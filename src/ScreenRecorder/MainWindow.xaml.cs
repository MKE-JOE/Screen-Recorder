using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenRecorder.Native;
using ScreenRecorder.Services;

namespace ScreenRecorder;

public partial class MainWindow : Window
{
    private enum Mode { Idle, Countdown, Recording, Paused, Finishing, Exporting }

    private sealed record SizePreset(string Name, int W, int H) { public override string ToString() => Name; }
    private sealed record ColorChoice(string Name, string Hex, string OutlineHex) { public override string ToString() => Name; }
    private const string AreaItem = "Selected area (drag the frame)";

    private static readonly SizePreset[] SizePresets =
    {
        new("Custom", 0, 0),
        new("1920 × 1080", 1920, 1080),
        new("1280 × 720", 1280, 720),
        new("854 × 480", 854, 480),
        new("1080 × 1080", 1080, 1080),
        new("800 × 600", 800, 600),
    };

    private static readonly ColorChoice[] Colors =
    {
        new("White", "FFFFFF", "000000"),
        new("Yellow", "FFE14D", "000000"),
        new("Black", "000000", "FFFFFF"),
        new("Red", "FF3B30", "000000"),
        new("Cyan", "3FE0FF", "000000"),
        new("Lime", "7CFF4F", "000000"),
    };

    private readonly RegionWindow _region = new();
    private readonly ControlBar _bar = new();
    private const int HotkeyRecord = 1, HotkeyPause = 2, HotkeyStop = 3;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly TextOverlay _overlay = new();
    private Mode _mode = Mode.Idle;
    private Recorder? _recorder;
    private CancellationTokenSource? _countdownCts, _exportCts;
    private bool _ffmpegReady, _syncingUi, _syncingTimeline, _isPlaying, _closeAfterSave, _sizeTouched;
    private string? _videoPath, _lastExport;
    private double _duration, _trimStart, _trimEnd;
    private int _videoW, _videoH;
    private Point? _dragStart;
    private double _dragOrigX, _dragOrigY;

    private static string OutputDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Screen Recorder");

    public MainWindow()
    {
        InitializeComponent();
        _tick.Tick += OnTick;
        _region.CaptureRectChanged += (_, _) => OnRegionChanged();

        SizeCombo.ItemsSource = SizePresets;
        SizeCombo.SelectedIndex = 2;
        FontCombo.ItemsSource = FontChoice.All;
        FontCombo.SelectedItem = _overlay.Font;
        ColorCombo.ItemsSource = Colors;
        ColorCombo.SelectedIndex = 0;

        Loaded += OnLoaded;
        SourceInitialized += (_, _) => RegisterHotkeys();
        _bar.RecordRequested += () =>
        {
            if (_mode == Mode.Idle) OnRecord(this, new RoutedEventArgs());
            else if (_mode == Mode.Paused) OnPause(this, new RoutedEventArgs());
        };
        _bar.PauseRequested += () => { if (_mode is Mode.Recording or Mode.Paused) OnPause(this, new RoutedEventArgs()); };
        _bar.StopRequested += () => { if (_mode is Mode.Countdown or Mode.Recording or Mode.Paused) OnStop(this, new RoutedEventArgs()); };
        _bar.ShowAppRequested += RestoreMainWindow;
        Closing += OnClosing;
    }

    // ------------------------------------------------------------------ startup

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _region.Owner = null;
        _region.Show();
        _region.SetCaptureSize(1280, 720);
        _bar.Show();
        LoadDisplays();
        await LoadAudioDevicesAsync();
        _tick.Start();

        SetStatus("Preparing encoder…");
        UpdateUi();
        try
        {
            await Ffmpeg.EnsureAsync();
            _ffmpegReady = true;
            SetStatus("Ready");
            if (App.SelfTest)
            {
                var displays = Displays.GetAll();
                var target = Displays.Resolve(_region.GetCaptureRect());
                App.Pass($"ffmpeg={Ffmpeg.ExePath}\ndisplays={displays.Count}\ntarget={target.Rect} dda={target.DdaOutputIndex}\n" +
                         $"playback={AudioCombo.Items.Count} mics={MicCombo.Items.Count}");
            }
        }
        catch (Exception ex)
        {
            if (App.SelfTest) App.Fail(ex.ToString());
            SetStatus("Encoder not available");
            MessageBox.Show(this, "Could not prepare FFmpeg:\n\n" + ex.Message, "Screen Recorder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        UpdateUi();
    }

    private void LoadDisplays()
    {
        object? prev = CaptureCombo.SelectedItem;
        var items = new List<object> { AreaItem };
        items.AddRange(Displays.GetAll());
        CaptureCombo.ItemsSource = items;
        CaptureCombo.SelectedItem = items.FirstOrDefault(i => Equals(i, prev) ||
            (i is DisplayInfo d && prev is DisplayInfo p && d.DeviceName == p.DeviceName)) ?? items[0];
    }

    private async Task LoadAudioDevicesAsync()
    {
        string? audioId = (AudioCombo.SelectedItem as AudioDeviceItem)?.Id;
        string? micId = (MicCombo.SelectedItem as AudioDeviceItem)?.Id;
        var playback = await AudioDevices.GetPlaybackDevicesAsync();
        var mics = await AudioDevices.GetMicrophonesAsync();
        AudioCombo.ItemsSource = playback;
        MicCombo.ItemsSource = mics;
        AudioCombo.SelectedItem = playback.FirstOrDefault(d => audioId != null && d.Id == audioId) ?? playback[0];
        MicCombo.SelectedItem = mics.FirstOrDefault(d => micId != null && d.Id == micId) ?? mics[0];
    }

    private async void OnRefreshDevices(object sender, RoutedEventArgs e)
    {
        LoadDisplays();
        await LoadAudioDevicesAsync();
    }

    // ------------------------------------------------------------------ capture area

    private bool AreaMode => CaptureCombo.SelectedItem is string;

    private void OnCaptureModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (AreaMode) _region.Show(); else _region.Hide();
        SizeCombo.IsEnabled = AreaMode && _mode == Mode.Idle;
    }

    private void OnSizePresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi || !IsLoaded) return;
        if (SizeCombo.SelectedItem is SizePreset { W: > 0 } p)
        {
            _syncingUi = true;
            _region.SetCaptureSize(p.W, p.H);
            _syncingUi = false;
        }
    }

    private void OnRegionChanged()
    {
        if (_syncingUi) return;
        var r = _region.GetCaptureRect();
        var match = SizePresets.FirstOrDefault(p => p.W == r.Width && p.H == r.Height) ?? SizePresets[0];
        if (!Equals(SizeCombo.SelectedItem, match))
        {
            _syncingUi = true;
            SizeCombo.SelectedItem = match;
            _syncingUi = false;
        }
    }

    // ------------------------------------------------------------------ recording

    private async void OnRecord(object sender, RoutedEventArgs e)
    {
        if (_mode != Mode.Idle || !_ffmpegReady) return;

        RECT rect = CaptureCombo.SelectedItem is DisplayInfo d ? d.Bounds : _region.GetCaptureRect();
        var target = Displays.Resolve(rect);

        Directory.CreateDirectory(OutputDir);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(OutputDir)!);
            if (drive.AvailableFreeSpace < 3L * 1024 * 1024 * 1024 &&
                MessageBox.Show(this, $"Only {drive.AvailableFreeSpace / 1024 / 1024} MB free on {drive.Name}. A long recording may not fit.\n\nRecord anyway?",
                    "Low disk space", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }
        catch { }

        PausePlayback();
        _mode = Mode.Countdown;
        if (AreaMode) _region.SetRecording(true);
        WindowHelpers.ExcludeFromCapture(new WindowInteropHelper(this).Handle, true);
        SetStatus("Get ready…");
        if (MinimizeCheck.IsChecked == true) WindowState = WindowState.Minimized;
        UpdateUi();

        _countdownCts = new CancellationTokenSource();
        bool go = await CountdownWindow.RunAsync(target.Rect, _countdownCts.Token);
        _countdownCts.Dispose();
        _countdownCts = null;
        if (!go) { EndRecordingUi("Cancelled"); return; }

        var options = new RecordingOptions
        {
            Target = target,
            Fps = int.Parse((string)((ComboBoxItem)FpsCombo.SelectedItem).Tag),
            SystemAudioDeviceId = (AudioCombo.SelectedItem as AudioDeviceItem)?.Id,
            MicrophoneDeviceId = (MicCombo.SelectedItem as AudioDeviceItem)?.Id,
        };
        string work = Path.Combine(Ffmpeg.AppDataDir, "parts", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        _recorder = new Recorder(options, work);
        _recorder.Failed += msg => Dispatcher.BeginInvoke(() => OnRecorderFailed(msg));

        SetStatus("Starting…");
        try
        {
            await _recorder.StartAsync();
            _mode = Mode.Recording;
            SetStatus($"Recording {target.Width}×{target.Height}");
        }
        catch (Exception ex)
        {
            _recorder = null;
            EndRecordingUi("Could not start recording");
            MessageBox.Show(this, ex.Message, "Screen Recorder", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        UpdateUi();
    }

    private async Task FinishRecordingAsync(string? reason = null)
    {
        if (_recorder == null || _mode is Mode.Finishing) return;
        _mode = Mode.Finishing;
        SetStatus("Saving recording…");
        UpdateUi();

        string path = Path.Combine(OutputDir, $"Recording {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
        try
        {
            var rec = _recorder;
            await rec.FinishAsync(path);
            _recorder = null;
            EndRecordingUi(reason ?? "Saved " + Path.GetFileName(path));
            LoadVideo(path);
        }
        catch (Exception ex)
        {
            _recorder = null;
            EndRecordingUi("Saving failed");
            MessageBox.Show(this, ex.Message, "Screen Recorder", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        if (_closeAfterSave) Close();
    }

    private void EndRecordingUi(string status)
    {
        _mode = Mode.Idle;
        _region.SetRecording(false);
        RestoreMainWindow();
        WindowHelpers.ExcludeFromCapture(new WindowInteropHelper(this).Handle, false);
        SetStatus(status);
        UpdateUi();
    }

    private async void OnRecorderFailed(string message)
    {
        if (_mode is not (Mode.Recording or Mode.Paused)) return;
        await FinishRecordingAsync("Recording stopped early");
        MessageBox.Show(this, message, "Screen Recorder", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ------------------------------------------------------------------ transport buttons

    private async void OnPause(object sender, RoutedEventArgs e)
    {
        if (_recorder != null && _mode == Mode.Recording)
        {
            PauseButton.IsEnabled = false;
            await _recorder.PauseAsync();
            _mode = Mode.Paused;
            SetStatus("Paused. Press Pause again to resume.");
            UpdateUi();
        }
        else if (_recorder != null && _mode == Mode.Paused)
        {
            PauseButton.IsEnabled = false;
            try
            {
                await _recorder.ResumeAsync();
                _mode = Mode.Recording;
                SetStatus("Recording");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Screen Recorder", MessageBoxButton.OK, MessageBoxImage.Error);
                await FinishRecordingAsync("Saved what was recorded");
                return;
            }
            UpdateUi();
        }
        else
        {
            PausePlayback();
        }
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case Mode.Countdown:
                _countdownCts?.Cancel();
                break;
            case Mode.Recording:
            case Mode.Paused:
                await FinishRecordingAsync();
                break;
            default:
                if (_videoPath == null) return;
                PausePlayback();
                Seek(_trimStart);
                break;
        }
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_videoPath == null || _mode != Mode.Idle) return;
        double pos = Media.Position.TotalSeconds;
        if (pos < _trimStart || pos >= _trimEnd - 0.05) Seek(_trimStart);
        Media.Play();
        _isPlaying = true;
    }

    private void OnRewind(object sender, RoutedEventArgs e) => Seek(Media.Position.TotalSeconds - 5);
    private void OnForward(object sender, RoutedEventArgs e) => Seek(Media.Position.TotalSeconds + 5);

    private void PausePlayback()
    {
        if (_videoPath == null) return;
        Media.Pause();
        _isPlaying = false;
    }

    private void Seek(double seconds)
    {
        if (_videoPath == null) return;
        seconds = Math.Clamp(seconds, 0, _duration);
        Media.Position = TimeSpan.FromSeconds(seconds);
        SyncTimeline(seconds);
    }

    // ------------------------------------------------------------------ playback

    private void LoadVideo(string path)
    {
        _videoPath = path;
        _isPlaying = false;
        Media.Volume = 0;
        Media.Source = new Uri(path);
        Media.Play();      // opens the file; paused again in MediaOpened so the first frame shows
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        Media.Pause();
        Media.Position = TimeSpan.Zero;
        Media.Volume = 1;
        _videoW = Math.Max(2, Media.NaturalVideoWidth);
        _videoH = Math.Max(2, Media.NaturalVideoHeight);
        _duration = Media.NaturalDuration.HasTimeSpan ? Media.NaturalDuration.TimeSpan.TotalSeconds : 0;
        VideoHost.Width = _videoW;
        VideoHost.Height = _videoH;
        PreviewBox.Visibility = Visibility.Visible;
        Placeholder.Visibility = Visibility.Collapsed;

        Timeline.Maximum = Math.Max(0.1, _duration);
        _trimStart = 0;
        _trimEnd = _duration;
        if (!_sizeTouched)
        {
            _syncingUi = true;
            SizeSlider.Maximum = Math.Max(240, _videoH / 2.0);
            SizeSlider.Value = Math.Clamp(Math.Round(_videoH / 10.0), 24, 160);
            _syncingUi = false;
        }
        _overlay.Y = Math.Round(_videoH * 0.05);
        SyncTrimUi();
        SyncTimeline(0);
        RefreshOverlay();
        UpdateUi();
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        Seek(_trimEnd);
    }

    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        SetStatus("Preview unavailable for this file (export still works)");
        _isPlaying = false;
    }

    private void OnTimelineChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingTimeline || _videoPath == null) return;
        Media.Position = TimeSpan.FromSeconds(e.NewValue);
        PositionText.Text = Fmt(e.NewValue);
    }

    private void SyncTimeline(double seconds)
    {
        _syncingTimeline = true;
        Timeline.Value = seconds;
        _syncingTimeline = false;
        PositionText.Text = Fmt(seconds);
        DurationText.Text = Fmt(_duration);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_recorder != null && _mode is Mode.Recording or Mode.Paused or Mode.Finishing)
        {
            var t = _recorder.Elapsed;
            TimerText.Text = t.ToString(@"hh\:mm\:ss");
            UpdateBar();
            RecBadgeText.Text = (_mode == Mode.Paused ? "PAUSED " : "REC ") + t.ToString(@"hh\:mm\:ss");
            RecDot.Opacity = _mode == Mode.Paused ? 0.3 : (DateTime.Now.Millisecond < 500 ? 1 : 0.35);
            if (_mode == Mode.Recording && t >= Recorder.MaxDuration)
                _ = FinishRecordingAsync("Reached the 2-hour limit and saved");
            return;
        }

        if (_videoPath != null && _isPlaying)
        {
            double pos = Media.Position.TotalSeconds;
            if (pos >= _trimEnd)
            {
                PausePlayback();
                Seek(_trimEnd);
            }
            else SyncTimeline(pos);
        }
    }

    // ------------------------------------------------------------------ trim

    private void OnSetTrimStart(object sender, RoutedEventArgs e)
    {
        _trimStart = Math.Min(Media.Position.TotalSeconds, _trimEnd - 0.1);
        _trimStart = Math.Max(0, _trimStart);
        SyncTrimUi();
    }

    private void OnSetTrimEnd(object sender, RoutedEventArgs e)
    {
        _trimEnd = Math.Max(Media.Position.TotalSeconds, _trimStart + 0.1);
        _trimEnd = Math.Min(_duration, _trimEnd);
        SyncTrimUi();
    }

    private void OnResetTrim(object sender, RoutedEventArgs e)
    {
        _trimStart = 0;
        _trimEnd = _duration;
        SyncTrimUi();
    }

    private void SyncTrimUi()
    {
        Timeline.SelectionStart = _trimStart;
        Timeline.SelectionEnd = _trimEnd;
        TrimStartText.Text = Fmt(_trimStart);
        TrimEndText.Text = Fmt(_trimEnd);
        TrimLengthText.Text = Fmt(_trimEnd - _trimStart);
    }

    // ------------------------------------------------------------------ text overlay

    private void OnOverlayChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender == SizeSlider && !_syncingUi) _sizeTouched = true;
        RefreshOverlay();
    }

    private void RefreshOverlay()
    {
        if (!IsLoaded) return;
        _overlay.Text = OverlayInput.Text;
        _overlay.Font = (FontChoice?)FontCombo.SelectedItem ?? FontChoice.All[0];
        _overlay.FontSize = Math.Round(SizeSlider.Value);
        var color = (ColorChoice?)ColorCombo.SelectedItem ?? Colors[0];
        _overlay.ColorHex = color.Hex;
        _overlay.OutlineHex = color.OutlineHex;
        _overlay.Outline = OutlineCheck.IsChecked == true;
        _overlay.CenterX = CenterCheck.IsChecked == true;
        SizeValue.Text = _overlay.FontSize.ToString(CultureInfo.InvariantCulture);

        OverlayText.Text = _overlay.Text.Replace("\r\n", "\n").TrimEnd('\n');
        OverlayText.FontFamily = new FontFamily(_overlay.Font.WpfFamily);
        OverlayText.FontWeight = _overlay.Font.Bold ? FontWeights.Bold : FontWeights.Normal;
        OverlayText.FontSize = _overlay.FontSize;
        OverlayText.Fill = new SolidColorBrush(ParseHex(_overlay.ColorHex));
        OverlayText.Stroke = new SolidColorBrush(ParseHex(_overlay.OutlineHex));
        OverlayText.StrokeThickness = _overlay.Outline ? _overlay.OutlineWidth : 0;
        OverlayText.Visibility = _overlay.IsEmpty || _videoPath == null ? Visibility.Collapsed : Visibility.Visible;
        PositionOverlay();
    }

    private void PositionOverlay()
    {
        OverlayText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = OverlayText.DesiredSize.Width, h = OverlayText.DesiredSize.Height;
        _overlay.Y = Math.Clamp(_overlay.Y, 0, Math.Max(0, _videoH - h));
        if (_overlay.CenterX) _overlay.X = Math.Max(0, (_videoW - w) / 2);
        _overlay.X = Math.Clamp(_overlay.X, 0, Math.Max(0, _videoW - w));
        Canvas.SetLeft(OverlayText, _overlay.X);
        Canvas.SetTop(OverlayText, _overlay.Y);
    }

    private void OnOverlayPlace(object sender, RoutedEventArgs e)
    {
        OverlayText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double h = OverlayText.DesiredSize.Height, margin = Math.Round(_videoH * 0.05);
        _overlay.Y = (string)((Button)sender).Tag switch
        {
            "top" => margin,
            "middle" => Math.Round((_videoH - h) / 2),
            _ => Math.Round(_videoH - h - margin),
        };
        PositionOverlay();
    }

    private void OnOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(OverlayCanvas);
        _dragOrigX = _overlay.X;
        _dragOrigY = _overlay.Y;
        OverlayText.CaptureMouse();
        e.Handled = true;
    }

    private void OnOverlayMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not Point start) return;
        var p = e.GetPosition(OverlayCanvas);
        double dx = p.X - start.X, dy = p.Y - start.Y;
        if (Math.Abs(dx) > 3 && CenterCheck.IsChecked == true)
        {
            CenterCheck.IsChecked = false;   // free horizontal placement
            _overlay.CenterX = false;
        }
        if (!_overlay.CenterX) _overlay.X = Math.Round(_dragOrigX + dx);
        _overlay.Y = Math.Round(_dragOrigY + dy);
        PositionOverlay();
    }

    private void OnOverlayMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        OverlayText.ReleaseMouseCapture();
    }

    // ------------------------------------------------------------------ export

    private ExportFormat SelectedFormat => FmtGif.IsChecked == true ? ExportFormat.Gif : FmtMkv.IsChecked == true ? ExportFormat.Mkv : ExportFormat.Mp4;

    private void OnFormatChanged(object sender, RoutedEventArgs e)
    {
        if (GifOptions != null) GifOptions.Visibility = FmtGif.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_videoPath == null || _mode != Mode.Idle) return;
        var fmt = SelectedFormat;
        string ext = fmt switch { ExportFormat.Gif => "gif", ExportFormat.Mkv => "mkv", _ => "mp4" };
        double length = _trimEnd - _trimStart;

        if (fmt == ExportFormat.Gif && length > 60 &&
            MessageBox.Show(this, $"This GIF will be {Fmt(length)} long, which can make a very large file. Continue?",
                "Export GIF", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var dlg = new SaveFileDialog
        {
            Title = "Export " + ext.ToUpperInvariant(),
            Filter = $"{ext.ToUpperInvariant()} file|*.{ext}",
            DefaultExt = ext,
            InitialDirectory = Path.GetDirectoryName(_videoPath),
            FileName = Path.GetFileNameWithoutExtension(_videoPath) + " (edited)." + ext,
        };
        if (dlg.ShowDialog(this) != true) return;
        if (string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(_videoPath), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Choose a different file name than the original recording.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        RefreshOverlay();
        bool useText = !_overlay.IsEmpty && (fmt == ExportFormat.Gif || TextOnVideoCheck.IsChecked == true);
        var opt = new ExportOptions
        {
            Input = _videoPath,
            Output = dlg.FileName,
            Format = fmt,
            Start = _trimStart,
            End = _trimEnd,
            SourceDuration = _duration,
            Overlay = useText ? _overlay : null,
            GifFps = int.Parse((string)((ComboBoxItem)GifFpsCombo.SelectedItem).Tag),
            GifWidth = int.Parse((string)((ComboBoxItem)GifWidthCombo.SelectedItem).Tag),
            SourceWidth = _videoW,
        };

        PausePlayback();
        _mode = Mode.Exporting;
        _exportCts = new CancellationTokenSource();
        ExportProgress.Value = 0;
        SetStatus("Exporting…");
        UpdateUi();
        try
        {
            await Exporter.ExportAsync(opt, new Progress<double>(v => ExportProgress.Value = v), _exportCts.Token);
            _lastExport = dlg.FileName;
            SetStatus("Exported " + Path.GetFileName(dlg.FileName));
        }
        catch (OperationCanceledException)
        {
            ExportProgress.Value = 0;
            SetStatus("Export cancelled");
        }
        catch (Exception ex)
        {
            ExportProgress.Value = 0;
            SetStatus("Export failed");
            string details = ex is FfmpegException fe ? "\n\n" + fe.Details : "";
            MessageBox.Show(this, ex.Message + details, "Export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _exportCts.Dispose();
            _exportCts = null;
            _mode = Mode.Idle;
            UpdateUi();
        }
    }

    private void OnCancelExport(object sender, RoutedEventArgs e) => _exportCts?.Cancel();

    private void OnOpenVideo(object sender, RoutedEventArgs e)
    {
        if (_mode != Mode.Idle) return;
        var dlg = new OpenFileDialog
        {
            Title = "Open a video",
            Filter = "Videos|*.mp4;*.mkv;*.mov;*.m4v|All files|*.*",
            InitialDirectory = Directory.Exists(OutputDir) ? OutputDir : null,
        };
        if (dlg.ShowDialog(this) == true) LoadVideo(dlg.FileName);
    }

    private void OnShowFolder(object sender, RoutedEventArgs e)
    {
        string? file = _lastExport ?? _videoPath;
        try
        {
            if (file != null && File.Exists(file))
                Process.Start("explorer.exe", $"/select,\"{file}\"");
            else
            {
                Directory.CreateDirectory(OutputDir);
                Process.Start("explorer.exe", $"\"{OutputDir}\"");
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ misc

    private void UpdateUi()
    {
        bool idle = _mode == Mode.Idle;
        bool recording = _mode is Mode.Countdown or Mode.Recording or Mode.Paused or Mode.Finishing;
        bool hasVideo = _videoPath != null;

        CapturePanel.IsEnabled = idle;
        SizeCombo.IsEnabled = idle && AreaMode;
        RecordButton.IsEnabled = idle && _ffmpegReady;
        PlayButton.IsEnabled = idle && hasVideo;
        RewindButton.IsEnabled = ForwardButton.IsEnabled = idle && hasVideo;
        PauseButton.IsEnabled = _mode is Mode.Recording or Mode.Paused || (idle && hasVideo);
        StopButton.IsEnabled = _mode is Mode.Countdown or Mode.Recording or Mode.Paused || (idle && hasVideo);
        Timeline.IsEnabled = idle && hasVideo;
        EditPanel.IsEnabled = _mode is Mode.Idle or Mode.Exporting;
        ExportButton.IsEnabled = idle && hasVideo;
        CancelExportButton.IsEnabled = _mode == Mode.Exporting;

        RecBadge.Visibility = recording && _mode != Mode.Countdown ? Visibility.Visible : Visibility.Collapsed;
        if (recording) { PreviewBox.Visibility = Visibility.Collapsed; Placeholder.Visibility = Visibility.Collapsed; }
        else
        {
            PreviewBox.Visibility = hasVideo ? Visibility.Visible : Visibility.Collapsed;
            Placeholder.Visibility = hasVideo ? Visibility.Collapsed : Visibility.Visible;
        }
        if (!recording && _recorder == null) TimerText.Text = "00:00:00";
        UpdateBar();
    }

    private void SetStatus(string text) => StatusText.Text = text;

    // ------------------------------------------------------------------ hotkeys + floating bar

    private void RegisterHotkeys()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        const uint mods = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT;
        var failed = new List<string>();
        if (!NativeMethods.RegisterHotKey(hwnd, HotkeyRecord, mods, 0x52)) failed.Add("Ctrl+Alt+R");
        if (!NativeMethods.RegisterHotKey(hwnd, HotkeyPause, mods, 0x50)) failed.Add("Ctrl+Alt+P");
        if (!NativeMethods.RegisterHotKey(hwnd, HotkeyStop, mods, 0x53)) failed.Add("Ctrl+Alt+S");
        HotkeyText.Text = failed.Count == 0
            ? "Hotkeys: Ctrl+Alt+R record · Ctrl+Alt+P pause · Ctrl+Alt+S stop"
            : "Hotkeys in use by another app: " + string.Join(", ", failed);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY) return IntPtr.Zero;
        handled = true;
        switch (wParam.ToInt32())
        {
            case HotkeyRecord:
                if (_mode == Mode.Idle) OnRecord(this, new RoutedEventArgs());
                else if (_mode == Mode.Paused) OnPause(this, new RoutedEventArgs());
                break;
            case HotkeyPause:
                if (_mode is Mode.Recording or Mode.Paused) OnPause(this, new RoutedEventArgs());
                break;
            case HotkeyStop:
                if (_mode is Mode.Countdown or Mode.Recording or Mode.Paused) OnStop(this, new RoutedEventArgs());
                break;
        }
        return IntPtr.Zero;
    }

    private void RestoreMainWindow()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    private void UpdateBar()
    {
        bool live = _mode is Mode.Recording or Mode.Paused or Mode.Finishing;
        string time = _mode switch
        {
            Mode.Countdown => "Starting",
            Mode.Finishing => "Saving…",
            Mode.Exporting => "Exporting",
            _ when live && _recorder != null => _recorder.Elapsed.ToString(@"hh\:mm\:ss"),
            _ => _ffmpegReady ? "Ready" : "Wait…",
        };
        _bar.SetState(
            canRecord: (_mode == Mode.Idle && _ffmpegReady) || _mode == Mode.Paused,
            canPause: _mode is Mode.Recording or Mode.Paused,
            canStop: _mode is Mode.Countdown or Mode.Recording or Mode.Paused,
            live: live, paused: _mode == Mode.Paused, time: time);
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_mode is Mode.Recording or Mode.Paused)
        {
            e.Cancel = true;
            if (MessageBox.Show(this, "A recording is in progress. Stop and save it before closing?", "Screen Recorder",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _closeAfterSave = true;
                await FinishRecordingAsync();
            }
            return;
        }
        if (_mode == Mode.Finishing) { e.Cancel = true; _closeAfterSave = true; return; }

        _exportCts?.Cancel();
        _countdownCts?.Cancel();
        Media.Close();
        _region.Close();
        _bar.Close();
        var hwnd = new WindowInteropHelper(this).Handle;
        foreach (int id in new[] { HotkeyRecord, HotkeyPause, HotkeyStop }) NativeMethods.UnregisterHotKey(hwnd, id);
    }

    private static string Fmt(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}"
            : $"{t.Minutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
    }

    private static Color ParseHex(string hex) =>
        Color.FromRgb(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
}
