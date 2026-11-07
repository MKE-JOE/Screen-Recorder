using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenRecorder.Controls;
using ScreenRecorder.GifEditor;
using ScreenRecorder.Services;

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

/// <summary>Simple video editor: trim, crop, resize and timed captions, exported as high-quality MP4 or MKV.</summary>
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
    private int _w = 1280, _h = 720;
    private double _duration, _start, _end;
    private bool _loadingUi, _playing, _syncing, _busy;
    private CropPreset _cropPreset = CropPreset.All[0];
    private Rect _crop;
    private CancellationTokenSource? _exportCts;
    private string? _lastSaved;

    private enum Drag { None, Text, Crop }
    private Drag _drag;
    private Point _dragStart, _dragOrigin;
    private TextLayer? _dragLayer;

    /// <summary>Set by --selftest-video: after loading, add a caption and a crop, export here, then exit.</summary>
    public string? SelfTestOutput { get; init; }

    public VideoEditorWindow(VideoEditorOptions options)
    {
        InitializeComponent();
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

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_playing) { Pause(); return; }
        if (!_previewOk) return;
        if (Position < _start || Position >= _end - 0.05) Seek(_start);
        Media.Play();
        _playing = true;
        PlayButton.Content = "❚❚ Pause";
    }

    private void Pause()
    {
        Media.Pause();
        _playing = false;
        PlayButton.Content = "▶ Play";
    }

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
        var kbps = TargetBudget();

        if (active.Count == 0 && !crop && !scale)
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
            for (int i = 0; i < active.Count; i++)
            {
                double a = Math.Max(0, active[i].StartSec - _start);
                double b = active[i].EndSec == double.MaxValue ? length + 1 : Math.Min(length + 1, active[i].EndSec - _start);
                graph.Append(CultureInfo.InvariantCulture, $"[{last}][{i + 1}:v]overlay=0:0:enable='between(t,{Ffmpeg.Num(a)},{Ffmpeg.Num(b)})'[t{i}];");
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
