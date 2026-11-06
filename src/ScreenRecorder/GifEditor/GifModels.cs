using System.Windows;
using System.Windows.Media;
using ScreenRecorder.Controls;
using ScreenRecorder.Services;

namespace ScreenRecorder.GifEditor;

/// <summary>One GIF frame: an image file and how long it stays on screen. Immutable, so undo can keep snapshots.</summary>
public sealed record GifFrame(string ImagePath, int DelayMs);

public sealed record TextColor(string Name, string Hex, string OutlineHex)
{
    public override string ToString() => Name;

    public static readonly TextColor[] All =
    {
        new("White", "FFFFFF", "000000"),
        new("Yellow", "FFE14D", "000000"),
        new("Black", "000000", "FFFFFF"),
        new("Red", "FF3B30", "000000"),
        new("Cyan", "3FE0FF", "000000"),
        new("Lime", "7CFF4F", "000000"),
        new("Pink", "FF5FD2", "000000"),
    };

    public static Color Parse(string hex) =>
        Color.FromRgb(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
}

/// <summary>A caption shown on a range of frames. Positions and size are in frame pixels.</summary>
public sealed class TextLayer
{
    public string Text { get; set; } = "Your text";
    public FontChoice Font { get; set; } = FontChoice.All[3];
    public double FontSize { get; set; } = 48;
    public TextColor Color { get; set; } = TextColor.All[0];
    public bool Outline { get; set; } = true;
    public bool CenterX { get; set; } = true;
    public double X { get; set; }
    public double Y { get; set; } = 16;
    /// <summary>First frame (0-based, inclusive).</summary>
    public int FromFrame { get; set; }
    /// <summary>Last frame (0-based, inclusive). int.MaxValue means "to the end".</summary>
    public int ToFrame { get; set; } = int.MaxValue;

    public int OutlineWidth => Math.Max(2, (int)Math.Round(FontSize / 18));
    public bool IsOnFrame(int index) => index >= FromFrame && index <= ToFrame;
    public override string ToString() => string.IsNullOrWhiteSpace(Text) ? "(empty)" : Text.Replace("\r", "").Replace('\n', ' ');

    public TextLayer Clone() => (TextLayer)MemberwiseClone();

    public FormattedText Format() => OutlinedText.Create(Text.Replace("\r\n", "\n").TrimEnd('\n'),
        new FontFamily(Font.WpfFamily), Font.Bold ? FontWeights.Bold : FontWeights.Normal, FontSize,
        new SolidColorBrush(TextColor.Parse(Color.Hex)));

    /// <summary>Top-left position of the text, centring inside <paramref name="area"/> when CenterX is on.</summary>
    public Point Origin(Rect area, double textWidth) => CenterX ? new Point(area.X + (area.Width - textWidth) / 2, Y) : new Point(X, Y);

    public void DrawOn(DrawingContext dc, Rect area)
    {
        if (string.IsNullOrWhiteSpace(Text)) return;
        var ft = Format();
        OutlinedText.Draw(dc, ft, Origin(area, ft.WidthIncludingTrailingWhitespace),
            new SolidColorBrush(TextColor.Parse(Color.Hex)), new SolidColorBrush(TextColor.Parse(Color.OutlineHex)),
            Outline ? OutlineWidth : 0);
    }
}

public sealed record CropPreset(string Name, double Aspect)
{
    public override string ToString() => Name;
    public bool IsOff => Aspect < 0;
    public bool IsFree => Aspect == 0;

    public static readonly CropPreset[] All =
    {
        new("No crop", -1),
        new("Free", 0),
        new("Square 1:1", 1),
        new("Portrait 4:5", 4.0 / 5),
        new("Wide 16:9", 16.0 / 9),
        new("Story 9:16", 9.0 / 16),
    };
}

public sealed record LoopChoice(string Name, int FfmpegLoop)
{
    public override string ToString() => Name;
    public static readonly LoopChoice[] All =
    {
        new("Loop forever", 0),
        new("Play once", -1),
        new("Play 2 times", 1),
        new("Play 3 times", 2),
    };
}
