using System.Windows;

namespace ScreenRecorder.VideoEditor;

/// <summary>A rectangle of the video that is blurred between two times. Coordinates are in video pixels.</summary>
public sealed class BlurBox
{
    public const int MaxCount = 10;
    public const double MinSize = 32;

    public Rect Area { get; set; }
    /// <summary>Blurred from this time (seconds in the source video).</summary>
    public double StartSec { get; set; }
    /// <summary>Blurred until this time. double.MaxValue means "to the end".</summary>
    public double EndSec { get; set; } = double.MaxValue;
    /// <summary>Shown in the list, numbered in the order the boxes were added.</summary>
    public int Number { get; set; }

    public bool IsAt(double seconds) => seconds >= StartSec && seconds <= EndSec;

    /// <summary>The area snapped to even pixels inside a w × h frame, as FFmpeg's crop on 4:2:0 video expects.</summary>
    public Int32Rect EvenArea(int w, int h)
    {
        int x = Math.Clamp((int)Math.Round(Area.X) & ~1, 0, Math.Max(0, w - 2));
        int y = Math.Clamp((int)Math.Round(Area.Y) & ~1, 0, Math.Max(0, h - 2));
        int bw = Math.Clamp((int)Math.Round(Area.Width) & ~1, 2, (w - x) & ~1);
        int bh = Math.Clamp((int)Math.Round(Area.Height) & ~1, 2, (h - y) & ~1);
        return new Int32Rect(x, y, bw, bh);
    }

    /// <summary>Blur radius for a video of the given height: strong enough to hide text, scaled with resolution.</summary>
    public static int Radius(int videoHeight) => Math.Max(8, (int)Math.Round(videoHeight / 40.0));

    public override string ToString() => $"Blur {Number}";
}
