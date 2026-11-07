using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ScreenRecorder.Controls;

/// <summary>Text with an outline, used to preview the overlay the way FFmpeg's drawtext will render it.</summary>
public sealed class OutlinedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = Reg(nameof(Text), typeof(string), "");
    public static readonly DependencyProperty FontFamilyProperty = Reg(nameof(FontFamily), typeof(FontFamily), new FontFamily("Arial"));
    public static readonly DependencyProperty FontWeightProperty = Reg(nameof(FontWeight), typeof(FontWeight), FontWeights.Normal);
    public static readonly DependencyProperty FontSizeProperty = Reg(nameof(FontSize), typeof(double), 48.0);
    public static readonly DependencyProperty FillProperty = Reg(nameof(Fill), typeof(Brush), Brushes.White);
    public static readonly DependencyProperty StrokeProperty = Reg(nameof(Stroke), typeof(Brush), Brushes.Black);
    public static readonly DependencyProperty StrokeThicknessProperty = Reg(nameof(StrokeThickness), typeof(double), 0.0);

    private static DependencyProperty Reg(string name, Type t, object def) =>
        DependencyProperty.Register(name, t, typeof(OutlinedText),
            new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    /// <summary>Outline width outside the glyphs, like drawtext's borderw.</summary>
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    private FormattedText Format() => Create(Text, FontFamily, FontWeight, FontSize, Fill);

    public static FormattedText Create(string text, FontFamily family, FontWeight weight, double size, Brush fill) => new(
        string.IsNullOrEmpty(text) ? " " : text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal),
        Math.Max(1, size), fill, 1.0);

    /// <summary>Draws outlined text the same way the preview control does (used when rendering GIF frames).</summary>
    public static void Draw(DrawingContext dc, FormattedText ft, Point origin, Brush fill, Brush stroke, double strokeThickness)
    {
        var geo = ft.BuildGeometry(origin);
        if (strokeThickness > 0)
            dc.DrawGeometry(null, new Pen(stroke, strokeThickness * 2) { LineJoin = PenLineJoin.Round }, geo);
        dc.DrawGeometry(fill, null, geo);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var ft = Format();
        return new Size(ft.WidthIncludingTrailingWhitespace, ft.Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        Draw(dc, Format(), new Point(0, 0), Fill, Stroke, StrokeThickness);
    }
}
