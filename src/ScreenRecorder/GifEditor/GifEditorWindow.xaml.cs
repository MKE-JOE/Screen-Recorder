using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenRecorder.Controls;
using ScreenRecorder.Services;

namespace ScreenRecorder.GifEditor;

public sealed class GifEditorOptions
{
    public required string Input { get; init; }
    public double Start { get; init; }
    public double Length { get; init; }
    public int Fps { get; init; } = 15;
    /// <summary>0 = original width.</summary>
    public int Width { get; init; } = 640;
    public int SourceWidth { get; init; }
    public TextOverlay? InitialText { get; init; }
    public string SuggestedName { get; init; } = "animation.gif";
}

/// <summary>Frame-by-frame GIF editor: strip of frames, timing tools, timed captions, crop, and optimised export.</summary>
public partial class GifEditorWindow : Window
{
    public sealed record FrameItem(string Number, string DelayText, BitmapSource Thumb);

    private readonly GifEditorOptions _opt;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ScreenRecorderGifEdit_" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _closing = new();
    private readonly DispatcherTimer _play = new();
    private readonly Dictionary<string, BitmapSource> _thumbs = new();
    private readonly Dictionary<TextLayer, OutlinedText> _visuals = new();
    private readonly Stack<(List<GifFrame> Frames, List<TextLayer> Layers)> _undo = new(), _redo = new();

    private List<GifFrame> _frames = new();
    private List<TextLayer> _layers = new();
    private int _current, _frameW = 640, _frameH = 360;
    private bool _loadingUi, _busy;
    private CropPreset _cropPreset = CropPreset.All[0];
    private Rect _crop;
    private CancellationTokenSource? _exportCts;
    private string? _lastSaved;

    // drag state
    private enum Drag { None, Text, Crop }
    private Drag _drag;
    private Point _dragStart;
    private Point _dragOrigin;
    private TextLayer? _dragLayer;

    /// <summary>Set by --selftest-gif: after loading, run the main editing tools and export here, then exit.</summary>
    public string? SelfTestOutput { get; init; }

    public GifEditorWindow(GifEditorOptions options)
    {
        InitializeComponent();
        _opt = options;
        LayerFont.ItemsSource = FontChoice.All;
        LayerColor.ItemsSource = TextColor.All;
        CropCombo.ItemsSource = CropPreset.All;
        CropCombo.SelectedIndex = 0;
        LoopCombo.ItemsSource = LoopChoice.All;
        LoopCombo.SelectedIndex = 0;
        _play.Tick += OnPlayTick;
        Transport.RewindButton.ToolTip = "Previous frame (hold to scan)";
        Transport.ForwardButton.ToolTip = "Next frame (hold to scan)";
        Transport.StartButton.ToolTip = "Jump to first frame";
        Transport.EndButton.ToolTip = "Jump to last frame";
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    // ------------------------------------------------------------------ loading

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetLoading(true, "Loading frames…");
        try
        {
            _frames = await GifRenderer.ExtractAsync(_opt.Input, _opt.Start, _opt.Length, _opt.Fps, _opt.Width, _opt.SourceWidth,
                _folder, new Progress<double>(v => LoadingProgress.Value = v), _closing.Token);
            if (_frames.Count == 0) throw new InvalidOperationException("No frames could be read from this part of the video.");

            var first = GifRenderer.Load(_frames[0].ImagePath);
            _frameW = first.PixelWidth;
            _frameH = first.PixelHeight;
            Stage.Width = _frameW;
            Stage.Height = _frameH;

            if (_opt.InitialText is { IsEmpty: false } t)
            {
                double scale = _opt.SourceWidth > 0 ? (double)_frameW / _opt.SourceWidth : 1;
                _layers.Add(new TextLayer
                {
                    Text = t.Text, Font = t.Font, FontSize = Math.Max(10, Math.Round(t.FontSize * scale)),
                    Color = TextColor.All.FirstOrDefault(c => c.Hex == t.ColorHex) ?? TextColor.All[0],
                    Outline = t.Outline, CenterX = t.CenterX, X = t.X * scale, Y = t.Y * scale,
                });
            }

            RefreshAll();
            ShowFrame(0);
            if (_layers.Count > 0) LayerList.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (SelfTestOutput != null)
        {
            App.Fail("GIF editor load failed: " + ex);
            return;
        }
        catch (Exception ex)
        {
            string details = ex is FfmpegException fe ? "\n\n" + fe.Details : "";
            MessageBox.Show(this, "Could not load frames: " + ex.Message + details, "GIF Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }
        finally { SetLoading(false); }

        if (SelfTestOutput != null) await RunSelfTestAsync(SelfTestOutput);
    }

    private async Task RunSelfTestAsync(string output)
    {
        try
        {
            int loaded = _frames.Count;
            OnAddLayer(this, new RoutedEventArgs());
            LayerText.Text = "Self test";
            OnLayerPlace(new Button { Tag = "bottom" }, new RoutedEventArgs());
            CropCombo.SelectedIndex = 2;   // square
            OnBoomerang(this, new RoutedEventArgs());
            OnReduce(this, new RoutedEventArgs());
            OnUndo(this, new RoutedEventArgs());
            OnRedo(this, new RoutedEventArgs());
            var crop = new Int32Rect((int)_crop.X, (int)_crop.Y, (int)_crop.Width, (int)_crop.Height);
            await GifRenderer.ExportAsync(_frames, _layers, crop, _frameW, _frameH, 0, true, output,
                new Progress<double>(_ => { }), CancellationToken.None);
            long size = new FileInfo(output).Length;
            if (size < 1000) throw new InvalidOperationException("GIF is empty");
            App.Pass($"gif loaded={loaded} frames={_frames.Count} size={size} crop={crop.Width}x{crop.Height} length={_frames.Sum(f => f.DelayMs)}ms");
        }
        catch (Exception ex)
        {
            App.Fail("GIF editor self-test failed: " + ex);
        }
    }

    private void SetLoading(bool on, string text = "")
    {
        LoadingOverlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        LoadingText.Text = text;
        LoadingProgress.Value = 0;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _play.Stop();
        _closing.Cancel();
        _exportCts?.Cancel();
        PreviewImage.Source = null;
        Dispatcher.BeginInvoke(() => { try { Directory.Delete(_folder, true); } catch { } }, DispatcherPriority.ApplicationIdle);
    }

    // ------------------------------------------------------------------ strip + preview

    private void RefreshAll(IEnumerable<int>? select = null)
    {
        var items = new List<FrameItem>(_frames.Count);
        for (int i = 0; i < _frames.Count; i++)
            items.Add(new FrameItem($"#{i + 1}", $"{_frames[i].DelayMs} ms", Thumb(_frames[i].ImagePath)));
        Strip.ItemsSource = items;
        if (select != null)
            foreach (int i in select.Where(i => i >= 0 && i < items.Count)) Strip.SelectedItems.Add(items[i]);

        FrameSlider.Maximum = Math.Max(0, _frames.Count - 1);
        double total = _frames.Sum(f => f.DelayMs) / 1000.0;
        LengthText.Text = $"{total:0.0} s · {_frames.Count} frames";
        LengthText.Foreground = total > GifRenderer.MaxSeconds + 0.001 ? Brushes.Firebrick : (Brush)FindResource("TextMain");

        LayerList.ItemsSource = null;
        LayerList.ItemsSource = _layers;
        RebuildLayerVisuals();
        ShowFrame(Math.Min(_current, _frames.Count - 1));
    }

    private BitmapSource Thumb(string path)
    {
        if (!_thumbs.TryGetValue(path, out var bmp))
            _thumbs[path] = bmp = GifRenderer.Load(path, 72);
        return bmp;
    }

    private void ShowFrame(int index)
    {
        if (_frames.Count == 0) return;
        _current = Math.Clamp(index, 0, _frames.Count - 1);
        PreviewImage.Source = GifRenderer.Load(_frames[_current].ImagePath);
        _loadingUi = true;
        FrameSlider.Value = _current;
        _loadingUi = false;
        FrameText.Text = $"Frame {_current + 1} / {_frames.Count}";
        foreach (var (layer, visual) in _visuals)
            visual.Visibility = layer.IsOnFrame(_current) && !string.IsNullOrWhiteSpace(layer.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingUi) return;
        ShowFrame((int)Math.Round(e.NewValue));
    }

    private void OnStripSelection(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[e.AddedItems.Count - 1] is FrameItem item && !_play.IsEnabled)
            ShowFrame(Strip.Items.IndexOf(item));
    }

    private void OnStripKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { OnDelete(sender, e); e.Handled = true; }
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_play.IsEnabled || _frames.Count == 0) return;
        _play.Interval = TimeSpan.FromMilliseconds(Math.Max(20, _frames[_current].DelayMs));
        _play.Start();
    }

    private void OnPause(object sender, RoutedEventArgs e) => _play.Stop();
    private void OnStop(object sender, RoutedEventArgs e) { _play.Stop(); ShowFrame(0); }

    private void OnJumpStart(object sender, RoutedEventArgs e) => ShowFrame(0);
    private void OnJumpEnd(object sender, RoutedEventArgs e) { _play.Stop(); ShowFrame(_frames.Count - 1); }

    // A GIF is at most 15 s, so rewind / fast-forward step one frame at a time (hold to scan).
    private void OnPrevFrame(object sender, RoutedEventArgs e) { _play.Stop(); ShowFrame(_current - 1); }
    private void OnNextFrame(object sender, RoutedEventArgs e) { _play.Stop(); ShowFrame(_current + 1); }

    private void OnPlayTick(object? sender, EventArgs e)
    {
        ShowFrame((_current + 1) % _frames.Count);
        _play.Interval = TimeSpan.FromMilliseconds(Math.Max(20, _frames[_current].DelayMs));
    }

    // ------------------------------------------------------------------ frame tools

    private List<int> Selected() =>
        Strip.SelectedItems.Cast<FrameItem>().Select(i => Strip.Items.IndexOf(i)).Where(i => i >= 0).OrderBy(i => i).ToList();

    /// <summary>Selected frames, or every frame when nothing (or just one) is selected.</summary>
    private List<int> SelectedOrAll()
    {
        var sel = Selected();
        return sel.Count > 1 ? sel : Enumerable.Range(0, _frames.Count).ToList();
    }

    private void Snapshot()
    {
        _undo.Push((_frames.ToList(), _layers.Select(l => l.Clone()).ToList()));
        _redo.Clear();
        if (_undo.Count > 100) TrimStack(_undo, 100);
    }

    private static void TrimStack<T>(Stack<T> s, int max)
    {
        var keep = s.Take(max).Reverse().ToList();
        s.Clear();
        foreach (var x in keep) s.Push(x);
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_busy || _undo.Count == 0) return;
        _redo.Push((_frames.ToList(), _layers.Select(l => l.Clone()).ToList()));
        (_frames, _layers) = _undo.Pop();
        RefreshAll();
        LoadLayerEditor();
    }

    private void OnRedo(object sender, RoutedEventArgs e)
    {
        if (_busy || _redo.Count == 0) return;
        _undo.Push((_frames.ToList(), _layers.Select(l => l.Clone()).ToList()));
        (_frames, _layers) = _redo.Pop();
        RefreshAll();
        LoadLayerEditor();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        if (sel.Count >= _frames.Count) { MessageBox.Show(this, "A GIF needs at least one frame.", "GIF Editor"); return; }
        Snapshot();
        foreach (int i in sel.OrderByDescending(i => i)) _frames.RemoveAt(i);
        _current = Math.Min(sel[0], _frames.Count - 1);
        RefreshAll(new[] { _current });
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 0) sel.Add(_current);
        Snapshot();
        int insertAt = sel[^1] + 1;
        _frames.InsertRange(insertAt, sel.Select(i => _frames[i]).ToList());
        RefreshAll(Enumerable.Range(insertAt, sel.Count));
    }

    private void OnMoveLeft(object sender, RoutedEventArgs e) => Move(-1);
    private void OnMoveRight(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int dir)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        if ((dir < 0 && sel[0] == 0) || (dir > 0 && sel[^1] == _frames.Count - 1)) return;
        Snapshot();
        var order = dir < 0 ? sel : sel.AsEnumerable().Reverse().ToList();
        foreach (int i in order)
            (_frames[i], _frames[i + dir]) = (_frames[i + dir], _frames[i]);
        _current = Math.Clamp(_current + dir, 0, _frames.Count - 1);
        RefreshAll(sel.Select(i => i + dir));
    }

    private void OnReverse(object sender, RoutedEventArgs e)
    {
        var idx = SelectedOrAll();
        if (idx.Count < 2) return;
        Snapshot();
        var items = idx.Select(i => _frames[i]).Reverse().ToList();
        for (int k = 0; k < idx.Count; k++) _frames[idx[k]] = items[k];
        RefreshAll(Selected());
    }

    private void OnBoomerang(object sender, RoutedEventArgs e)
    {
        if (_frames.Count < 3) return;
        Snapshot();
        var back = _frames.Skip(1).Take(_frames.Count - 2).Reverse().ToList();
        _frames.AddRange(back);
        RefreshAll();
    }

    private void OnReduce(object sender, RoutedEventArgs e)
    {
        var idx = SelectedOrAll();
        if (idx.Count < 2) return;
        Snapshot();
        var remove = new HashSet<int>();
        for (int k = 1; k < idx.Count; k += 2)
        {
            int keep = idx[k - 1], drop = idx[k];
            _frames[keep] = _frames[keep] with { DelayMs = _frames[keep].DelayMs + _frames[drop].DelayMs };
            remove.Add(drop);
        }
        _frames = _frames.Where((_, i) => !remove.Contains(i)).ToList();
        RefreshAll();
    }

    private void OnSlower(object sender, RoutedEventArgs e) => ScaleDelays(1.25);
    private void OnFaster(object sender, RoutedEventArgs e) => ScaleDelays(1 / 1.25);

    private void ScaleDelays(double factor)
    {
        var idx = SelectedOrAll();
        Snapshot();
        foreach (int i in idx) _frames[i] = _frames[i] with { DelayMs = RoundDelay(_frames[i].DelayMs * factor) };
        RefreshAll(Selected());
    }

    private void OnSetDelay(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DelayBox.Text.Trim(), out int ms) || ms <= 0)
        {
            MessageBox.Show(this, "Enter a delay in milliseconds, for example 100.", "GIF Editor");
            return;
        }
        var idx = Selected();
        if (idx.Count == 0) idx = Enumerable.Range(0, _frames.Count).ToList();
        Snapshot();
        foreach (int i in idx) _frames[i] = _frames[i] with { DelayMs = RoundDelay(ms) };
        RefreshAll(Selected());
    }

    /// <summary>GIF stores delays in 1/100 s and browsers treat anything under 20 ms as slow, so clamp to that.</summary>
    private static int RoundDelay(double ms) => Math.Clamp((int)Math.Round(ms / 10) * 10, 20, 10000);

    // ------------------------------------------------------------------ text layers

    private TextLayer? CurrentLayer => LayerList.SelectedItem as TextLayer;
    private Rect VisibleArea => _cropPreset.IsOff ? new Rect(0, 0, _frameW, _frameH) : _crop;

    private void OnAddLayer(object sender, RoutedEventArgs e)
    {
        Snapshot();
        var layer = new TextLayer
        {
            FontSize = Math.Clamp(Math.Round(_frameH / 8.0), 16, 120),
            Y = VisibleArea.Y + Math.Round(_frameH * 0.05),
            FromFrame = 0,
            ToFrame = int.MaxValue,
        };
        _layers.Add(layer);
        RefreshAll();
        LayerList.SelectedItem = layer;
        LayerText.Focus();
        LayerText.SelectAll();
    }

    private void OnRemoveLayer(object sender, RoutedEventArgs e)
    {
        if (CurrentLayer is not TextLayer l) return;
        Snapshot();
        _layers.Remove(l);
        RefreshAll();
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
            // Refresh the list label without losing the selection.
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
        l.FromFrame = _current;
        if (l.ToFrame < l.FromFrame) l.ToFrame = int.MaxValue;
        UpdateRangeText(l);
        ShowFrame(_current);
    }

    private void OnLayerTo(object sender, RoutedEventArgs e)
    {
        if (CurrentLayer is not TextLayer l) return;
        l.ToFrame = _current;
        if (l.FromFrame > l.ToFrame) l.FromFrame = 0;
        UpdateRangeText(l);
        ShowFrame(_current);
    }

    private void OnLayerAll(object sender, RoutedEventArgs e)
    {
        if (CurrentLayer is not TextLayer l) return;
        l.FromFrame = 0;
        l.ToFrame = int.MaxValue;
        UpdateRangeText(l);
        ShowFrame(_current);
    }

    private void UpdateRangeText(TextLayer l)
    {
        LayerFromText.Text = $"#{l.FromFrame + 1}";
        LayerToText.Text = l.ToFrame == int.MaxValue ? "last" : $"#{l.ToFrame + 1}";
    }

    private void RebuildLayerVisuals()
    {
        foreach (var v in _visuals.Values) StageCanvas.Children.Remove(v);
        _visuals.Clear();
        foreach (var layer in _layers)
        {
            var v = new OutlinedText { Cursor = Cursors.SizeAll, Tag = layer };
            v.MouseLeftButtonDown += OnTextMouseDown;
            StageCanvas.Children.Add(v);   // above the crop box so text stays draggable
            _visuals[layer] = v;
            UpdateVisual(layer);
        }
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
        v.Visibility = l.IsOnFrame(_current) && !string.IsNullOrWhiteSpace(l.Text) ? Visibility.Visible : Visibility.Collapsed;
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

    // ------------------------------------------------------------------ crop

    private void OnCropPreset(object sender, SelectionChangedEventArgs e)
    {
        _cropPreset = (CropPreset?)CropCombo.SelectedItem ?? CropPreset.All[0];
        if (!_cropPreset.IsOff)
        {
            double w = _frameW, h = _frameH;
            if (_cropPreset.IsFree) { w *= 0.8; h *= 0.8; }
            else if (w / h > _cropPreset.Aspect) w = h * _cropPreset.Aspect;
            else h = w / _cropPreset.Aspect;
            _crop = new Rect(Math.Round((_frameW - w) / 2), Math.Round((_frameH - h) / 2), Math.Round(w), Math.Round(h));
        }
        UpdateCropVisuals();
    }

    private void UpdateCropVisuals()
    {
        bool on = !_cropPreset.IsOff;
        var vis = on ? Visibility.Visible : Visibility.Collapsed;
        CropBox.Visibility = CropMask.Visibility = CropHandle.Visibility = vis;
        if (on)
        {
            Canvas.SetLeft(CropBox, _crop.X);
            Canvas.SetTop(CropBox, _crop.Y);
            CropBox.Width = _crop.Width;
            CropBox.Height = _crop.Height;
            Canvas.SetLeft(CropHandle, _crop.Right - 8);
            Canvas.SetTop(CropHandle, _crop.Bottom - 8);
            CropMask.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, _frameW, _frameH)), new RectangleGeometry(_crop));
            CropText.Text = $"{(int)_crop.Width} × {(int)_crop.Height} px. Drag the box to move it, the corner to resize.";
        }
        else CropText.Text = "Pick a shape, then drag the box to choose what stays.";
        foreach (var l in _layers) UpdateVisual(l);
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
        double w = Math.Max(32, _crop.Width + e.HorizontalChange);
        double h = Math.Max(32, _crop.Height + e.VerticalChange);
        if (!_cropPreset.IsFree) h = w / _cropPreset.Aspect;
        double maxW = _frameW - _crop.X, maxH = _frameH - _crop.Y;
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
            if (l.CenterX && Math.Abs(dx) > 3)
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
            double x = Math.Clamp(_dragOrigin.X + dx, 0, _frameW - _crop.Width);
            double y = Math.Clamp(_dragOrigin.Y + dy, 0, _frameH - _crop.Height);
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

    // ------------------------------------------------------------------ export

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_busy || _frames.Count == 0) return;
        double total = _frames.Sum(f => f.DelayMs) / 1000.0;
        if (total > GifRenderer.MaxSeconds + 0.001)
        {
            MessageBox.Show(this, $"This GIF is {total:0.0} seconds long. GIFs are limited to 15 seconds.\n\n" +
                "Delete some frames, use Faster, or remove the boomerang to shorten it.", "GIF Editor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "Save GIF",
            Filter = "GIF image|*.gif",
            DefaultExt = "gif",
            FileName = _opt.SuggestedName,
            InitialDirectory = Path.GetDirectoryName(_opt.Input),
        };
        if (dlg.ShowDialog(this) != true) return;

        _play.Stop();
        SetBusy(true);
        _exportCts = new CancellationTokenSource();
        ExportProgress.Value = 0;
        ExportText.Text = "Saving…";
        ExportText.Foreground = (Brush)FindResource("TextMuted");
        try
        {
            Int32Rect? crop = _cropPreset.IsOff ? null : new Int32Rect((int)_crop.X, (int)_crop.Y, (int)_crop.Width, (int)_crop.Height);
            var loop = (LoopChoice?)LoopCombo.SelectedItem ?? LoopChoice.All[0];
            await GifRenderer.ExportAsync(_frames, _layers, crop, _frameW, _frameH, loop.FfmpegLoop,
                dither: QualityCombo.SelectedIndex == 0, dlg.FileName,
                new Progress<double>(v => ExportProgress.Value = v), _exportCts.Token);

            long bytes = new FileInfo(dlg.FileName).Length;
            double mb = bytes / 1024.0 / 1024.0;
            _lastSaved = dlg.FileName;
            (Owner as MainWindow)?.MarkClipKept();
            ShowFileButton.Visibility = Visibility.Visible;
            ExportText.Text = $"Saved {Path.GetFileName(dlg.FileName)}: {mb:0.0} MB. " + mb switch
            {
                > 15 => "Too big for X (15 MB) and Discord (10 MB). Try Reduce, a smaller width, a crop, or \"Smaller file\".",
                > 10 => "Fine for X (15 MB) but over Discord's 10 MB limit.",
                _ => "Small enough for Discord and X.",
            };
            ExportText.Foreground = mb > 10 ? Brushes.DarkOrange : Brushes.SeaGreen;
        }
        catch (OperationCanceledException)
        {
            ExportText.Text = "Cancelled.";
            ExportProgress.Value = 0;
        }
        catch (Exception ex)
        {
            ExportText.Text = "Saving failed.";
            ExportProgress.Value = 0;
            string details = ex is FfmpegException fe ? "\n\n" + fe.Details : "";
            MessageBox.Show(this, ex.Message + details, "GIF Editor", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _exportCts.Dispose();
            _exportCts = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Tools.IsEnabled = !busy;
        Strip.IsEnabled = !busy;
        TextPanel.IsEnabled = !busy;
        CropCombo.IsEnabled = !busy;
        ExportButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _exportCts?.Cancel();

    private void OnShowFile(object sender, RoutedEventArgs e)
    {
        if (_lastSaved != null && File.Exists(_lastSaved))
            try { Process.Start("explorer.exe", $"/select,\"{_lastSaved}\""); } catch { }
    }
}
