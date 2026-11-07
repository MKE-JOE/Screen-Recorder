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

        // Each part's video starts a second or two after its audio (FFmpeg start-up). Cut that lead-in so picture
        // and sound start together, without a frozen frame at the start of the recording or after a resume.
        for (int i = 0; i < parts.Count; i++)
        {
            double lead = await VideoStartAsync(parts[i]);
            if (lead < 0.02) continue;
            string trimmed = Path.ChangeExtension(parts[i], ".trim.mkv");
            try
            {
                await Ffmpeg.RunAsync(new[]
                {
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-ss", Ffmpeg.Num(lead), "-i", parts[i], "-map", "0", "-c", "copy", trimmed,
                }, _workDir, 0, null, CancellationToken.None);
                if (new FileInfo(trimmed).Length > 1024) parts[i] = trimmed;
            }
            catch { /* keep the untrimmed part */ }
        }

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

    /// <summary>When the video stream of a recorded part starts, in seconds (0 if it can't be read).</summary>
    private async Task<double> VideoStartAsync(string part)
    {
        try
        {
            using var p = Ffmpeg.Start(new[]
            {
                "-hide_banner", "-loglevel", "error", "-i", part, "-map", "0:v:0", "-c", "copy", "-frames:v", "8", "-f", "framemd5", "-",
            }, _workDir);
            var errTask = p.StandardError.ReadToEndAsync();
            string output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            await errTask;

            double tbNum = 1, tbDen = 1000;
            long? first = null;
            foreach (var raw in output.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("#tb 0:", StringComparison.Ordinal))
                {
                    var tb = line[6..].Trim().Split('/');
                    if (tb.Length == 2) { tbNum = double.Parse(tb[0], CultureInfo.InvariantCulture); tbDen = double.Parse(tb[1], CultureInfo.InvariantCulture); }
                }
                else if (line.Length > 0 && line[0] != '#')
                {
                    var cols = line.Split(',');
                    if (cols.Length > 2 && long.TryParse(cols[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long pts))
                        first = first is { } f ? Math.Min(f, pts) : pts;
                }
            }
            return first is { } v && v > 0 ? v * tbNum / tbDen : 0;
        }
        catch { return 0; }
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
        foreach (var p in _pipes) p.SetOrigin(origin);
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

        // Video and audio run in separate filter graphs. In a shared graph FFmpeg only asks Desktop Duplication for
        // a frame when audio arrives, and audio from the pipes arrives in bursts, so most frames came out as repeats.
        // Both use the same time zero (origin): video by capture time, audio by sample count.
        var fc = new StringBuilder();
        var afc = new StringBuilder();
        string toYuv = $"scale=out_color_matrix=bt709:out_range=tv,format={_opt.Encoder.PixelFormat}";
        if (_useGdi)
        {
            fc.Append($"[0:v]{toYuv}[v]");
        }
        else
        {
            fc.Append(CultureInfo.InvariantCulture,
                $"ddagrab=output_idx={t.DdaOutputIndex}:framerate={fps}:draw_mouse=1:video_size={t.Width}x{t.Height}:offset_x={t.DdaOffsetX}:offset_y={t.DdaOffsetY}");
            // Timestamps come from the moment Desktop Duplication grabbed each frame (its own PTS), mapped onto the
            // wall clock. The offset between the two clocks is the smallest one seen so far (frames that reached this
            // filter without delay), allowed to creep up 20 µs per frame to follow clock drift.
            fc.Append(CultureInfo.InvariantCulture,
                $",settb=AVTB,setpts='PTS+st(0,if(eq(N,0),RTCTIME-PTS,min(RTCTIME-PTS,ld(0)+20)))-{originUs}'");
            if (_opt.Encoder.ZeroCopy)
            {
                if (!string.IsNullOrEmpty(_opt.Encoder.GpuFilter)) fc.Append(',').Append(_opt.Encoder.GpuFilter);
                fc.Append("[v]");
            }
            else fc.Append($",hwdownload,format=bgra,{toYuv}[v]");
        }

        const string toStereo = "aresample=48000:async=1000:first_pts=0,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo";
        if (_pipes.Count == 1)
        {
            afc.Append($"[{audioBase}:a]{toStereo}[a]");
        }
        else if (_pipes.Count == 2)
        {
            afc.Append($"[{audioBase}:a]{toStereo}[a0];[{audioBase + 1}:a]{toStereo}[a1]");
            afc.Append(";[a0][a1]amix=inputs=2:duration=longest:normalize=0,alimiter=limit=0.98[a]");
        }

        a.AddRange(new[] { "-filter_complex", fc.ToString() });
        if (afc.Length > 0) a.AddRange(new[] { "-filter_complex", afc.ToString() });
        a.AddRange(new[] { "-map", "[v]" });
        if (_pipes.Count > 0) a.AddRange(new[] { "-map", "[a]" });

        // Frames keep their capture times. FFmpeg's constant-frame-rate mode would fill the second or two before the
        // first frame (FFmpeg start-up) with a repeated frame; FinishAsync trims that lead-in instead.
        a.AddRange(new[] { "-fps_mode", "passthrough" });
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

    private long _segFrames, _doneFrames;
    private double _doneExpected, _doneCounted;
    // The first couple of seconds of a segment are start-up and are left out of the measurement.
    private (DateTime At, long Frames)? _segBase;
    private (DateTime At, long Frames) _segLast;
    private DateTime _segStart;
    private readonly Queue<(DateTime At, long Frames)> _history = new();

    /// <summary>Frames written, and frames that should have been captured in that time but weren't (the PC fell behind).</summary>
    public (long Frames, long Missed) Health
    {
        get
        {
            lock (_history)
            {
                double expected = _doneExpected, got = _doneCounted;
                if (_segBase is { } b)
                {
                    expected += (_segLast.At - b.At).TotalSeconds * _opt.Fps;
                    got += _segLast.Frames - b.Frames;
                }
                return (_doneFrames + _segFrames, (long)Math.Max(0, Math.Round(expected - got)));
            }
        }
    }

    /// <summary>Share of frames missed over the last ~5 seconds (0–1).</summary>
    public double RecentLag
    {
        get
        {
            lock (_history)
            {
                if (_history.Count < 2) return 0;
                var first = _history.Peek();
                var last = _history.Last();
                double expected = (last.At - first.At).TotalSeconds * _opt.Fps;
                if (expected < _opt.Fps) return 0;   // under a second of data
                return Math.Clamp(1 - (last.Frames - first.Frames) / expected, 0, 1);
            }
        }
    }

    /// <summary>One line for the user: how smooth the whole recording was.</summary>
    public string HealthSummary()
    {
        var (frames, missed) = Health;
        if (frames <= 0) return "";
        double pct = 100.0 * missed / Math.Max(1, frames + missed);
        return pct < 2 ? "smooth" : $"{pct:0.#}% of frames were missed because the PC fell behind";
    }

    /// <summary>Saves a short smoothness report for the last recording (helps compare PCs and settings).</summary>
    public void WriteReport(string path, TimeSpan length)
    {
        try
        {
            var (frames, missed) = Health;
            File.WriteAllText(path, string.Join(Environment.NewLine, new[]
            {
                $"Recorded: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"Length: {length:hh\\:mm\\:ss}",
                $"Area: {_opt.Target.Width}x{_opt.Target.Height} at {_opt.Fps} fps",
                $"Encoder: {_opt.Encoder}",
                $"Frames written: {frames}",
                $"Frames missed (PC fell behind): {missed}",
                $"Result: {HealthSummary()}",
            }) + Environment.NewLine);
        }
        catch { }
    }

    private async Task PumpProgress(Process p)
    {
        lock (_history) { _segFrames = 0; _segBase = null; _segStart = DateTime.UtcNow; _history.Clear(); }
        try
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
            {
                if (!line.StartsWith("frame=", StringComparison.Ordinal)
                    || !long.TryParse(line.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)) continue;
                if (v > 0) _firstFrame?.TrySetResult(true);
                var now = DateTime.UtcNow;
                lock (_history)
                {
                    _segFrames = v;
                    if (now - _segStart < TimeSpan.FromSeconds(2)) continue;
                    _segBase ??= (now, v);
                    _segLast = (now, v);
                    _history.Enqueue((now, v));
                    while (_history.Count > 2 && now - _history.Peek().At > TimeSpan.FromSeconds(5)) _history.Dequeue();
                }
            }
        }
        catch { }
        lock (_history)
        {
            if (_segBase is { } b)
            {
                _doneExpected += (_segLast.At - b.At).TotalSeconds * _opt.Fps;
                _doneCounted += _segLast.Frames - b.Frames;
            }
            _doneFrames += _segFrames;
            _segFrames = 0;
            _segBase = null;
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
