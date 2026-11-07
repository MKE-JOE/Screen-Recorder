using System.Globalization;
using System.Text.RegularExpressions;

namespace ScreenRecorder.Services;

/// <summary>Bitrates for a size-targeted encode.</summary>
public sealed record Budget(int VideoKbps, int AudioKbps, int Gop, double TargetBytes);

/// <summary>What FFmpeg reports about a video file. Stream bitrates are null when the container doesn't say (MKV).</summary>
public sealed record VideoInfo(int Width, int Height, double Duration, double Fps, bool HasAudio, long Bytes,
    double? VideoKbps = null, double? AudioKbps = null);

/// <summary>
/// The "File size" slider: pick a percentage of the original size and the video is encoded (two-pass) to hit it.
/// A floor keeps the result from looking bad: video never below 25% of the original video bitrate.
/// </summary>
public static class SizeTarget
{
    public const double MinPercent = 25;
    public const int AudioKbps = 320;
    /// <summary>
    /// Lowest video bitrate we ever aim for. The main rule is 25% of the original's video bitrate: the original was
    /// recorded at CRF 18, so its bitrate already reflects how busy the content is (a still screen needs very little).
    /// </summary>
    private const double MinVideoKbps = 50;
    /// <summary>
    /// Keyframes keep a fixed cost however still the screen is, so very low bitrates make text soft. This keeps
    /// at least 0.05 bits per pixel per second (about 100 kbps at 1080p), which measured visually lossless
    /// (SSIM 0.996) on a still 1080p60 clip, while 25% of that bitrate measured clearly soft (0.94).
    /// </summary>
    private const double MinBitsPerPixelSecond = 0.05;
    /// <summary>Small safety margin so the file lands at or just under the target.</summary>
    private const double Overhead = 0.99;

    /// <summary>
    /// MP4/MKV index and packet headers cost a roughly fixed amount per frame, which matters at low bitrates
    /// (about 13 kbps for 60 fps with audio).
    /// </summary>
    private static double ContainerKbps(VideoInfo src) => (Math.Min(src.Fps, 120) * 20 + (src.HasAudio ? 47 * 10 : 0)) * 8 / 1000.0;

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
            bool hasAudio = info.Contains("Audio:", StringComparison.Ordinal);
            var vk = Regex.Match(info, @"Video:[^\n]*?(\d+) kb/s");
            var ak = Regex.Match(info, @"Audio:[^\n]*?(\d+) kb/s");
            double? videoKbps = vk.Success ? double.Parse(vk.Groups[1].Value, CultureInfo.InvariantCulture) : null;
            double? audioKbps = ak.Success ? double.Parse(ak.Groups[1].Value, CultureInfo.InvariantCulture) : hasAudio ? null : 0;
            if ((videoKbps == null || audioKbps == null) && dur > 0)
            {
                // MKV doesn't list stream bitrates: read through the file (stream copy, very fast) and total them up.
                using var q = Ffmpeg.Start(new[] { "-hide_banner", "-nostats", "-i", path, "-map", "0", "-c", "copy", "-f", "null", "-" });
                string stats = await q.StandardError.ReadToEndAsync();
                await q.WaitForExitAsync();
                var sz = Regex.Match(stats, @"video:(\d+)\s*[kK]i?B\s+audio:(\d+)\s*[kK]i?B");
                if (sz.Success)
                {
                    videoKbps = double.Parse(sz.Groups[1].Value, CultureInfo.InvariantCulture) * 1024 * 8 / 1000 / dur;
                    audioKbps = double.Parse(sz.Groups[2].Value, CultureInfo.InvariantCulture) * 1024 * 8 / 1000 / dur;
                }
            }
            return new VideoInfo(int.Parse(v.Groups[1].Value), int.Parse(v.Groups[2].Value), dur, fps,
                hasAudio, new FileInfo(path).Length, videoKbps, audioKbps);
        }
        catch { return null; }
    }

    /// <summary>Bytes the original file spends on this part of the clip.</summary>
    public static double SourceBytes(VideoInfo src, double length) =>
        src.Duration > 0 ? src.Bytes * Math.Min(1, length / src.Duration) : src.Bytes;

    /// <summary>
    /// The original's video and audio bitrates (kbps). Uses what the file reports; otherwise splits the overall
    /// bitrate with a modest guess for audio. Silent recordings have almost no audio data, so this matters.
    /// </summary>
    public static (double Video, double Audio) Rates(VideoInfo src)
    {
        double total = src.Duration > 0 ? src.Bytes * 8 / 1000.0 / src.Duration : 1000;
        double audio = !src.HasAudio ? 0 : src.AudioKbps ?? Math.Min(AudioKbps, total * 0.3);
        double video = src.VideoKbps ?? Math.Max(1, total - audio);
        return (Math.Max(1, video), audio);
    }

    /// <summary>
    /// Smallest percentage the slider allows (25–100): video down to 25% of its original bitrate (but keeping
    /// enough for sharp keyframes, see MinBitsPerPixelSecond),
    /// audio down to 160k AAC (or less if the original audio already used less, e.g. silence).
    /// <paramref name="pixelFactor"/> scales the video bitrate for a crop/resize in the editor.
    /// </summary>
    public static double FloorPercent(VideoInfo src, double pixelFactor = 1, int outW = 0, int outH = 0)
    {
        var (video, audio) = Rates(src);
        video *= pixelFactor;
        double pixels = outW > 0 && outH > 0 ? (double)outW * outH : (double)src.Width * src.Height;
        double safety = Math.Max(MinVideoKbps, MinBitsPerPixelSecond * pixels / 1000);
        // A clip that's already below the safety level can still shrink a little.
        double minVideo = Math.Max(video * MinPercent / 100, Math.Min(safety, video * 0.6));
        double minAudio = Math.Min(160, audio);
        double c = ContainerKbps(src);
        double pct = (minVideo + minAudio + c) / Overhead / (video + audio + c) * 100;
        return Math.Clamp(Math.Ceiling(Math.Max(MinPercent, pct)), MinPercent, 100);
    }

    /// <summary>Overall bitrate at full quality (kbps), with the video part scaled by <paramref name="pixelFactor"/>.</summary>
    public static double FullKbps(VideoInfo src, double pixelFactor = 1)
    {
        var (video, audio) = Rates(src);
        return video * pixelFactor + audio + ContainerKbps(src);
    }

    /// <summary>AAC bitrate for a total budget: full 320k unless it would take more than a quarter of the file.</summary>
    private static int AudioFor(double totalKbps) => totalKbps >= 1280 ? 320 : totalKbps >= 700 ? 192 : 160;

    /// <summary>Video and audio bitrates that make the whole file about <paramref name="targetBytes"/>.</summary>
    public static Budget Plan(double targetBytes, double length, VideoInfo src)
    {
        double total = targetBytes * Overhead * 8 / 1000 / Math.Max(0.1, length) - ContainerKbps(src);
        int audio = src.HasAudio ? AudioFor(total) : AudioKbps;
        // Audio can't use more than it had: silence stays tiny, so the video gets the rest.
        double audioUse = src.HasAudio ? Math.Min(audio, Rates(src).Audio) : 0;
        // A keyframe every 10 s instead of every ~4 s: screen content is mostly still, and keyframes are
        // the expensive part at low bitrates.
        return new Budget(Math.Max(20, (int)(total - audioUse)), audio, (int)Math.Round(Math.Clamp(src.Fps, 10, 120) * 10), targetBytes);
    }

    /// <summary>Short description of the original's bitrates, shown when the slider can't go lower.</summary>
    public static string Describe(VideoInfo src)
    {
        var (video, audio) = Rates(src);
        return $"video {video:0} kbps" + (src.HasAudio ? $", audio {audio:0} kbps" : "");
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
        string[] Rate(int k) => new[] { "-c:v", "libx264", "-preset", "medium", "-b:v", k + "k",
            "-maxrate", k * 2 + "k", "-bufsize", k * 4 + "k", "-g", budget.Gop.ToString(CultureInfo.InvariantCulture),
            "-passlogfile", log };

        var p1 = new List<string>(common); p1.AddRange(inputArgs);
        p1.AddRange(Rate(kbps)); p1.AddRange(new[] { "-pass", "1" }); p1.AddRange(color);
        p1.AddRange(new[] { "-an", "-f", "null", "-" });
        await Ffmpeg.RunAsync(p1, work, length, Scale(progress, 0, 0.4), ct);

        Task Pass2(int k, double from, double span)
        {
            var p2 = new List<string>(common); p2.AddRange(inputArgs);
            p2.AddRange(Rate(k)); p2.AddRange(new[] { "-pass", "2" }); p2.AddRange(color);
            p2.AddRange(audio); p2.AddRange(mux); p2.Add(output);
            return Ffmpeg.RunAsync(p2, work, length, Scale(progress, from, span), ct);
        }
        await Pass2(kbps, 0.4, 0.6);

        // At very low bitrates x264 tends to overshoot (keyframes have a fixed cost). If the file came out more
        // than 8% over, run the second pass again with the difference taken off the video bitrate.
        double actual = new FileInfo(output).Length;
        if (actual > budget.TargetBytes * 1.08 && length > 0)
        {
            int fixedKbps = (int)(kbps - (actual - budget.TargetBytes) * 8 / 1000 / length);
            if (fixedKbps >= 15 && fixedKbps < kbps)
            {
                progress?.Report(0.4);
                await Pass2(fixedKbps, 0.4, 0.6);
            }
        }
    }

    private static IProgress<double>? Scale(IProgress<double>? p, double offset, double span) =>
        p == null ? null : new Progress<double>(v => p.Report(offset + v * span));
}
