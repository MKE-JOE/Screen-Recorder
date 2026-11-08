using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenRecorder.Controls;
using ScreenRecorder.GifEditor;
using ScreenRecorder.Services;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace ScreenRecorder.VideoEditor;

public sealed class VideoEditorOptions
{
    public required string Input { get; init; }
    public double Start { get; init; }
    public double End { get; init; }
    public ExportFormat Format { get; init; } = ExportFormat.Mp4;
    public TextOverlay? InitialText { get; init; }
    /// <summary>Starting position of the File size slider (25–100).</summary>
    public double SizePercent { get; init; } = 100;
}

/// <summary>Simple video editor: trim, crop, resize, timed captions and blur boxes, exported as high-quality MP4 or MKV.</summary>
public partial class VideoEditorWindow : Window
{
    private sealed record OutputSize(string Name, int Height) { public override string ToString() => Name; }

    private static readonly OutputSize[] Sizes =
    {
        new("Original size", 0),
        new("1080p", 1080),
        new("720p", 720),
        new("480p", 480),
    };

    private readonly VideoEditorOptions _opt;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly List<TextLayer> _layers = new();
    private readonly Dictionary<TextLayer, OutlinedText> _visuals = new();
    private readonly List<BlurBox> _blurs = new();
    private readonly Dictionary<BlurBox, BlurVisual> _blurVisuals = new();
    /// <summary>False after "Done": outlines and handles are hidden so the edit can be reviewed as it will export.</summary>
    private bool _blurEditing = true;
    private int _w = 1280, _h = 720;
    private double _duration, _start, _end;
    private bool _loadingUi, _playing, _syncing, _busy;
    private CropPreset _cropPreset = CropPreset.All[0];
    private Rect _crop;
    private CancellationTokenSource? _exportCts;
    private string? _lastSaved;

    private enum Drag { None, Text, Crop, Blur }
    private Drag _drag;
    private Point _dragStart, _dragOrigin;
    private TextLayer? _dragLayer;
    private BlurBox? _dragBlur;

    /// <summary>Preview of one blur box: a clipped, blurred copy of the video under it, plus a frame to drag.</summary>
    private sealed record BlurVisual(Canvas Clip, Rectangle Blurred, VisualBrush Brush, Border Frame);

    /// <summary>Set by --selftest-video: after loading, add a caption and a crop, export here, then exit.</summary>
    public string? SelfTestOutput { get; init; }

    public VideoEditorWindow(VideoEditorOptions options)
    {
        InitializeComponent();
        Transport.StartButton.ToolTip = "Jump to trim start (press again for the start of the recording)";
        Transport.EndButton.ToolTip = "Jump to trim end (press again for the end of the recording)";
        Transport.StopButton.ToolTip = "Stop and go back to trim start";
        _opt = options;
        LayerFont.ItemsSource = FontChoice.All;
        LayerColor.ItemsSource = TextColor.All;
        CropCombo.ItemsSource = CropPreset.All;
        CropCombo.SelectedIndex = 0;
        SizeCombo.ItemsSource = Sizes;
        SizeCombo.SelectedIndex = 0;
        FmtMkv.IsChecked = options.Format == ExportFormat.Mkv;
        _tick.Tick += OnTick;
        Loaded += OnLoaded;
        Loaded += async (_, _) => await LoadInfoAsync();
        FileSizeSlider.Value = Math.Clamp(options.SizePercent, 25, 100);
        SidePanel.IsEnabled = false;
        Closing += (_, _) => { _tick.Stop(); _exportCts?.Cancel(); Media.Close(); };

        Media.Volume = 0;
        Media.Source = new Uri(options.Input);
        Media.Play();   // opens the file; paused again in MediaOpened
    }

    // ------------------------------------------------------------------ playback

    private bool _ready, _previewOk;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // If the preview never opens (no Windows media codecs, e.g. "N" editions), still allow editing and export.
        var fallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SelfTestOutput != null ? 6 : 10) };
        fallback.Tick += async (_, _) => { fallback.Stop(); if (!_ready) await InitializeFromProbeAsync(); };
        fallback.Start();
    }

    private async void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        Media.Pause();
        Media.Volume = 1;
        if (_ready) return;
        _previewOk = true;
        double duration = Media.NaturalDuration.HasTimeSpan ? Media.NaturalDuration.TimeSpan.TotalSeconds : Math.Max(_opt.End, 1);
        await InitializeAsync(Media.NaturalVideoWidth, Media.NaturalVideoHeight, duration);
    }

    private async void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (_ready) return;
        if (SelfTestOutput == null)
            MessageBox.Show(this, "This video can't be previewed on this PC (" + e.ErrorException?.Message +
                "). You can still trim, crop, add text and export it.", "Video Editor", MessageBoxButton.OK, MessageBoxImage.Information);
        await InitializeFromProbeAsync();
    }

    private VideoInfo? _info;

    /// <summary>Reads size and duration from FFmpeg's stream info when the preview can't.</summary>
    private async Task InitializeFromProbeAsync()
    {
        if (_ready) return;
        _info ??= await SizeTarget.ProbeAsync(_opt.Input);
        if (_info == null) { if (SelfTestOutput != null) App.Fail("Video probe found no video stream"); return; }
        await InitializeAsync(_info.Width, _info.Height, _info.Duration > 0 ? _info.Duration : _opt.End);
    }

    private async Task LoadInfoAsync()
    {
        _info ??= await SizeTarget.ProbeAsync(_opt.Input);
        UpdateFileSize();
    }

    private async Task InitializeAsync(int w, int h, double duration)
    {
        if (_ready) return;
        _ready = true;
        _w = Math.Max(2, w);
        _h = Math.Max(2, h);
        _duration = Math.Max(0.1, duration);
        Stage.Width = _w;
        Stage.Height = _h;
        _start = Math.Clamp(_opt.Start, 0, _duration);
        _end = _opt.End > _start ? Math.Min(_opt.End, _duration) : _duration;
        Timeline.Maximum = _duration;
        DurationText.Text = Fmt(_duration);
        SyncTrim();
        Seek(_start);

        if (_opt.InitialText is { IsEmpty: false } t)
        {
            _layers.Add(new TextLayer
            {
                Text = t.Text, Font = t.Font, FontSize = t.FontSize,
                Color = TextColor.All.FirstOrDefault(c => c.Hex == t.ColorHex) ?? TextColor.All[0],
                Outline = t.Outline, CenterX = t.CenterX, X = t.X, Y = t.Y,
            });
            RefreshLayers();
            LayerList.SelectedIndex = 0;
        }
        OnCropPreset(this, null!);
        SidePanel.IsEnabled = true;
        _tick.Start();

        if (SelfTestOutput != null) await RunSelfTestAsync(SelfTestOutput);
    }

    private double _pos;
    private double Position => _previewOk ? Media.Position.TotalSeconds : _pos;

    private void Seek(double seconds)
    {
        seconds = Math.Clamp(seconds, 0, _duration);
        _pos = seconds;
        if (_previewOk) Media.Position = TimeSpan.FromSeconds(seconds);
        SyncTimeline(seconds);
    }

    private void SyncTimeline(double seconds)
    {
        _syncing = true;
        Timeline.Value = seconds;
        _syncing = false;
        PositionText.Text = Fmt(seconds);
        UpdateVisibility(seconds);
    }

    private void OnTimelineChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || !_ready) return;
        _pos = e.NewValue;
        if (_previewOk) Media.Position = TimeSpan.FromSeconds(e.NewValue);
        PositionText.Text = Fmt(e.NewValue);
        UpdateVisibility(e.NewValue);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_playing) return;
        double pos = Position;
        if (pos >= _end) { Pause(); Seek(_end); return; }
        SyncTimeline(pos);
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_playing || !_previewOk) return;
        if (Position < _start || Position >= _end - 0.05) Seek(_start);
        Media.Play();
        _playing = true;
    }

    private void OnPause(object sender, RoutedEventArgs e) => Pause();

    private void Pause()
    {
        Media.Pause();
        _playing = false;
    }

    private void OnStop(object sender, RoutedEventArgs e) { Pause(); Seek(_start); }

    // Jump to the trim point; pressing again when already there goes to the very start/end of the recording.
    private void OnJumpStart(object sender, RoutedEventArgs e) => Seek(Position > _start + 0.05 ? _start : 0);
    private void OnJumpEnd(object sender, RoutedEventArgs e) { Pause(); Seek(Position < _end - 0.05 ? _end : _duration); }

    private void OnBack(object sender, RoutedEventArgs e) => Seek(Position - 5);
    private void OnForward(object sender, RoutedEventArgs e) => Seek(Position + 5);

    private void OnSetStart(object sender, RoutedEventArgs e) { _start = Math.Clamp(Position, 0, _end - 0.1); SyncTrim(); }
    private void OnSetEnd(object sender, RoutedEventArgs e) { _end = Math.Clamp(Position, _start + 0.1, _duration); SyncTrim(); }
    private void OnResetTrim(object sender, RoutedEventArgs e) { _start = 0; _end = _duration; SyncTrim(); }

    private void SyncTrim()
    {
        Timeline.SelectionStart = _start;
        Timeline.SelectionEnd = _end;
        TrimText.Text = $"Keeping {Fmt(_start)} to {Fmt(_end)} ({Fmt(_end - _start)})";
        UpdateFileSize();
    }

    // ------------------------------------------------------------------ text

    private TextLayer? CurrentLayer => LayerList.SelectedItem as TextLayer;
    private Rect VisibleArea => _cropPreset.IsOff ? new Rect(0, 0, _w, _h) : _crop;

    private void RefreshLayers()
    {
        var sel = CurrentLayer;
        LayerList.ItemsSource = null;
        LayerList.ItemsSource = _layers;
        if (sel != null && _layers.Contains(sel)) LayerList.SelectedItem = sel;

        foreach (var v in _visuals.Values) StageCanvas.Children.Remove(v);
        _visuals.Clear();
        foreach (var layer in _layers)
        {
            var v = new OutlinedText { Cursor = Cursors.SizeAll, Tag = layer };
            Panel.SetZIndex(v, 2);   // above blur frames, so overlapping text can still be dragged
            v.MouseLeftButtonDown += OnTextMouseDown;
            StageCanvas.Children.Add(v);
            _visuals[layer] = v;
            UpdateVisual(layer);
        }
    }

    private void OnAddLayer(object sender, RoutedEventArgs e)
    {
        var layer = new TextLayer
        {
            FontSize = Math.Clamp(Math.Round(_h / 10.0), 24, 200),
            Y = VisibleArea.Y + Math.Round(VisibleArea.Height * 0.05),
        };
        _layers.Add(layer);
        RefreshLayers();
        LayerList.SelectedItem = layer;
        LayerText.Focus();
        LayerText.SelectAll();
    }

    private void OnRemoveLayer(object sender, RoutedEventArgs e)
    {
        if (CurrentLayer is not TextLayer l) return;
        _layers.Remove(l);
        RefreshLayers();
        LoadLayerEditor();
    }

    private void OnLayerSelected(object sender, SelectionChangedEventArgs e) => LoadLayerEditor();

    private void LoadLayerEditor()
    {
        var l = CurrentLayer;
        LayerEditor.IsEnabled = l != null;
        if (l == null) return;
        _loadingUi = true;
        LayerText.Text = l.Text;
        LayerFont.SelectedItem = FontChoice.All.FirstOrDefault(f => f.Name == l.Font.Name) ?? FontChoice.All[0];
        LayerColor.SelectedItem = TextColor.All.FirstOrDefault(c => c.Name == l.Color.Name) ?? TextColor.All[0];
        LayerSize.Maximum = Math.Max(300, _h / 2.0);
        LayerSize.Value = l.FontSize;
        LayerSizeText.Text = ((int)l.FontSize).ToString();
        LayerOutline.IsChecked = l.Outline;
        LayerCenter.IsChecked = l.CenterX;
        _loadingUi = false;
        UpdateRangeText(l);
    }

    private void OnLayerEdited(object sender, RoutedEventArgs e)
    {
        if (_loadingUi || CurrentLayer is not TextLayer l) return;
        l.Text = LayerText.Text;
        l.Font = (FontChoice?)LayerFont.SelectedItem ?? l.Font;
        l.Color = (TextColor?)LayerColor.SelectedItem ?? l.Color;
        l.FontSize = Math.Round(LayerSize.Value);
        l.Outline = LayerOutline.IsChecked == true;
        l.CenterX = LayerCenter.IsChecked == true;
        LayerSizeText.Text = ((int)l.FontSize).ToString();
        UpdateVisual(l);
        if (sender == LayerText)
        {
            _loadingUi = true;
            LayerList.Items.Refresh();
            LayerList.SelectedItem = l;
            _loadingUi = false;
        }
    }

    private void OnLayerPlace(object sender, RoutedEventArgs e)
    {
        if (CurrentLayer is not TextLayer l) return;
        var a = VisibleArea;
        double h = l.Format().Height, margin = Math.Round(a.Height * 0.05);
        l.Y = (string)((Button)sender).Tag switch
        {
            "top" => a.Y + margin,
            "middle" => a.Y + Math.Round((a.Height - h) / 2),
            _ => a.Y + Math.Round(a.Height - h - margin),
        };
        UpdateVisual(l);
    }

    private void OnLayerFrom(object sender, RoutedEventArgs e)
    {
        if (CurrentLayer is not TextLayer l) return;
        l.StartSec = Position;
        if (l.EndSec < l.StartSec) l.EndSec = double.MaxValue;
        UpdateRangeText(l);
        UpdateVisibility(Position);
    }

    private void OnLayerTo(object sender, RoutedEventArgs e)
    {
        if (CurrentLayer is not TextLayer l) return;
        l.EndSec = Position;
        if (l.StartSec > l.EndSec) l.StartSec = 0;
        UpdateRangeText(l);
        UpdateVisibility(Position);
    }

    private void OnLayerAll(object sender, RoutedEventArgs e)
    {
        if (CurrentLayer is not TextLayer l) return;
        l.StartSec = 0;
        l.EndSec = double.MaxValue;
        UpdateRangeText(l);
        UpdateVisibility(Position);
    }

    private void UpdateRangeText(TextLayer l)
    {
        LayerFromText.Text = l.StartSec <= 0 ? "start" : Fmt(l.StartSec);
        LayerToText.Text = l.EndSec == double.MaxValue ? "end" : Fmt(l.EndSec);
    }

    private void UpdateVisual(TextLayer l)
    {
        if (!_visuals.TryGetValue(l, out var v)) return;
        v.Text = l.Text.Replace("\r\n", "\n").TrimEnd('\n');
        v.FontFamily = new FontFamily(l.Font.WpfFamily);
        v.FontWeight = l.Font.Bold ? FontWeights.Bold : FontWeights.Normal;
        v.FontSize = l.FontSize;
        v.Fill = new SolidColorBrush(TextColor.Parse(l.Color.Hex));
        v.Stroke = new SolidColorBrush(TextColor.Parse(l.Color.OutlineHex));
        v.StrokeThickness = l.Outline ? l.OutlineWidth : 0;
        var pos = l.Origin(VisibleArea, l.Format().WidthIncludingTrailingWhitespace);
        Canvas.SetLeft(v, pos.X);
        Canvas.SetTop(v, pos.Y);
        v.Visibility = l.IsAt(Position) && !string.IsNullOrWhiteSpace(l.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateVisibility(double t)
    {
        foreach (var (layer, v) in _visuals)
            v.Visibility = layer.IsAt(t) && !string.IsNullOrWhiteSpace(layer.Text) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var b in _blurs) UpdateBlurVisibility(b, t);
    }

    private void OnTextMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;
        var v = (OutlinedText)sender;
        var l = (TextLayer)v.Tag;
        LayerList.SelectedItem = l;
        _drag = Drag.Text;
        _dragLayer = l;
        _dragStart = e.GetPosition(StageCanvas);
        _dragOrigin = new Point(Canvas.GetLeft(v), l.Y);
        v.CaptureMouse();
        e.Handled = true;
    }

    // ------------------------------------------------------------------ blur

    private BlurBox? CurrentBlur => BlurList.SelectedItem as BlurBox;

    /// <summary>Preview blur radius in video pixels; a Gaussian this size looks close to FFmpeg's two-pass box blur.</summary>
    private double PreviewBlurRadius => BlurBox.Radius(_h) * 1.6;

    private void RefreshBlurs()
    {
        var sel = CurrentBlur;
        for (int i = 0; i < _blurs.Count; i++) _blurs[i].Number = i + 1;
        BlurList.ItemsSource = null;
        BlurList.ItemsSource = _blurs;
        if (sel != null && _blurs.Contains(sel)) BlurList.SelectedItem = sel;

        foreach (var v in _blurVisuals.Values) { StageCanvas.Children.Remove(v.Clip); StageCanvas.Children.Remove(v.Frame); }
        _blurVisuals.Clear();
        foreach (var b in _blurs)
        {
            var brush = new VisualBrush(Media) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
            var blurred = new Rectangle
            {
                Fill = brush,
                Effect = new BlurEffect { Radius = PreviewBlurRadius, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
            };
            // Without a working preview there is no video to blur, so show a grey patch where the blur will go.
            var clip = new Canvas
            {
                ClipToBounds = true, IsHitTestVisible = false,
                Background = _previewOk ? null : new SolidColorBrush(Color.FromArgb(0xB0, 0x80, 0x80, 0x80)),
            };
            clip.Children.Add(blurred);
            Panel.SetZIndex(clip, -1);   // under the crop mask, like the real blur
            var frame = new Border { Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)), Cursor = Cursors.SizeAll, Tag = b };
            frame.MouseLeftButtonDown += OnBlurMouseDown;
            Panel.SetZIndex(frame, 1);   // above the crop box, so a blur inside the crop can be grabbed
            StageCanvas.Children.Add(clip);
            StageCanvas.Children.Add(frame);
            _blurVisuals[b] = new BlurVisual(clip, blurred, brush, frame);
            UpdateBlurVisual(b);
        }
        AddBlurButton.IsEnabled = _blurs.Count < BlurBox.MaxCount;
        BlurHint.Text = _blurs.Count >= BlurBox.MaxCount
            ? $"That's the most boxes ({BlurBox.MaxCount}). Remove one to add another."
            : "Hide part of the video. Drag a blur box on the preview to move it, the corner to resize. Click 'Done' to finalize each blur. Up to 10 blur boxes.";
        UpdateBlurHandle();
        DoneBlurButton.IsEnabled = _blurs.Count > 0;
    }

    private void OnAddBlur(object sender, RoutedEventArgs e)
    {
        if (_blurs.Count >= BlurBox.MaxCount) return;
        var a = VisibleArea;
        double w = Math.Min(_w, Math.Max(BlurBox.MinSize, Math.Round(a.Width / 4)));
        double h = Math.Min(_h, Math.Max(BlurBox.MinSize, Math.Round(a.Height / 4)));
        // Stagger new boxes a little so they don't land exactly on top of each other.
        double shift = (_blurs.Count % 5) * Math.Round(Math.Min(a.Width, a.Height) / 20);
        double x = Math.Clamp(Math.Round(a.X + (a.Width - w) / 2 + shift), 0, _w - w);
        double y = Math.Clamp(Math.Round(a.Y + (a.Height - h) / 2 + shift), 0, _h - h);
        var box = new BlurBox { Area = new Rect(x, y, w, h) };
        _blurs.Add(box);
        _blurEditing = true;
        RefreshBlurs();
        BlurList.SelectedItem = box;
    }

    private void OnRemoveBlur(object sender, RoutedEventArgs e)
    {
        if (CurrentBlur is not BlurBox b) return;
        _blurs.Remove(b);
        RefreshBlurs();
        LoadBlurEditor();
    }

    private void OnBlurSelected(object sender, SelectionChangedEventArgs e)
    {
        if (CurrentBlur != null) _blurEditing = true;   // picking a box in the list brings the outlines back
        LoadBlurEditor();
        foreach (var b in _blurs) UpdateBlurVisual(b);
        UpdateBlurHandle();
    }

    private void OnBlurDone(object sender, RoutedEventArgs e)
    {
        _blurEditing = false;
        BlurList.SelectedItem = null;   // also hides the editor fields and the handle
        foreach (var b in _blurs) UpdateBlurVisual(b);
        UpdateBlurHandle();
    }

    private void LoadBlurEditor()
    {
        var b = CurrentBlur;
        BlurEditor.IsEnabled = b != null;
        if (b == null) { BlurFromText.Text = BlurToText.Text = ""; return; }
        BlurFromText.Text = b.StartSec <= 0 ? "start" : Fmt(b.StartSec);
        BlurToText.Text = b.EndSec == double.MaxValue ? "end" : Fmt(b.EndSec);
    }

    private void OnBlurFrom(object sender, RoutedEventArgs e)
    {
        if (CurrentBlur is not BlurBox b) return;
        b.StartSec = Position;
        if (b.EndSec < b.StartSec) b.EndSec = double.MaxValue;
        LoadBlurEditor();
        UpdateVisibility(Position);
    }

    private void OnBlurTo(object sender, RoutedEventArgs e)
    {
        if (CurrentBlur is not BlurBox b) return;
        b.EndSec = Position;
        if (b.StartSec > b.EndSec) b.StartSec = 0;
        LoadBlurEditor();
        UpdateVisibility(Position);
    }

    private void OnBlurAll(object sender, RoutedEventArgs e)
    {
        if (CurrentBlur is not BlurBox b) return;
        b.StartSec = 0;
        b.EndSec = double.MaxValue;
        LoadBlurEditor();
        UpdateVisibility(Position);
    }

    private void UpdateBlurVisual(BlurBox b)
    {
        if (!_blurVisuals.TryGetValue(b, out var v)) return;
        var a = b.Area;
        // The blurred copy is larger than the box and clipped to it, so the edges stay solid instead of fading out.
        double pad = Math.Ceiling(PreviewBlurRadius);
        Canvas.SetLeft(v.Clip, a.X);
        Canvas.SetTop(v.Clip, a.Y);
        v.Clip.Width = a.Width;
        v.Clip.Height = a.Height;
        Canvas.SetLeft(v.Blurred, -pad);
        Canvas.SetTop(v.Blurred, -pad);
        v.Blurred.Width = a.Width + 2 * pad;
        v.Blurred.Height = a.Height + 2 * pad;
        v.Brush.Viewbox = new Rect(a.X - pad, a.Y - pad, a.Width + 2 * pad, a.Height + 2 * pad);

        bool selected = CurrentBlur == b;
        Canvas.SetLeft(v.Frame, a.X);
        Canvas.SetTop(v.Frame, a.Y);
        v.Frame.Width = a.Width;
        v.Frame.Height = a.Height;
        // Only the box being edited gets an outline. The others show just their blur, as they will export,
        // but can still be clicked on the preview to pick them up again.
        v.Frame.BorderThickness = new Thickness(3);
        v.Frame.BorderBrush = selected ? Brushes.White : Brushes.Transparent;
        UpdateBlurVisibility(b, Position);
    }

    private void UpdateBlurVisibility(BlurBox b, double t)
    {
        if (!_blurVisuals.TryGetValue(b, out var v)) return;
        bool on = b.IsAt(t);
        v.Clip.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        // The selected box keeps its frame outside its time range, so it can still be found and moved.
        v.Frame.Visibility = _blurEditing && (on || CurrentBlur == b) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateBlurHandle()
    {
        if (!_blurEditing || CurrentBlur is not BlurBox b) { BlurHandle.Visibility = Visibility.Collapsed; return; }
        BlurHandle.Visibility = Visibility.Visible;
        Canvas.SetLeft(BlurHandle, b.Area.Right - 12);
        Canvas.SetTop(BlurHandle, b.Area.Bottom - 12);
    }

    private void OnBlurMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;
        var frame = (Border)sender;
        var b = (BlurBox)frame.Tag;
        BlurList.SelectedItem = b;
        _drag = Drag.Blur;
        _dragBlur = b;
        _dragStart = e.GetPosition(StageCanvas);
        _dragOrigin = b.Area.TopLeft;
        frame.CaptureMouse();
        e.Handled = true;
    }

    private void OnBlurResize(object sender, DragDeltaEventArgs e)
    {
        if (_busy || CurrentBlur is not BlurBox b) return;
        var a = b.Area;
        double w = Math.Clamp(Math.Round(a.Width + e.HorizontalChange), BlurBox.MinSize, _w - a.X);
        double h = Math.Clamp(Math.Round(a.Height + e.VerticalChange), BlurBox.MinSize, _h - a.Y);
        b.Area = new Rect(a.X, a.Y, w, h);
        UpdateBlurVisual(b);
        UpdateBlurHandle();
    }

    // ------------------------------------------------------------------ crop + size

    private void OnCropPreset(object sender, SelectionChangedEventArgs? e)
    {
        if (CropBox == null) return;
        _cropPreset = (CropPreset?)CropCombo.SelectedItem ?? CropPreset.All[0];
        if (!_cropPreset.IsOff)
        {
            double w = _w, h = _h;
            if (_cropPreset.IsFree) { w *= 0.8; h *= 0.8; }
            else if (w / h > _cropPreset.Aspect) w = h * _cropPreset.Aspect;
            else h = w / _cropPreset.Aspect;
            _crop = new Rect(Math.Round((_w - w) / 2), Math.Round((_h - h) / 2), Math.Round(w), Math.Round(h));
        }
        UpdateCropVisuals();
    }

    private void UpdateCropVisuals()
    {
        bool on = !_cropPreset.IsOff;
        CropBox.Visibility = CropMask.Visibility = CropHandle.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            Canvas.SetLeft(CropBox, _crop.X);
            Canvas.SetTop(CropBox, _crop.Y);
            CropBox.Width = _crop.Width;
            CropBox.Height = _crop.Height;
            Canvas.SetLeft(CropHandle, _crop.Right - 12);
            Canvas.SetTop(CropHandle, _crop.Bottom - 12);
            CropMask.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, _w, _h)), new RectangleGeometry(_crop));
        }
        foreach (var l in _layers) UpdateVisual(l);
        UpdateCropText();
    }

    private void OnSizeChanged(object sender, SelectionChangedEventArgs e) => UpdateCropText();

    private (int W, int H) OutputDimensions()
    {
        var a = VisibleArea;
        int w = (int)a.Width & ~1, h = (int)a.Height & ~1;
        var size = (OutputSize?)SizeCombo.SelectedItem ?? Sizes[0];
        if (size.Height > 0 && size.Height < h)
        {
            w = (int)Math.Round(w * (double)size.Height / h / 2) * 2;
            h = size.Height;
        }
        return (w, h);
    }

    private void UpdateCropText()
    {
        if (CropText == null) return;
        var (w, h) = OutputDimensions();
        CropText.Text = $"Output: {w} × {h} px." + (_cropPreset.IsOff ? "" : " Drag the box to move it, the corner to resize.");
        UpdateFileSize();
    }

    private void OnCropMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;
        _drag = Drag.Crop;
        _dragStart = e.GetPosition(StageCanvas);
        _dragOrigin = _crop.TopLeft;
        CropBox.CaptureMouse();
        e.Handled = true;
    }

    private void OnCropResize(object sender, DragDeltaEventArgs e)
    {
        double w = Math.Max(64, _crop.Width + e.HorizontalChange);
        double h = Math.Max(64, _crop.Height + e.VerticalChange);
        if (!_cropPreset.IsFree) h = w / _cropPreset.Aspect;
        double maxW = _w - _crop.X, maxH = _h - _crop.Y;
        if (w > maxW) { w = maxW; if (!_cropPreset.IsFree) h = w / _cropPreset.Aspect; }
        if (h > maxH) { h = maxH; if (!_cropPreset.IsFree) w = h * _cropPreset.Aspect; }
        _crop = new Rect(_crop.X, _crop.Y, Math.Round(w), Math.Round(h));
        UpdateCropVisuals();
    }

    private void OnStageMouseMove(object sender, MouseEventArgs e)
    {
        if (_drag == Drag.None) return;
        var p = e.GetPosition(StageCanvas);
        double dx = p.X - _dragStart.X, dy = p.Y - _dragStart.Y;
        if (_drag == Drag.Text && _dragLayer is TextLayer l)
        {
            if (l.CenterX && Math.Abs(dx) > 4)
            {
                l.CenterX = false;
                if (CurrentLayer == l) { _loadingUi = true; LayerCenter.IsChecked = false; _loadingUi = false; }
            }
            if (!l.CenterX) l.X = Math.Round(_dragOrigin.X + dx);
            l.Y = Math.Round(_dragOrigin.Y + dy);
            UpdateVisual(l);
        }
        else if (_drag == Drag.Blur && _dragBlur is BlurBox b)
        {
            double x = Math.Clamp(_dragOrigin.X + dx, 0, _w - b.Area.Width);
            double y = Math.Clamp(_dragOrigin.Y + dy, 0, _h - b.Area.Height);
            b.Area = new Rect(Math.Round(x), Math.Round(y), b.Area.Width, b.Area.Height);
            UpdateBlurVisual(b);
            UpdateBlurHandle();
        }
        else if (_drag == Drag.Crop)
        {
            double x = Math.Clamp(_dragOrigin.X + dx, 0, _w - _crop.Width);
            double y = Math.Clamp(_dragOrigin.Y + dy, 0, _h - _crop.Height);
            _crop = new Rect(Math.Round(x), Math.Round(y), _crop.Width, _crop.Height);
            UpdateCropVisuals();
        }
    }

    private void OnStageMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == Drag.None) return;
        _drag = Drag.None;
        _dragLayer = null;
        _dragBlur = null;
        Mouse.Capture(null);
    }

    // ------------------------------------------------------------------ file size

    private bool _sizeSync;

    /// <summary>How much of the original video bitrate a crop/resize still needs (smaller frames need fewer bits).</summary>
    private double PixelFactor()
    {
        if (_info == null) return 1;
        var (w, h) = OutputDimensions();
        double pixels = Math.Clamp((double)w * h / Math.Max(1, _info.Width * _info.Height), 0.05, 1);
        return 0.15 + 0.85 * pixels;
    }

    /// <summary>Estimated size at full quality for the kept time and output size. The slider is a percentage of this.</summary>
    private double FullQualityBytes() =>
        _info == null ? 0 : SizeTarget.FullKbps(_info, PixelFactor()) * 1000 / 8 * (_end - _start);

    private void UpdateFileSize()
    {
        if (FileSizeSlider == null || FileSizeText == null) return;
        if (_info == null || !_ready || _end <= _start) { FileSizeSlider.IsEnabled = false; return; }
        // Same floor rule as the main screen, measured against the full-quality size of this edit.
        var (ow, oh) = OutputDimensions();
        double floor = SizeTarget.FloorPercent(_info, PixelFactor(), ow, oh);
        _sizeSync = true;
        FileSizeSlider.Minimum = floor;
        if (FileSizeSlider.Value < floor) FileSizeSlider.Value = floor;
        _sizeSync = false;
        FileSizeSlider.IsEnabled = floor < 100 && !_busy;
        FileSizeHint.Text = floor >= 100
            ? $"Already as small as it can be without losing quality ({SizeTarget.Describe(_info)})."
            : $"Move slider to adjust export file size. Minimum size is capped to prevent poor quality.";
        OnFileSizeChanged(this, null!);
    }

    private void OnFileSizeChanged(object sender, RoutedPropertyChangedEventArgs<double>? e)
    {
        if (_sizeSync || FileSizeText == null || _info == null || !_ready) return;
        double pct = Math.Round(FileSizeSlider.Value);
        double bytes = FullQualityBytes() * pct / 100;
        FileSizeText.Text = pct >= 100
            ? $"Best quality, about {SizeTarget.FormatBytes(bytes)}"
            : $"About {SizeTarget.FormatBytes(bytes)} ({pct:0}%)";
    }

    /// <summary>Two-pass bitrate for the slider, or null at 100% (CRF 18 best quality).</summary>
    private Budget? TargetBudget()
    {
        if (_info == null || FileSizeSlider.Value >= 99.5) return null;
        double bytes = FullQualityBytes() * Math.Round(FileSizeSlider.Value) / 100;
        return SizeTarget.Plan(bytes, _end - _start, _info);
    }

    // ------------------------------------------------------------------ export

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string ext = FmtMkv.IsChecked == true ? "mkv" : "mp4";
        var dlg = new SaveFileDialog
        {
            Title = "Save video",
            Filter = $"{ext.ToUpperInvariant()} video|*.{ext}",
            DefaultExt = ext,
            InitialDirectory = Path.GetDirectoryName(_opt.Input),
            FileName = Path.GetFileNameWithoutExtension(_opt.Input) + " (edited)." + ext,
        };
        if (dlg.ShowDialog(this) != true) return;
        if (string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(_opt.Input), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Choose a different file name than the original video.", "Video Editor");
            return;
        }
        await ExportToAsync(dlg.FileName, ext == "mkv" ? ExportFormat.Mkv : ExportFormat.Mp4);
    }

    private async Task<bool> ExportToAsync(string output, ExportFormat format)
    {
        Pause();
        SetBusy(true);
        _exportCts = new CancellationTokenSource();
        ExportProgress.Value = 0;
        ExportText.Text = "Saving…";
        ExportText.Foreground = (Brush)FindResource("TextMuted");
        try
        {
            await RenderAsync(output, format, new Progress<double>(v => ExportProgress.Value = v), _exportCts.Token);
            _lastSaved = output;
            (Owner as MainWindow)?.MarkClipKept();
            ShowFileButton.Visibility = Visibility.Visible;
            double mb = new FileInfo(output).Length / 1024.0 / 1024.0;
            ExportText.Text = $"Saved {Path.GetFileName(output)} ({mb:0.0} MB).";
            ExportText.Foreground = Brushes.SeaGreen;
            return true;
        }
        catch (OperationCanceledException)
        {
            ExportText.Text = "Cancelled.";
            ExportProgress.Value = 0;
        }
        catch (Exception ex)
        {
            if (SelfTestOutput != null) App.Fail("Video editor export failed: " + ex);
            ExportText.Text = "Saving failed.";
            ExportProgress.Value = 0;
            string details = ex is FfmpegException fe ? "\n\n" + fe.Details : "";
            MessageBox.Show(this, ex.Message + details, "Video Editor", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _exportCts?.Dispose();
            _exportCts = null;
            SetBusy(false);
        }
        return false;
    }

    /// <summary>
    /// Captions are drawn by WPF into transparent PNGs (so they look exactly like the preview) and laid
    /// over the video with FFmpeg only during their time range. Then crop, resize and encode.
    /// </summary>
    private async Task RenderAsync(string output, ExportFormat format, IProgress<double> progress, CancellationToken ct)
    {
        double length = _end - _start;
        var area = VisibleArea;
        var (outW, outH) = OutputDimensions();
        var active = _layers.Where(l => !string.IsNullOrWhiteSpace(l.Text) && l.EndSec > _start && l.StartSec < _end).ToList();
        bool crop = !_cropPreset.IsOff;
        bool scale = outH != ((int)area.Height & ~1);
        var blurs = _blurs.Where(b => b.EndSec > _start && b.StartSec < _end).ToList();
        var kbps = TargetBudget();

        if (active.Count == 0 && blurs.Count == 0 && !crop && !scale)
        {
            // Only trimming: reuse the plain exporter (stream copy when nothing is cut).
            await Exporter.ExportAsync(new ExportOptions
            {
                Input = _opt.Input, Output = output, Format = format,
                Start = _start, End = _end, SourceDuration = _duration, SourceWidth = _w, Budget = kbps,
            }, progress, ct);
            return;
        }

        string work = Path.Combine(Path.GetTempPath(), "ScreenRecorderVideoEdit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var args = new List<string> { "-ss", Ffmpeg.Num(_start), "-t", Ffmpeg.Num(length), "-i", _opt.Input };

            for (int i = 0; i < active.Count; i++)
            {
                string png = Path.Combine(work, $"text{i}.png");
                RenderLayer(active[i], area, png);
                args.AddRange(new[] { "-i", png });
            }

            var graph = new System.Text.StringBuilder();
            string last = "0:v";
            // Blur first, on the full frame, so captions drawn over a blurred area stay sharp.
            int radius = BlurBox.Radius(_h);
            for (int i = 0; i < blurs.Count; i++)
            {
                var r = blurs[i].EvenArea(_w, _h);
                int luma = Math.Max(1, Math.Min(radius, Math.Min(r.Width, r.Height) / 2));
                int chroma = Math.Min(luma, Math.Min(r.Width, r.Height) / 4);   // 4:2:0 chroma planes are half size
                graph.Append($"[{last}]split[bm{i}][bs{i}];");
                graph.Append($"[bs{i}]crop={r.Width}:{r.Height}:{r.X}:{r.Y},");
                graph.Append($"boxblur=luma_radius={luma}:luma_power=2:chroma_radius={chroma}:chroma_power=2[bb{i}];");
                graph.Append($"[bm{i}][bb{i}]overlay={r.X}:{r.Y}:enable='{Between(blurs[i].StartSec, blurs[i].EndSec, length)}'[b{i}];");
                last = $"b{i}";
            }
            for (int i = 0; i < active.Count; i++)
            {
                graph.Append(CultureInfo.InvariantCulture, $"[{last}][{i + 1}:v]overlay=0:0:enable='{Between(active[i].StartSec, active[i].EndSec, length)}'[t{i}];");
                last = $"t{i}";
            }
            graph.Append($"[{last}]");
            var chain = new List<string>();
            if (crop) chain.Add($"crop={(int)area.Width & ~1}:{(int)area.Height & ~1}:{(int)area.X}:{(int)area.Y}");
            if (scale) chain.Add($"scale={outW}:{outH}:flags=lanczos");
            chain.Add("format=yuv420p");
            graph.Append(string.Join(",", chain)).Append("[out]");

            args.AddRange(new[] { "-filter_complex", graph.ToString(), "-map", "[out]", "-map", "0:a?" });
            await SizeTarget.EncodeAsync(args, output, format, kbps, length, work, progress, ct);
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

    /// <summary>FFmpeg timeline expression for a source-time range, relative to the trimmed clip.</summary>
    private string Between(double startSec, double endSec, double length)
    {
        double a = Math.Max(0, startSec - _start);
        double b = endSec == double.MaxValue ? length + 1 : Math.Min(length + 1, endSec - _start);
        return $"between(t,{Ffmpeg.Num(a)},{Ffmpeg.Num(b)})";
    }

    private void RenderLayer(TextLayer layer, Rect area, string path)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) layer.DrawOn(dc, area);
        var rtb = new RenderTargetBitmap(_w, _h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        using var fs = File.Create(path);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        enc.Save(fs);
    }

    private async Task RunSelfTestAsync(string output)
    {
        try
        {
            OnAddLayer(this, new RoutedEventArgs());
            LayerText.Text = "Self test";
            OnLayerPlace(new Button { Tag = "bottom" }, new RoutedEventArgs());
            CurrentLayer!.StartSec = 0.5;
            CurrentLayer.EndSec = 2;
            OnAddBlur(this, new RoutedEventArgs());
            CurrentBlur!.StartSec = 1;
            CurrentBlur.EndSec = 2.5;
            OnAddBlur(this, new RoutedEventArgs());   // second box, whole clip, in the top-left corner
            CurrentBlur!.Area = new Rect(0, 0, 120, 80);
            if (_blurs.Count != 2) App.Fail("Could not add blur boxes");
            OnBlurDone(this, new RoutedEventArgs());
            if (_blurVisuals.Values.Any(v => v.Frame.Visibility == Visibility.Visible) || BlurHandle.Visibility == Visibility.Visible)
                App.Fail("Blur outlines still showing after Done");
            // Short busy clip: crop, resize, trim to 3 s at 60%. Long, mostly still clip (like a real screen
            // recording): keep it whole and go to the lowest size the slider allows.
            bool longClip = _duration > 10;
            if (!longClip)
            {
                CropCombo.SelectedIndex = 2;   // square
                SizeCombo.SelectedIndex = 3;   // 480p
            }
            if (!longClip) { _start = 0.5; _end = 3.5; }
            await LoadInfoAsync();
            SyncTrim();
            string probe = $"{_info?.Width}x{_info?.Height} {_info?.Fps}fps {_info?.Duration:0.0}s {_info?.Bytes}B, {(_info == null ? "no info" : SizeTarget.Describe(_info))}";
            if (_info == null) App.Fail("Could not read the test clip's details");
            if (SizeTarget.FloorPercent(_info) > 70) App.Fail($"Main-screen floor too high: {SizeTarget.FloorPercent(_info)}% ({probe})");
            if (FileSizeSlider.Minimum >= 100) App.Fail($"File size slider has no range on the test clip ({probe})");
            if (longClip && FileSizeSlider.Minimum > 80) App.Fail($"Editor floor too high on a still clip: {FileSizeSlider.Minimum}% ({probe})");
            FileSizeSlider.Value = longClip ? FileSizeSlider.Minimum : Math.Max(FileSizeSlider.Minimum, 60);
            double expected = FullQualityBytes() * Math.Round(FileSizeSlider.Value) / 100;
            var kbps = TargetBudget();
            if (!await ExportToAsync(output, ExportFormat.Mp4)) App.Fail("Video editor export returned false");
            var (w, h) = OutputDimensions();
            long size = new FileInfo(output).Length;
            string report = $"[{probe}] floor={FileSizeSlider.Minimum}% video size={size} expected={expected:0} ({FileSizeText.Text}) budget={kbps} out={w}x{h}";
            if (Math.Abs(size - expected) > expected * 0.2) App.Fail("Two-pass size missed the target: " + report);
            App.Pass(report);
        }
        catch (Exception ex) { App.Fail("Video editor self-test failed: " + ex); }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        TextCard.IsEnabled = CropCard.IsEnabled = ExportButton.IsEnabled = FmtMp4.IsEnabled = FmtMkv.IsEnabled = !busy;
        FileSizeSlider.IsEnabled = !busy && FileSizeSlider.Minimum < 100 && _info != null;
        CancelButton.IsEnabled = busy;
        BlurCard.IsEnabled = !busy;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _exportCts?.Cancel();

    private void OnShowFile(object sender, RoutedEventArgs e)
    {
        if (_lastSaved != null && File.Exists(_lastSaved))
            try { Process.Start("explorer.exe", $"/select,\"{_lastSaved}\""); } catch { }
    }

    private static string Fmt(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}"
            : $"{t.Minutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
    }
}
