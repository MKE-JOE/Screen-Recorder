using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenRecorder.Services;

/// <summary>
/// A rectangle that is blurred for part of a clip. Coordinates are in video (or GIF frame) pixels.
/// The video editor times it in seconds, the GIF editor in frames.
/// </summary>
public sealed class BlurBox
{
    public const int MaxCount = 10;
    public const double MinSize = 32;

    public Rect Area { get; set; }
    /// <summary>Video editor: blurred from this time (seconds in the source video).</summary>
    public double StartSec { get; set; }
    /// <summary>Video editor: blurred until this time. double.MaxValue means "to the end".</summary>
    public double EndSec { get; set; } = double.MaxValue;
    /// <summary>GIF editor: first frame (0-based, inclusive).</summary>
    public int FromFrame { get; set; }
    /// <summary>GIF editor: last frame (0-based, inclusive). int.MaxValue means "to the end".</summary>
    public int ToFrame { get; set; } = int.MaxValue;
    /// <summary>Shown in the list, numbered in the order the boxes were added.</summary>
    public int Number { get; set; }

    public bool IsAt(double seconds) => seconds >= StartSec && seconds <= EndSec;
    public bool IsOnFrame(int index) => index >= FromFrame && index <= ToFrame;

    public BlurBox Clone() => (BlurBox)MemberwiseClone();

    /// <summary>The area snapped to even pixels inside a w × h frame, as FFmpeg's crop on 4:2:0 video expects.</summary>
    public Int32Rect EvenArea(int w, int h)
    {
        int x = Math.Clamp((int)Math.Round(Area.X) & ~1, 0, Math.Max(0, w - 2));
        int y = Math.Clamp((int)Math.Round(Area.Y) & ~1, 0, Math.Max(0, h - 2));
        int bw = Math.Clamp((int)Math.Round(Area.Width) & ~1, 2, (w - x) & ~1);
        int bh = Math.Clamp((int)Math.Round(Area.Height) & ~1, 2, (h - y) & ~1);
        return new Int32Rect(x, y, bw, bh);
    }

    /// <summary>The area in whole pixels, kept inside a w × h frame.</summary>
    public Int32Rect PixelArea(int w, int h)
    {
        int x = Math.Clamp((int)Math.Round(Area.X), 0, Math.Max(0, w - 1));
        int y = Math.Clamp((int)Math.Round(Area.Y), 0, Math.Max(0, h - 1));
        int bw = Math.Clamp((int)Math.Round(Area.Width), 1, w - x);
        int bh = Math.Clamp((int)Math.Round(Area.Height), 1, h - y);
        return new Int32Rect(x, y, bw, bh);
    }

    public override string ToString() => $"Blur {Number}";

    /// <summary>Blur radius for a picture of the given height: strong enough to hide text, scaled with resolution.</summary>
    public static int Radius(int height) => Math.Max(8, (int)Math.Round(height / 40.0));

    /// <summary>
    /// Returns the blurred pixels of <paramref name="r"/> in <paramref name="frame"/>: two box-blur passes, the same
    /// as FFmpeg's boxblur with power 2. Pixels just outside the box feed the blur, so its edges stay solid.
    /// </summary>
    public static BitmapSource BlurPatch(BitmapSource frame, Int32Rect r, int radius)
    {
        const int passes = 2;
        if (frame.Format != PixelFormats.Bgra32 && frame.Format != PixelFormats.Pbgra32)
            frame = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
        int fw = frame.PixelWidth, fh = frame.PixelHeight;
        int pad = radius * passes;
        int x0 = Math.Max(0, r.X - pad), y0 = Math.Max(0, r.Y - pad);
        int x1 = Math.Min(fw, r.X + r.Width + pad), y1 = Math.Min(fh, r.Y + r.Height + pad);
        int w = x1 - x0, h = y1 - y0, stride = w * 4;

        var px = new byte[stride * h];
        frame.CopyPixels(new Int32Rect(x0, y0, w, h), px, stride, 0);
        var tmp = new byte[px.Length];
        for (int p = 0; p < passes; p++)
        {
            BoxBlur(px, tmp, w, h, radius, horizontal: true);
            BoxBlur(tmp, px, w, h, radius, horizontal: false);
        }

        int ox = r.X - x0, oy = r.Y - y0, outStride = r.Width * 4;
        var outPx = new byte[outStride * r.Height];
        for (int y = 0; y < r.Height; y++)
            Buffer.BlockCopy(px, (oy + y) * stride + ox * 4, outPx, y * outStride, outStride);
        var bmp = BitmapSource.Create(r.Width, r.Height, 96, 96, frame.Format, null, outPx, outStride);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>One box-blur pass along rows or columns of a BGRA buffer, repeating the edge pixels.</summary>
    private static void BoxBlur(byte[] src, byte[] dst, int w, int h, int r, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        int step = horizontal ? 4 : w * 4, div = 2 * r + 1;
        for (int line = 0; line < lines; line++)
        {
            int start = horizontal ? line * w * 4 : line * 4;
            for (int c = 0; c < 4; c++)
            {
                int sum = 0;
                for (int k = -r; k <= r; k++) sum += src[start + Math.Clamp(k, 0, len - 1) * step + c];
                for (int i = 0; i < len; i++)
                {
                    dst[start + i * step + c] = (byte)(sum / div);
                    sum += src[start + Math.Min(i + r + 1, len - 1) * step + c] - src[start + Math.Max(i - r, 0) * step + c];
                }
            }
        }
    }
}
