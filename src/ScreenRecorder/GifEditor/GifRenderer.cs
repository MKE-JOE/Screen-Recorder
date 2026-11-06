using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenRecorder.Services;

namespace ScreenRecorder.GifEditor;

public static class GifRenderer
{
    public const double MaxSeconds = 15.0;

    /// <summary>
    /// Splits a clip into PNG frames. Delays are whole centiseconds (what GIF stores) and are spread so the
    /// total length matches the clip exactly.
    /// </summary>
    public static async Task<List<GifFrame>> ExtractAsync(string input, double start, double length, int fps, int width, int sourceWidth,
        string folder, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        string vf = $"fps={fps}";
        if (width > 0 && width < sourceWidth) vf += $",scale={width}:-2:flags=lanczos";
        await Ffmpeg.RunAsync(new[]
        {
            "-hide_banner", "-nostats", "-loglevel", "error", "-progress", "pipe:1", "-y",
            "-ss", Ffmpeg.Num(start), "-t", Ffmpeg.Num(length), "-i", input,
            "-vf", vf, "-start_number", "0", Path.Combine(folder, "f_%05d.png"),
        }, folder, length, progress, ct);

        var files = Directory.GetFiles(folder, "f_*.png").OrderBy(f => f, StringComparer.Ordinal).ToList();
        var frames = new List<GifFrame>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            int cs = (int)Math.Round((i + 1) * 100.0 / fps) - (int)Math.Round(i * 100.0 / fps);
            frames.Add(new GifFrame(files[i], Math.Max(2, cs) * 10));
        }
        return frames;
    }

    /// <summary>Renders text and crop into the frames (on the UI thread), then encodes an optimised GIF.</summary>
    public static async Task ExportAsync(IReadOnlyList<GifFrame> frames, IReadOnlyList<TextLayer> layers, Int32Rect? crop,
        int frameWidth, int frameHeight, int loop, bool dither, string output, IProgress<double> progress, CancellationToken ct)
    {
        string work = Path.Combine(Path.GetTempPath(), "ScreenRecorderGif_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var area = crop is Int32Rect c ? new Rect(c.X, c.Y, c.Width, c.Height) : new Rect(0, 0, frameWidth, frameHeight);
            bool needsRender = crop != null || layers.Any(l => !string.IsNullOrWhiteSpace(l.Text));
            var paths = new List<string>(frames.Count);

            for (int i = 0; i < frames.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!needsRender) { paths.Add(frames[i].ImagePath); continue; }

                var src = Load(frames[i].ImagePath);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.PushTransform(new TranslateTransform(-area.X, -area.Y));
                    dc.DrawImage(src, new Rect(0, 0, frameWidth, frameHeight));
                    foreach (var layer in layers)
                        if (layer.IsOnFrame(i)) layer.DrawOn(dc, area);
                    dc.Pop();
                }
                var rtb = new RenderTargetBitmap((int)area.Width, (int)area.Height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                string path = Path.Combine(work, $"r_{i:00000}.png");
                using (var fs = File.Create(path))
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    enc.Save(fs);
                }
                paths.Add(path);
                progress.Report(0.5 * (i + 1) / frames.Count);
                if (i % 4 == 0) await Task.Yield();   // keep the window responsive
            }

            // Variable frame delays via the concat demuxer; framerate 100 keeps centisecond timing exact.
            var list = new StringBuilder("ffconcat version 1.0\n");
            for (int i = 0; i < frames.Count; i++)
            {
                list.Append("file '").Append(paths[i].Replace("'", "'\\''")).Append("'\n");
                list.Append("option framerate 100\n");
                list.Append("duration ").Append((frames[i].DelayMs / 1000.0).ToString("0.00", CultureInfo.InvariantCulture)).Append('\n');
            }
            string listPath = Path.Combine(work, "frames.txt");
            File.WriteAllText(listPath, list.ToString(), new UTF8Encoding(false));
            string palette = Path.Combine(work, "palette.png");
            double total = frames.Sum(f => f.DelayMs) / 1000.0;

            var common = new[] { "-hide_banner", "-nostats", "-loglevel", "error", "-progress", "pipe:1", "-y", "-f", "concat", "-safe", "0", "-i", listPath };
            await Ffmpeg.RunAsync(common.Concat(new[] { "-vf", "palettegen=stats_mode=diff", "-frames:v", "1", "-update", "1", palette }),
                work, total, Scale(progress, 0.5, 0.15), ct);

            string use = dither ? "paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle" : "paletteuse=dither=none:diff_mode=rectangle";
            int finalDelayCs = frames[^1].DelayMs / 10;
            await Ffmpeg.RunAsync(common.Concat(new[]
            {
                "-i", palette, "-lavfi", $"[0:v][1:v]{use}", "-fps_mode", "vfr",
                "-final_delay", finalDelayCs.ToString(CultureInfo.InvariantCulture),
                "-loop", loop.ToString(CultureInfo.InvariantCulture), output,
            }), work, total, Scale(progress, 0.65, 0.35), ct);
        }
        catch
        {
            try { if (File.Exists(output)) File.Delete(output); } catch { }
            throw;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    /// <summary>Loads an image fully into memory so the file is not kept locked.</summary>
    public static BitmapImage Load(string path, int decodeHeight = 0)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (decodeHeight > 0) bmp.DecodePixelHeight = decodeHeight;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static IProgress<double> Scale(IProgress<double> p, double offset, double span) =>
        new Progress<double>(v => p.Report(offset + v * span));
}
