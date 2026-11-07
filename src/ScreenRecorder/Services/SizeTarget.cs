using System.Globalization;
using System.Text.RegularExpressions;

namespace ScreenRecorder.Services;

/// <summary>What FFmpeg reports about a video file.</summary>
/// <summary>Bitrates for a size-targeted encode.</summary>
public sealed record Budget(int VideoKbps, int AudioKbps);

public sealed record VideoInfo(int Width, int Height, double Duration, double Fps, bool HasAudio, long Bytes);

/// <summary>
/// The "File size" slider: pick a percentage of the original size and the video is encoded (two-pass) to hit it.
/// A floor keeps the result from looking bad: video never below 25% of the original bitrate or a small minimum
/// bitrate for the output resolution and frame rate.
/// </summary>
public static class SizeTarget
{
    public const double MinPercent = 25;
    public const int AudioKbps = 320;
    /// <summary>
    /// Lowest video bitrate we ever aim for. The main rule is 25% of the original's video bitrate: the original was
    /// recorded at CRF 18, so its bitrate already reflects how busy the content is (a still screen needs very little).
    /// </summary>
    private const double MinVideoKbps = 150;
    /// <summary>Container overhead allowance so the file lands at or just under the target.</summary>
    private const double Overhead = 0.98;

    public static async Task<VideoInfo?> ProbeAsync(string path)
    {
        try
        {
            using var p = Ffmpeg.Start(new[] { "-hide_banner", "-i", path });
            string info = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var v = Regex.Match(info, @"Video:.*?, (\d{2,5})x(\d{2,5})");
            if (!v.Success) return null;
            double dur = 0;
            var d = Regex.Match(info, @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
            if (d.Success)
                dur = int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60
                    + double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture);
            var f = Regex.Match(info, @"Video:.*?, (\d+(?:\.\d+)?) fps");
            double fps = f.Success ? double.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture) : 30;
            return new VideoInfo(int.Parse(v.Groups[1].Value), int.Parse(v.Groups[2].Value), dur, fps,
                info.Contains("Audio:", StringComparison.Ordinal), new FileInfo(path).Length);
        }
        catch { return null; }
    }

    /// <summary>Bytes the original file spends on this part of the clip.</summary>
    public static double SourceBytes(VideoInfo src, double length) =>
        src.Duration > 0 ? src.Bytes * Math.Min(1, length / src.Duration) : src.Bytes;

    /// <summary>
    /// Smallest percentage the slider allows (25–100). The video part may drop to 25% of its original bitrate, but
    /// never below 150 kbps; audio steps down from 320k to no less than 160k AAC.
    /// </summary>
    public static double FloorPercent(VideoInfo src, double length, int outW, int outH)
    {
        double srcKbps = SourceBytes(src, length) * 8 / 1000 / Math.Max(0.1, length);
        double audioSrc = src.HasAudio ? Math.Min(AudioKbps, srcKbps * 0.5) : 0;
        double videoSrc = Math.Max(1, srcKbps - audioSrc);
        double minVideo = Math.Max(videoSrc * MinPercent / 100, MinVideoKbps);
        double total = minVideo;
        if (src.HasAudio) { total += 160; total = minVideo + AudioFor(total); }
        double pct = total / Overhead / srcKbps * 100;
        return Math.Clamp(Math.Ceiling(Math.Max(MinPercent, pct)), MinPercent, 100);
    }

    /// <summary>AAC bitrate for a total budget: full 320k unless it would take more than a quarter of the file.</summary>
    private static int AudioFor(double totalKbps) => totalKbps >= 1280 ? 320 : totalKbps >= 700 ? 192 : 160;

    /// <summary>Video and audio bitrates that make the whole file about <paramref name="targetBytes"/>.</summary>
    public static Budget Plan(double targetBytes, double length, bool hasAudio)
    {
        double total = targetBytes * Overhead * 8 / 1000 / Math.Max(0.1, length);
        int audio = hasAudio ? AudioFor(total) : AudioKbps;
        return new Budget(Math.Max(100, (int)total - (hasAudio ? audio : 0)), audio);
    }

    public static string FormatBytes(double bytes) =>
        bytes >= 1024 * 1024 * 1024 ? $"{bytes / 1024 / 1024 / 1024:0.00} GB"
        : bytes >= 1024 * 1024 ? $"{bytes / 1024 / 1024:0.0} MB"
        : $"{Math.Max(1, bytes / 1024):0} KB";

    /// <summary>
    /// Encodes H.264 + AAC. <paramref name="inputArgs"/> holds the inputs, filters and -map options.
    /// With <paramref name="budget"/> null it uses CRF 18 (best quality); otherwise a two-pass encode at those bitrates.
    /// </summary>
    public static async Task EncodeAsync(IReadOnlyList<string> inputArgs, string output, ExportFormat format, Budget? budget,
        double length, string work, IProgress<double>? progress, CancellationToken ct)
    {
        var common = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error", "-progress", "pipe:1", "-y" };
        var color = new[] { "-pix_fmt", "yuv420p", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709" };
        var audio = new[] { "-c:a", "aac", "-b:a", (budget?.AudioKbps ?? AudioKbps) + "k", "-ar", "48000", "-ac", "2" };
        var mux = format == ExportFormat.Mp4 ? new[] { "-movflags", "+faststart" } : Array.Empty<string>();

        if (budget is not { VideoKbps: var kbps })
        {
            var a = new List<string>(common); a.AddRange(inputArgs);
            a.AddRange(new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "18" });
            a.AddRange(color); a.AddRange(audio); a.AddRange(mux); a.Add(output);
            await Ffmpeg.RunAsync(a, work, length, progress, ct);
            return;
        }

        string log = Path.Combine(work, "x264pass");
        var rate = new[] { "-c:v", "libx264", "-preset", "medium", "-b:v", kbps + "k",
            "-maxrate", kbps * 2 + "k", "-bufsize", kbps * 4 + "k", "-passlogfile", log };

        var p1 = new List<string>(common); p1.AddRange(inputArgs);
        p1.AddRange(rate); p1.AddRange(new[] { "-pass", "1" }); p1.AddRange(color);
        p1.AddRange(new[] { "-an", "-f", "null", "-" });
        await Ffmpeg.RunAsync(p1, work, length, Scale(progress, 0, 0.45), ct);

        var p2 = new List<string>(common); p2.AddRange(inputArgs);
        p2.AddRange(rate); p2.AddRange(new[] { "-pass", "2" }); p2.AddRange(color);
        p2.AddRange(audio); p2.AddRange(mux); p2.Add(output);
        await Ffmpeg.RunAsync(p2, work, length, Scale(progress, 0.45, 0.55), ct);
    }

    private static IProgress<double>? Scale(IProgress<double>? p, double offset, double span) =>
        p == null ? null : new Progress<double>(v => p.Report(offset + v * span));
}
