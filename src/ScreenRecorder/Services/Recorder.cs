using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ScreenRecorder.Services;

public sealed class RecordingOptions
{
    public required CaptureTarget Target { get; init; }
    public int Fps { get; init; } = 30;
    public string? SystemAudioDeviceId { get; init; }
    public string? MicrophoneDeviceId { get; init; }
    /// <summary>H.264 encoder for the live recording (graphics card when available).</summary>
    public VideoEncoder Encoder { get; init; } = VideoEncoder.Software;
}

/// <summary>
/// Records the screen with FFmpeg (Desktop Duplication, falling back to GDI) plus WASAPI audio.
/// Each pause closes the current segment; Stop joins the segments into one MP4.
/// Video is H.264 (near-lossless CRF 18), audio is AAC 320 kbps 48 kHz stereo.
/// </summary>
public sealed class Recorder : IAsyncDisposable
{
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(2);

    private readonly RecordingOptions _opt;
    private readonly string _workDir;
    private readonly List<string> _segments = new();
    private readonly List<AudioPipe> _pipes = new();
    private readonly Stopwatch _segmentClock = new();
    private TimeSpan _completed = TimeSpan.Zero;
    private Process? _ffmpeg;
    private Task? _stderrTask, _stdoutTask;
    private readonly StringBuilder _log = new();
    private bool _useGdi;
    private bool _stopping;
    private TaskCompletionSource<bool>? _firstFrame;

    /// <summary>Raised (on a background thread) if FFmpeg stops on its own while recording.</summary>
    public event Action<string>? Failed;

    public bool IsPaused { get; private set; }
    public TimeSpan Elapsed => _completed + _segmentClock.Elapsed;
    public string LogPath => Path.Combine(_workDir, "ffmpeg.log");

    public Recorder(RecordingOptions options, string workDir)
    {
        _opt = options;
        _workDir = workDir;
        Directory.CreateDirectory(_workDir);
        _useGdi = options.Target.DdaOutputIndex == null;
    }

    public async Task StartAsync()
    {
        try
        {
            await StartSegmentAsync();
        }
        catch when (!_useGdi)
        {
            // Desktop Duplication can fail (some remote sessions, hybrid GPUs). Try plain GDI capture.
            _useGdi = true;
            await StartSegmentAsync();
        }
    }

    public async Task PauseAsync()
    {
        if (IsPaused) return;
        await StopSegmentAsync();
        IsPaused = true;
    }

    public async Task ResumeAsync()
    {
        if (!IsPaused) return;
        await StartSegmentAsync();
        IsPaused = false;
    }

    /// <summary>Stops recording and writes the final MP4.</summary>
    public async Task<string> FinishAsync(string outputPath)
    {
        await StopSegmentAsync();
        var parts = _segments.Where(s => File.Exists(s) && new FileInfo(s).Length > 1024).ToList();
        if (parts.Count == 0) throw new InvalidOperationException("Nothing was recorded." + Environment.NewLine + TailLog());

        string list = Path.Combine(_workDir, "parts.txt");
        File.WriteAllLines(list, parts.Select(p => "file '" + p.Replace("'", "'\\''") + "'"));
        await Ffmpeg.RunAsync(new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "concat", "-safe", "0", "-i", list,
            "-map", "0", "-c", "copy", "-movflags", "+faststart", outputPath,
        }, _workDir, 0, null, CancellationToken.None);

        try { Directory.Delete(_workDir, true); } catch { }
        return outputPath;
    }

    private async Task StartSegmentAsync()
    {
        _stopping = false;
        string seg = Path.Combine(_workDir, $"part{_segments.Count:000}.mkv");

        foreach (var p in _pipes) p.Dispose();
        _pipes.Clear();
        if (_opt.SystemAudioDeviceId != null)
            _pipes.Add(await AudioPipe.CreateAsync(_opt.SystemAudioDeviceId, loopback: true));
        if (_opt.MicrophoneDeviceId != null)
            _pipes.Add(await AudioPipe.CreateAsync(_opt.MicrophoneDeviceId, loopback: false));

        // Every input is stamped with wall-clock time relative to this origin, which keeps audio and video aligned.
        double origin = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var args = BuildArgs(seg, origin);

        _firstFrame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ffmpeg = Ffmpeg.Start(args, _workDir);
        _ffmpeg.EnableRaisingEvents = true;
        Native.Performance.DisableThrottling(_ffmpeg);
        var proc = _ffmpeg;
        _stderrTask = Task.Run(() => PumpStderr(proc));
        _stdoutTask = Task.Run(() => PumpProgress(proc));
        proc.Exited += (_, _) =>
        {
            var ff = _firstFrame;
            bool wasRunning = ff != null && ff.Task.IsCompletedSuccessfully && ff.Task.Result;
            ff?.TrySetResult(false);
            if (!_stopping && wasRunning) Failed?.Invoke("Recording stopped unexpectedly." + Environment.NewLine + TailLog());
        };

        var started = await Task.WhenAny(_firstFrame.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        if (started != _firstFrame.Task || !_firstFrame.Task.Result)
        {
            _stopping = true;
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
            foreach (var p in _pipes) p.Dispose();
            _pipes.Clear();
            throw new InvalidOperationException("Could not start screen capture." + Environment.NewLine + TailLog());
        }

        _segments.Add(seg);
        _segmentClock.Restart();
    }

    private async Task StopSegmentAsync()
    {
        var proc = _ffmpeg;
        if (proc == null) return;
        _stopping = true;
        _segmentClock.Stop();
        _completed += _segmentClock.Elapsed;
        _segmentClock.Reset();

        try
        {
            if (!proc.HasExited)
            {
                await proc.StandardInput.WriteAsync('q');   // graceful stop: FFmpeg finalises the file
                await proc.StandardInput.FlushAsync();
            }
        }
        catch { }

        var exited = proc.WaitForExitAsync();
        if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(20))) != exited)
        {
            try { proc.Kill(true); } catch { }
        }
        foreach (var p in _pipes) p.Dispose();
        _pipes.Clear();
        try { await Task.WhenAll(_stderrTask ?? Task.CompletedTask, _stdoutTask ?? Task.CompletedTask); } catch { }
        proc.Dispose();
        _ffmpeg = null;
        File.AppendAllText(LogPath, _log.ToString());
        _log.Clear();
    }

    private List<string> BuildArgs(string output, double origin)
    {
        var t = _opt.Target;
        int fps = _opt.Fps;
        long originUs = (long)Math.Round(origin * 1_000_000);
        var a = new List<string> { "-hide_banner", "-nostats", "-loglevel", "warning", "-progress", "pipe:1", "-y", "-copyts" };

        // Inputs: [GDI video], audio pipes...
        int audioBase = 0;
        if (_useGdi)
        {
            a.AddRange(new[]
            {
                "-thread_queue_size", "1024", "-itsoffset", "-" + Ffmpeg.Num(origin),
                "-f", "gdigrab", "-framerate", fps.ToString(), "-draw_mouse", "1",
                "-offset_x", t.Rect.Left.ToString(), "-offset_y", t.Rect.Top.ToString(),
                "-video_size", $"{t.Width}x{t.Height}", "-i", "desktop",
            });
            audioBase = 1;
        }
        foreach (var p in _pipes) a.AddRange(p.InputArgs(origin));

        var fc = new StringBuilder();
        string toYuv = $"scale=out_color_matrix=bt709:out_range=tv,format={_opt.Encoder.PixelFormat}";
        if (_useGdi)
        {
            fc.Append(CultureInfo.InvariantCulture, $"[0:v]settb=AVTB,setpts=PTS+{originUs}-RTCSTART,{toYuv}[v]");
        }
        else
        {
            fc.Append(CultureInfo.InvariantCulture,
                $"ddagrab=output_idx={t.DdaOutputIndex}:framerate={fps}:draw_mouse=1:video_size={t.Width}x{t.Height}:offset_x={t.DdaOffsetX}:offset_y={t.DdaOffsetY}");
            // Timestamps come from the moment Desktop Duplication grabbed each frame (its own PTS), mapped onto the
            // wall clock the audio uses. The offset between the two clocks is the smallest one seen so far (frames
            // that reached this filter without delay), allowed to creep up 20 µs per frame to follow clock drift.
            // Delays later in the pipeline therefore no longer shift frames and cause repeats or drops.
            // Times are counted from when the filters started (see the audio note below).
            fc.Append(CultureInfo.InvariantCulture,
                $",settb=AVTB,setpts='PTS+st(0,if(eq(N,0),RTCTIME-PTS,min(RTCTIME-PTS,ld(0)+20)))-RTCSTART'");
            if (_opt.Encoder.ZeroCopy)
            {
                if (!string.IsNullOrEmpty(_opt.Encoder.GpuFilter)) fc.Append(',').Append(_opt.Encoder.GpuFilter);
                fc.Append("[v]");
            }
            else fc.Append($",hwdownload,format=bgra,{toYuv}[v]");
        }

        // The recording starts when the filters start (RTCSTART), not when FFmpeg was launched: FFmpeg takes a second
        // or two to open everything, and that gap used to be filled by repeating a frame, a visible freeze near the
        // start of every recording and after every resume. Audio from before that moment is dropped so both line up.
        string toStereo = string.Create(CultureInfo.InvariantCulture,
            $"asettb=AVTB,asetpts='PTS+{originUs}-RTCSTART',atrim=start=0,asetpts=PTS,aresample=48000:async=1000:first_pts=0,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo");
        if (_pipes.Count == 1)
        {
            fc.Append($";[{audioBase}:a]{toStereo}[a]");
        }
        else if (_pipes.Count == 2)
        {
            fc.Append($";[{audioBase}:a]{toStereo}[a0];[{audioBase + 1}:a]{toStereo}[a1]");
            fc.Append(";[a0][a1]amix=inputs=2:duration=longest:normalize=0,alimiter=limit=0.98[a]");
        }

        a.AddRange(new[] { "-filter_complex", fc.ToString(), "-map", "[v]" });
        if (_pipes.Count > 0) a.AddRange(new[] { "-map", "[a]" });

        a.AddRange(new[]
        {
            "-fps_mode", "cfr", "-r", fps.ToString(),
        });
        a.AddRange(_opt.Encoder.Args(fps));
        bool gpuFrames = _opt.Encoder.ZeroCopy && !_useGdi;
        if (!gpuFrames) a.AddRange(new[] { "-pix_fmt", _opt.Encoder.PixelFormat });
        a.AddRange(new[]
        {
            "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
        });
        if (_pipes.Count > 0) a.AddRange(new[] { "-c:a", "aac", "-b:a", "320k", "-ar", "48000", "-ac", "2" });
        a.Add(output);
        return a;
    }

    private async Task PumpStderr(Process p)
    {
        try
        {
            string? line;
            while ((line = await p.StandardError.ReadLineAsync()) != null)
                lock (_log) { _log.AppendLine(line); if (_log.Length > 200_000) _log.Remove(0, 100_000); }
        }
        catch { }
    }

    // ------------------------------------------------------------------ smoothness

    private long _segFrames, _segDup, _segDrop, _doneFrames, _doneDup, _doneDrop;
    // Repeats in the first couple of seconds of a segment are start-up, not lag: they're left out.
    private long _segSkipDup = -1, _segSkipDrop;
    private DateTime _segStart;
    private readonly Queue<(DateTime At, long Frames, long Lag)> _history = new();

    /// <summary>Frames written, and frames FFmpeg had to repeat or drop because capture/encoding fell behind.</summary>
    public (long Frames, long Repeated, long Dropped) Health
    {
        get
        {
            lock (_history)
            {
                bool counted = _segSkipDup >= 0;
                return (_doneFrames + _segFrames,
                        _doneDup + (counted ? _segDup - _segSkipDup : 0),
                        _doneDrop + (counted ? _segDrop - _segSkipDrop : 0));
            }
        }
    }

    /// <summary>Share of frames over the last ~5 seconds that were repeated or dropped (0–1).</summary>
    public double RecentLag
    {
        get
        {
            lock (_history)
            {
                if (_history.Count < 2) return 0;
                var first = _history.Peek();
                var last = _history.Last();
                long frames = last.Frames - first.Frames, lag = last.Lag - first.Lag;
                return frames <= 0 ? 0 : Math.Clamp((double)lag / frames, 0, 1);
            }
        }
    }

    /// <summary>One line for the user: how smooth the whole recording was.</summary>
    public string HealthSummary()
    {
        var (frames, dup, drop) = Health;
        if (frames <= 0) return "";
        double pct = 100.0 * (dup + drop) / Math.Max(1, frames);
        return pct < 1 ? "smooth" : $"{pct:0.#}% of frames were repeated because the PC fell behind";
    }

    /// <summary>Saves a short smoothness report for the last recording (helps compare PCs and settings).</summary>
    public void WriteReport(string path, TimeSpan length)
    {
        try
        {
            var (frames, dup, drop) = Health;
            File.WriteAllText(path, string.Join(Environment.NewLine, new[]
            {
                $"Recorded: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"Length: {length:hh\\:mm\\:ss}",
                $"Area: {_opt.Target.Width}x{_opt.Target.Height} at {_opt.Fps} fps",
                $"Encoder: {_opt.Encoder}",
                $"Frames written: {frames}",
                $"Frames repeated (PC fell behind): {dup}",
                $"Frames dropped: {drop}",
                $"Result: {HealthSummary()}",
            }) + Environment.NewLine);
        }
        catch { }
    }

    private async Task PumpProgress(Process p)
    {
        lock (_history) { _segFrames = _segDup = _segDrop = 0; _segSkipDup = -1; _segSkipDrop = 0; _segStart = DateTime.UtcNow; _history.Clear(); }
        try
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
            {
                int eq = line.IndexOf('=');
                if (eq <= 0 || !long.TryParse(line.AsSpan(eq + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)) continue;
                string key = line[..eq];
                lock (_history)
                {
                    switch (key)
                    {
                        case "frame":
                            _segFrames = v;
                            if (v > 0) _firstFrame?.TrySetResult(true);
                            break;
                        case "dup_frames": _segDup = v; break;
                        case "drop_frames":
                            _segDrop = v;
                            if (DateTime.UtcNow - _segStart < TimeSpan.FromSeconds(2)) break;
                            if (_segSkipDup < 0) { _segSkipDup = _segDup; _segSkipDrop = _segDrop; }
                            // drop_frames is the last counter in each progress block: take a sample.
                            _history.Enqueue((DateTime.UtcNow, _segFrames, _segDup + _segDrop));
                            while (_history.Count > 2 && DateTime.UtcNow - _history.Peek().At > TimeSpan.FromSeconds(5)) _history.Dequeue();
                            break;
                    }
                }
            }
        }
        catch { }
        lock (_history)
        {
            if (_segSkipDup >= 0) { _doneDup += _segDup - _segSkipDup; _doneDrop += _segDrop - _segSkipDrop; }
            _doneFrames += _segFrames;
            _segFrames = _segDup = _segDrop = 0;
            _segSkipDup = -1;
            _history.Clear();
        }
    }

    private string TailLog()
    {
        string text;
        lock (_log) text = _log.ToString();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("\n", lines.TakeLast(12));
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopSegmentAsync(); } catch { }
    }
}
