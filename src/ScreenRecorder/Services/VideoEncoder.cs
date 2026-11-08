using System.Text;

namespace ScreenRecorder.Services;

/// <summary>
/// Picks the H.264 encoder used while recording. A graphics-card encoder (NVIDIA, Intel or AMD) keeps up with
/// fast-moving 1080p content without loading the CPU; when none works, a fast libx264 setting is used.
/// If the encoder can't keep up, frames arrive late and the recording stutters, so this matters.
/// </summary>
/// <param name="GpuFilter">
/// Filters that turn Desktop Duplication's on-GPU frames into something this encoder takes, so frames never
/// leave the graphics card ("" = the encoder takes them as they are; null = not supported).
/// </param>
public sealed record VideoEncoder(string Name, string PixelFormat, Func<int, string[]> Args, string? GpuFilter = null)
{
    public override string ToString() => Name + (ZeroCopy ? ", on the GPU" : "");

    /// <summary>Set once detection has confirmed frames can stay on the GPU all the way to this encoder.</summary>
    public bool ZeroCopy { get; init; }

    /// <summary>Other ways to keep frames on the GPU, tried in order when <see cref="GpuFilter"/> doesn't work.</summary>
    public string[] OtherGpuFilters { get; init; } = Array.Empty<string>();

    private static readonly VideoEncoder[] HardwareChoices =
    {
        new("NVIDIA (NVENC)", "nv12", fps => new[]
        {
            "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", "19", "-b:v", "0",
            "-g", (fps * 2).ToString(),
        }, ""),
        new("Intel Quick Sync", "nv12", fps => new[]
        {
            "-c:v", "h264_qsv", "-preset", "faster", "-global_quality", "20", "-look_ahead", "0",
            "-g", (fps * 2).ToString(),
        }, "hwmap=derive_device=qsv,format=qsv,vpp_qsv=format=nv12")
        {
            OtherGpuFilters = new[] { "hwmap=derive_device=qsv,format=qsv,scale_qsv=format=nv12" },
        },
        new("AMD (AMF)", "nv12", fps => new[]
        {
            "-c:v", "h264_amf", "-usage", "transcoding", "-quality", "balanced",
            "-rc", "cqp", "-qp_i", "18", "-qp_p", "20", "-qp_b", "22", "-g", (fps * 2).ToString(),
        }, ""),
    };

    public static readonly VideoEncoder Software = new("CPU (x264)", "yuv420p", fps => new[]
    {
        "-c:v", "libx264", "-preset", "superfast", "-crf", "18", "-g", (fps * 2).ToString(),
    });

    private static Task<VideoEncoder>? _best;

    /// <summary>The first encoder that actually works on this PC (tested once with a tiny encode).</summary>
    public static Task<VideoEncoder> BestAsync() => _best ??= DetectAsync();

    /// <summary>What detection tried and why each attempt failed, kept for troubleshooting.</summary>
    public static string CheckLogPath => Path.Combine(Ffmpeg.AppDataDir, "encoder-check.log");

    private static async Task<VideoEncoder> DetectAsync()
    {
        var log = new StringBuilder($"Encoder check {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
        try
        {
            foreach (var enc in HardwareChoices)
            {
                // Best: capture and encode entirely on the graphics card. Without it every frame is copied off the
                // card and converted on the CPU, and a busy card (playing video, say) makes that copy wait.
                if (enc.GpuFilter != null)
                {
                    foreach (var filter in new[] { enc.GpuFilter }.Concat(enc.OtherGpuFilters))
                    {
                        if (await WorksAsync(new[]
                            {
                                "-filter_complex", "ddagrab=output_idx=0:framerate=30" + (filter.Length > 0 ? "," + filter : "") + "[v]",
                                "-map", "[v]", "-frames:v", "15",
                            }, enc.Args(30), $"{enc.Name}, on the GPU ({(filter.Length > 0 ? filter : "no conversion")})", log))
                            return enc with { GpuFilter = filter, ZeroCopy = true };
                    }
                }

                // Next best: frames are converted on the CPU, encoded on the graphics card.
                if (await WorksAsync(new[]
                    {
                        "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30", "-frames:v", "15", "-vf", "format=" + enc.PixelFormat,
                    }, enc.Args(30), enc.Name, log))
                    return enc;
            }
            return Software;
        }
        finally
        {
            try { File.WriteAllText(CheckLogPath, log.ToString()); } catch { }
        }
    }

    private static async Task<bool> WorksAsync(IEnumerable<string> input, IEnumerable<string> codec, string label, StringBuilder log)
    {
        try
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error" };
            args.AddRange(input);
            args.AddRange(codec);
            args.AddRange(new[] { "-f", "null", "-" });
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await Ffmpeg.RunAsync(args, null, 0, null, cts.Token);
            log.AppendLine($"OK      {label}");
            return true;
        }
        catch (Exception ex)
        {
            string why = ex switch
            {
                OperationCanceledException => "took longer than 15 s",
                FfmpegException f => f.Details.Trim(),
                _ => ex.Message,
            };
            log.AppendLine($"FAILED  {label}");
            foreach (var line in why.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(6))
                log.Append("        ").AppendLine(line.TrimEnd());
            return false;
        }
    }
}
