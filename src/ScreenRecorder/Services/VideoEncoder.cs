namespace ScreenRecorder.Services;

/// <summary>
/// Picks the H.264 encoder used while recording. A graphics-card encoder (NVIDIA, Intel or AMD) keeps up with
/// fast-moving 1080p content without loading the CPU; when none works, a fast libx264 setting is used.
/// If the encoder can't keep up, frames arrive late and the recording stutters, so this matters.
/// </summary>
public sealed record VideoEncoder(string Name, string PixelFormat, Func<int, string[]> Args)
{
    public override string ToString() => Name;

    private static readonly VideoEncoder[] HardwareChoices =
    {
        new("NVIDIA (NVENC)", "nv12", fps => new[]
        {
            "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", "19", "-b:v", "0",
            "-g", (fps * 2).ToString(),
        }),
        new("Intel Quick Sync", "nv12", fps => new[]
        {
            "-c:v", "h264_qsv", "-preset", "faster", "-global_quality", "20", "-look_ahead", "0",
            "-g", (fps * 2).ToString(),
        }),
        new("AMD (AMF)", "nv12", fps => new[]
        {
            "-c:v", "h264_amf", "-usage", "transcoding", "-quality", "balanced",
            "-rc", "cqp", "-qp_i", "18", "-qp_p", "20", "-qp_b", "22", "-g", (fps * 2).ToString(),
        }),
    };

    public static readonly VideoEncoder Software = new("CPU (x264)", "yuv420p", fps => new[]
    {
        "-c:v", "libx264", "-preset", "superfast", "-crf", "18", "-g", (fps * 2).ToString(),
    });

    private static Task<VideoEncoder>? _best;

    /// <summary>The first encoder that actually works on this PC (tested once with a tiny encode).</summary>
    public static Task<VideoEncoder> BestAsync() => _best ??= DetectAsync();

    private static async Task<VideoEncoder> DetectAsync()
    {
        foreach (var enc in HardwareChoices)
        {
            try
            {
                var args = new List<string>
                {
                    "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30",
                    "-frames:v", "15", "-vf", "format=" + enc.PixelFormat,
                };
                args.AddRange(enc.Args(30));
                args.AddRange(new[] { "-f", "null", "-" });
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await Ffmpeg.RunAsync(args, null, 0, null, cts.Token);
                return enc;
            }
            catch { /* not available on this PC */ }
        }
        return Software;
    }
}
