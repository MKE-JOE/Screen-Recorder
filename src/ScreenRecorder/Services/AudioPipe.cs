using System.Diagnostics;
using System.IO.Pipes;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ScreenRecorder.Services;

/// <summary>
/// Captures one audio device with WASAPI (loopback for playback devices) and streams raw PCM
/// to FFmpeg through a named pipe. The stream is paced against the wall clock: gaps are filled
/// with silence and any excess is dropped, so audio stays in sync with the video for hours.
/// </summary>
public sealed class AudioPipe : IDisposable
{
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly MMDevice _device;
    private readonly IWaveIn _capture;
    private readonly WasapiOut? _silence;
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly List<byte[]> _queue = new();
    private Thread? _writer;
    private volatile bool _connected;

    public string PipePath { get; }
    public string SampleFormat { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public string? Error { get; private set; }

    private AudioPipe(MMDevice device, bool loopback)
    {
        _device = device;
        if (loopback)
        {
            _capture = new WasapiLoopbackCapture(device);
            // Loopback delivers nothing while the device is silent; playing silence keeps the stream continuous.
            try
            {
                _silence = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
                _silence.Init(new SilenceProvider(device.AudioClient.MixFormat));
            }
            catch { _silence?.Dispose(); _silence = null; }
        }
        else
        {
            _capture = new WasapiCapture(device, true, 50);
        }

        var wf = _capture.WaveFormat;
        SampleRate = wf.SampleRate;
        Channels = wf.Channels;
        bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat
            || (wf is WaveFormatExtensible ext && ext.SubFormat == SubtypeFloat);
        bool isPcm = wf.Encoding == WaveFormatEncoding.Pcm
            || (wf is WaveFormatExtensible ext2 && ext2.SubFormat == SubtypePcm);
        SampleFormat = (isFloat, isPcm, wf.BitsPerSample) switch
        {
            (true, _, 32) => "f32le",
            (true, _, 64) => "f64le",
            (_, true, 16) => "s16le",
            (_, true, 24) => "s24le",
            (_, true, 32) => "s32le",
            _ => throw new NotSupportedException($"Unsupported audio format: {wf}"),
        };

        string name = "ScreenRecorder_" + Guid.NewGuid().ToString("N");
        PipePath = @"\\.\pipe\" + name;
        _pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 1 << 20);

        _capture.DataAvailable += OnData;
    }

    /// <summary>Opens the device on a background (MTA) thread and starts capturing.</summary>
    public static Task<AudioPipe> CreateAsync(string deviceId, bool loopback) => Task.Run(() =>
    {
        using var en = new MMDeviceEnumerator();
        var device = en.GetDevice(deviceId);
        var ap = new AudioPipe(device, loopback);
        try
        {
            ap._silence?.Play();
            ap._capture.StartRecording();
            ap._writer = new Thread(ap.WriterLoop) { IsBackground = true, Name = "AudioPipe", Priority = ThreadPriority.AboveNormal };
            ap._writer.Start();
            return ap;
        }
        catch
        {
            ap.Dispose();
            throw;
        }
    });

    /// <summary>FFmpeg input arguments for this pipe.</summary>
    public IEnumerable<string> InputArgs(double timeOrigin) => new[]
    {
        "-thread_queue_size", "4096",
        "-use_wallclock_as_timestamps", "1",
        "-itsoffset", "-" + Ffmpeg.Num(timeOrigin),
        "-f", SampleFormat, "-ar", SampleRate.ToString(), "-ac", Channels.ToString(),
        "-i", PipePath,
    };

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (!_connected || e.BytesRecorded <= 0) return;
        var copy = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);
        lock (_lock) _queue.Add(copy);
    }

    private void WriterLoop()
    {
        try
        {
            _pipe.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();
        }
        catch { return; }

        int block = _capture.WaveFormat.BlockAlign;
        long bytesPerSec = (long)SampleRate * block;
        long tolerance = bytesPerSec * 80 / 1000 / block * block;   // 80 ms
        var silence = new byte[bytesPerSec / 10 / block * block];      // 100 ms of zeros
        var clock = Stopwatch.StartNew();
        long written = 0;
        lock (_lock) _queue.Clear();
        _connected = true;

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                long expected = (long)(clock.Elapsed.TotalSeconds * SampleRate) * block;

                byte[][] chunks;
                lock (_lock) { chunks = _queue.ToArray(); _queue.Clear(); }
                foreach (var chunk in chunks)
                {
                    int offset = 0;
                    long ahead = written + chunk.Length - expected;
                    if (ahead > tolerance)
                    {
                        // Device clock ran fast or data came in right after a silence fill: drop the excess.
                        offset = (int)Math.Min(chunk.Length, (ahead - tolerance / 2) / block * block);
                    }
                    if (offset < chunk.Length)
                    {
                        _pipe.Write(chunk, offset, chunk.Length - offset);
                        written += chunk.Length - offset;
                    }
                }

                long behind = expected - written;
                if (behind > tolerance)
                {
                    long fill = (behind - tolerance / 2) / block * block;
                    while (fill > 0)
                    {
                        int n = (int)Math.Min(fill, silence.Length);
                        _pipe.Write(silence, 0, n);
                        written += n; fill -= n;
                    }
                }
                Thread.Sleep(5);
            }
        }
        catch (IOException) { /* FFmpeg closed the pipe */ }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Error = ex.Message; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _capture.StopRecording(); } catch { }
        try { _silence?.Stop(); } catch { }
        try { _pipe.Dispose(); } catch { }
        _writer?.Join(2000);
        try { _capture.Dispose(); } catch { }
        try { _silence?.Dispose(); } catch { }
        try { _device.Dispose(); } catch { }
        _cts.Dispose();
    }
}
