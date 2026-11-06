using System.Text;

namespace ScreenRecorder.Services;

public enum ExportFormat { Mp4, Mkv, Gif }

public sealed record FontChoice(string Name, string WpfFamily, bool Bold, string FileName)
{
    public override string ToString() => Name;

    public static readonly FontChoice[] All =
    {
        new("Arial", "Arial", false, "arial.ttf"),
        new("Arial Bold", "Arial", true, "arialbd.ttf"),
        new("Arial Black", "Arial Black", false, "ariblk.ttf"),
        new("Impact", "Impact", false, "impact.ttf"),
        new("Segoe UI", "Segoe UI", false, "segoeui.ttf"),
        new("Segoe UI Bold", "Segoe UI", true, "segoeuib.ttf"),
        new("Comic Sans MS", "Comic Sans MS", false, "comic.ttf"),
        new("Consolas", "Consolas", false, "consola.ttf"),
    };

    public string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), FileName);
}

/// <summary>Text drawn over the video. Coordinates and size are in video pixels.</summary>
public sealed class TextOverlay
{
    public string Text { get; set; } = "";
    public FontChoice Font { get; set; } = FontChoice.All[3];
    public double FontSize { get; set; } = 64;
    public string ColorHex { get; set; } = "FFFFFF";
    public bool Outline { get; set; } = true;
    public string OutlineHex { get; set; } = "000000";
    public double X { get; set; } = 20;
    public double Y { get; set; } = 20;
    public bool CenterX { get; set; } = true;

    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);
    public int OutlineWidth => Math.Max(2, (int)Math.Round(FontSize / 18));
}

public sealed class ExportOptions
{
    public required string Input { get; init; }
    public required string Output { get; init; }
    public ExportFormat Format { get; init; }
    public double Start { get; init; }
    public double End { get; init; }
    public double SourceDuration { get; init; }
    public TextOverlay? Overlay { get; init; }
    public int GifFps { get; init; } = 15;
    /// <summary>0 keeps the original width.</summary>
    public int GifWidth { get; init; } = 640;
    public int SourceWidth { get; init; }
}

public static class Exporter
{
    public static async Task ExportAsync(ExportOptions o, IProgress<double>? progress, CancellationToken ct)
    {
        string work = Path.Combine(Path.GetTempPath(), "ScreenRecorderExport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string? drawtext = PrepareDrawText(o.Overlay, work);
            double length = Math.Max(0.05, o.End - o.Start);
            bool trimmed = o.Start > 0.01 || o.End < o.SourceDuration - 0.01;
            var seek = new List<string>();
            if (trimmed) seek.AddRange(new[] { "-ss", Ffmpeg.Num(o.Start), "-t", Ffmpeg.Num(length) });
            seek.AddRange(new[] { "-i", o.Input });

            var common = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error", "-progress", "pipe:1", "-y" };

            if (o.Format == ExportFormat.Gif)
            {
                string chain = (drawtext != null ? drawtext + "," : "") + $"fps={o.GifFps}";
                if (o.GifWidth > 0 && o.GifWidth < o.SourceWidth) chain += $",scale={o.GifWidth}:-2:flags=lanczos";
                string palette = Path.Combine(work, "palette.png");

                // Pass 1: build an optimised 256-colour palette.
                var p1 = new List<string>(common); p1.AddRange(seek);
                p1.AddRange(new[] { "-vf", chain + ",palettegen=stats_mode=diff", "-frames:v", "1", "-update", "1", palette });
                await Ffmpeg.RunAsync(p1, work, length, Scale(progress, 0, 0.4), ct);

                // Pass 2: render with the palette.
                var p2 = new List<string>(common); p2.AddRange(seek);
                p2.AddRange(new[]
                {
                    "-i", palette,
                    "-filter_complex", $"[0:v]{chain}[x];[x][1:v]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle[out]",
                    "-map", "[out]", "-loop", "0", o.Output,
                });
                await Ffmpeg.RunAsync(p2, work, length, Scale(progress, 0.4, 0.6), ct);
                return;
            }

            var a = new List<string>(common); a.AddRange(seek);
            a.AddRange(new[] { "-map", "0:v:0", "-map", "0:a?" });
            if (!trimmed && drawtext == null)
            {
                // Nothing to change: copy the streams untouched (instant, lossless).
                a.AddRange(new[] { "-c", "copy" });
            }
            else
            {
                if (drawtext != null) a.AddRange(new[] { "-vf", drawtext });
                a.AddRange(new[]
                {
                    "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p",
                    "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709",
                    "-c:a", "aac", "-b:a", "320k", "-ar", "48000", "-ac", "2",
                });
            }
            if (o.Format == ExportFormat.Mp4) a.AddRange(new[] { "-movflags", "+faststart" });
            a.Add(o.Output);
            await Ffmpeg.RunAsync(a, work, length, progress, ct);
        }
        catch
        {
            try { if (File.Exists(o.Output)) File.Delete(o.Output); } catch { }
            throw;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    /// <summary>
    /// Builds a drawtext filter. The text and font are copied into the working folder and referenced
    /// by relative name, which sidesteps FFmpeg's escaping rules for Windows paths and user text.
    /// </summary>
    private static string? PrepareDrawText(TextOverlay? ov, string work)
    {
        if (ov == null || ov.IsEmpty) return null;
        string text = ov.Text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        File.WriteAllText(Path.Combine(work, "overlay.txt"), text, new UTF8Encoding(false));

        string font = ov.Font.FilePath;
        if (!File.Exists(font)) font = FontChoice.All[0].FilePath;
        if (!File.Exists(font)) throw new FileNotFoundException("Could not find a font for the text overlay.", font);
        File.Copy(font, Path.Combine(work, "font.ttf"), true);

        string x = ov.CenterX ? "(w-text_w)/2" : ((int)Math.Round(ov.X)).ToString();
        var sb = new StringBuilder("drawtext=fontfile=font.ttf:textfile=overlay.txt:expansion=none");
        sb.Append($":fontsize={(int)Math.Round(ov.FontSize)}:fontcolor=0x{ov.ColorHex}:x={x}:y={(int)Math.Round(ov.Y)}:y_align=font");
        if (ov.Outline) sb.Append($":borderw={ov.OutlineWidth}:bordercolor=0x{ov.OutlineHex}");
        return sb.ToString();
    }

    private static IProgress<double>? Scale(IProgress<double>? p, double offset, double span) =>
        p == null ? null : new Progress<double>(v => p.Report(offset + v * span));
}
