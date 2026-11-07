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

    private readonly RegionWindow _region = new();
    private readonly ControlBar _bar = new();
    private const int HotkeyRecord = 1, HotkeyPause = 2, HotkeyStop = 3, HotkeyFrame = 4;
    private bool _frameHidden;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private Mode _mode = Mode.Idle;
    private Recorder? _recorder;
    private CancellationTokenSource? _countdownCts, _exportCts;
    private bool _ffmpegReady, _syncingUi, _syncingTimeline, _isPlaying, _closeAfterSave;
    private string? _videoPath, _lastExport;
    private double _duration;
    private VideoInfo? _info;
    private bool _probeDone;
    private int _videoW, _videoH;

    /// <summary>Where recordings wait when autosave is off. Cleared of old leftovers at startup.</summary>
    private static string UnsavedDir => Path.Combine(Ffmpeg.AppDataDir, "Unsaved");
    /// <summary>The loaded clip when it lives in <see cref="UnsavedDir"/> (autosave off). Deleted once it's replaced or the app closes.</summary>
    private string? _unsavedPath;
    /// <summary>True once that clip has been exported, so throwing it away needs no warning.</summary>
    private bool _unsavedExported;

    private static string OutputDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ScreenGank");

    public MainWindow()
    {
        InitializeComponent();
        _tick.Tick += OnTick;
        _region.CaptureRectChanged += (_, _) => OnRegionChanged();

        SizeCombo.ItemsSource = SizePresets;
        SizeCombo.SelectedIndex = 2;

        Loaded += OnLoaded;
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
            RegisterHotkeys();
        };
        _bar.RecordRequested += () =>
        {
            if (_mode == Mode.Idle) OnRecord(this, new RoutedEventArgs());
            else if (_mode == Mode.Paused) OnPause(this, new RoutedEventArgs());
        };
        _bar.PauseRequested += () => { if (_mode is Mode.Recording or Mode.Paused) OnPause(this, new RoutedEventArgs()); };
        _bar.StopRequested += () => { if (_mode is Mode.Countdown or Mode.Recording or Mode.Paused) OnStop(this, new RoutedEventArgs()); };
        _bar.ShowAppRequested += RestoreMainWindow;
        _bar.ToggleFrameRequested += ToggleFrame;
        _region.HideRequested += ToggleFrame;
        Closing += OnClosing;
        AutoSaveCheck.IsChecked = _settings.AutoSave;
        // Fit small or high-DPI screens (e.g. 1280×720 effective at 150% scaling).
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(Width, wa.Width);
        Height = Math.Min(Height, wa.Height);
        CleanUnsavedLeftovers();
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
            var encoder = await VideoEncoder.BestAsync();   // tested once; a graphics-card encoder avoids stutter
            SetStatus($"Ready · recording with {encoder}");
            if (App.SelfTestGif is (string gifIn, string gifOut))
            {
                new GifEditor.GifEditorWindow(new GifEditor.GifEditorOptions { Input = gifIn, Start = 0.5, Length = 3, Fps = 15, Width = 320, SourceWidth = 640 })
                { Owner = this, SelfTestOutput = gifOut }.Show();
                return;
            }
            if (App.SelfTestVideo is (string vidIn, string vidOut))
            {
                new VideoEditor.VideoEditorWindow(new VideoEditor.VideoEditorOptions { Input = vidIn, Start = 0, End = 0 })
                { Owner = this, SelfTestOutput = vidOut }.Show();
                return;
            }
            if (App.SelfTest)
            {
                // Typical screen recordings must be shrinkable well below 100%: a 5 MB / 30 s clip with sound, and a long,
                // mostly still one with silent audio (6 MB / 4 min, the case that used to get stuck).
                double floor = Math.Max(
                    SizeTarget.FloorPercent(new VideoInfo(1920, 1080, 30, 60, true, 5_000_000, 1000, 320)),
                    SizeTarget.FloorPercent(new VideoInfo(1920, 1080, 240, 60, true, 6_000_000, 185, 2)));
                if (floor > 65) App.Fail($"File size floor too high for a typical screen recording: {floor}%");
                var displays = Displays.GetAll();
                var target = Displays.Resolve(_region.GetCaptureRect());
                App.Pass($"encoder={encoder}\nsizeFloor={floor}%\nffmpeg={Ffmpeg.ExePath}\ndisplays={displays.Count}\ntarget={target.Rect} dda={target.DdaOutputIndex}\n" +
                         $"audioSources={AudioCombo.Items.Count}");
            }
        }
        catch (Exception ex)
        {
            if (App.SelfTest) App.Fail(ex.ToString());
            SetStatus("Encoder not available");
            MessageBox.Show(this, "Could not prepare FFmpeg:\n\n" + ex.Message, "ScreenGank", MessageBoxButton.OK, MessageBoxImage.Error);
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
        var prev = AudioCombo.SelectedItem as AudioSourceItem;
        var sources = await AudioDevices.GetSourcesAsync();
        AudioCombo.ItemsSource = sources;
        AudioCombo.SelectedItem = sources.FirstOrDefault(i => prev != null && i.SystemId == prev.SystemId && i.MicId == prev.MicId)
                                  ?? sources[0];
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
        if (AreaMode && !_frameHidden) _region.Show(); else _region.Hide();
        _bar.SetFrameState(AreaMode, _frameHidden);
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
        if (!ConfirmDiscardUnsaved("Start a new recording")) return;

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
            SystemAudioDeviceId = (AudioCombo.SelectedItem as AudioSourceItem)?.SystemId,
            MicrophoneDeviceId = (AudioCombo.SelectedItem as AudioSourceItem)?.MicId,
            Encoder = await VideoEncoder.BestAsync(),
        };
        string work = Path.Combine(Ffmpeg.AppDataDir, "parts", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        _recorder = new Recorder(options, work);
        _recorder.Failed += msg => Dispatcher.BeginInvoke(() => OnRecorderFailed(msg));
        SetPerformanceMode(true);

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
            MessageBox.Show(this, ex.Message, "ScreenGank", MessageBoxButton.OK, MessageBoxImage.Error);
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

        bool autoSave = _settings.AutoSave || _closeAfterSave;   // closing mid-recording always keeps the file
        string dir = autoSave ? OutputDir : UnsavedDir;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"Recording {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
        try
        {
            var rec = _recorder;
            var length = rec.Elapsed;
            await rec.FinishAsync(path);
            _recorder = null;
            rec.WriteReport(Path.Combine(Ffmpeg.AppDataDir, "last-recording.txt"), length);
            string health = rec.HealthSummary();
            DiscardUnsaved();
            EndRecordingUi((reason ?? (autoSave ? "Saved " + Path.GetFileName(path) : "Recording ready. It isn't saved yet: export it to keep it."))
                           + (health == "smooth" ? " · Recording was smooth" : health.Length > 0 ? " · ⚠ " + health : ""));
            LoadVideo(path);
            if (!autoSave) { _unsavedPath = path; _unsavedExported = false; }
        }
        catch (Exception ex)
        {
            _recorder = null;
            EndRecordingUi("Saving failed");
            MessageBox.Show(this, ex.Message, "ScreenGank", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        if (_closeAfterSave) Close();
    }

    private bool _perfMode;

    /// <summary>While recording: this app (it paces the audio) runs above normal priority, unthrottled, with 1 ms timers.</summary>
    private void SetPerformanceMode(bool on)
    {
        if (on == _perfMode) return;
        _perfMode = on;
        if (on) Native.Performance.DisableThrottling(Process.GetCurrentProcess());
        else Native.Performance.RestoreSelf();
        Native.Performance.HighResTimers(on);
    }

    private void EndRecordingUi(string status)
    {
        SetPerformanceMode(false);
        _bar.SetWarning(false);
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
        MessageBox.Show(this, message, "ScreenGank", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                MessageBox.Show(this, ex.Message, "ScreenGank", MessageBoxButton.OK, MessageBoxImage.Error);
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
                Seek(0);
                break;
        }
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_videoPath == null || _mode != Mode.Idle) return;
        double pos = Media.Position.TotalSeconds;
        if (pos >= _duration - 0.05) Seek(0);
        Media.Play();
        _isPlaying = true;
    }

    private void OnJumpStart(object sender, RoutedEventArgs e) => Seek(0);
    private void OnJumpEnd(object sender, RoutedEventArgs e) { PausePlayback(); Seek(_duration); }

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
        _info = null;
        _probeDone = false;
        UpdateFileSize();
        _ = ProbeAsync(path);
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
        SyncTimeline(0);
        UpdateFileSize();
        UpdateUi();
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        Seek(_duration);
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
            _bar.SetWarning(_mode == Mode.Recording && _recorder.RecentLag > 0.05);
            RecBadgeText.Text = (_mode == Mode.Paused ? "PAUSED " : "REC ") + t.ToString(@"hh\:mm\:ss");
            RecDot.Opacity = _mode == Mode.Paused ? 0.3 : (DateTime.Now.Millisecond < 500 ? 1 : 0.35);
            if (_mode == Mode.Recording && t >= Recorder.MaxDuration)
                _ = FinishRecordingAsync("Reached the 2-hour limit and saved");
            return;
        }

        if (_videoPath != null && _isPlaying)
        {
            double pos = Media.Position.TotalSeconds;
            if (pos >= _duration)
            {
                PausePlayback();
                Seek(_duration);
            }
            else SyncTimeline(pos);
        }
    }

    // ------------------------------------------------------------------ export

    private ExportFormat SelectedFormat => FmtGif.IsChecked == true ? ExportFormat.Gif : FmtMkv.IsChecked == true ? ExportFormat.Mkv : ExportFormat.Mp4;

    private void OnFormatChanged(object sender, RoutedEventArgs e)
    {
        if (GifOptions == null) return;
        bool gif = FmtGif.IsChecked == true;
        GifOptions.Visibility = GifHint.Visibility = gif ? Visibility.Visible : Visibility.Collapsed;
        SizeOptions.Visibility = FileSizeHint.Visibility = gif ? Visibility.Collapsed : Visibility.Visible;
    }

    private double ClipLength => _duration > 0 ? _duration : _info?.Duration ?? 0;

    private async Task ProbeAsync(string path)
    {
        var info = await SizeTarget.ProbeAsync(path);
        if (_videoPath != path) return;
        _info = info;
        _probeDone = true;
        UpdateFileSize();
    }

    /// <summary>Sets the File size slider's range for the loaded clip and shows the projected size.</summary>
    private void UpdateFileSize()
    {
        if (FileSizeSlider == null) return;
        if (_info == null || ClipLength <= 0)
        {
            FileSizeSlider.IsEnabled = false;
            FileSizeText.Text = _videoPath == null ? "Original size" : _probeDone ? "Size unknown" : "Measuring…";
            if (_probeDone && _videoPath != null) FileSizeHint.Text = "Couldn't read this video's details, so it can only be exported at its original size.";
            return;
        }
        double floor = SizeTarget.FloorPercent(_info);
        _syncingUi = true;
        FileSizeSlider.Minimum = floor;
        FileSizeSlider.Value = Math.Max(floor, FileSizeSlider.Value);
        _syncingUi = false;
        FileSizeSlider.IsEnabled = floor < 100;
        FileSizeHint.Text = floor >= 100
            ? $"This recording is already as small as it can be without losing quality ({SizeTarget.Describe(_info)})."
            : $"Move slider to adjust export file size. Minimum size is capped to prevent poor quality.";
        OnFileSizeChanged(this, null!);
    }

    private void OnFileSizeChanged(object sender, RoutedPropertyChangedEventArgs<double>? e)
    {
        if (_syncingUi || FileSizeText == null || _info == null) return;
        double pct = Math.Round(FileSizeSlider.Value);
        double bytes = SizeTarget.SourceBytes(_info, ClipLength) * pct / 100;
        FileSizeText.Text = pct >= 100
            ? $"{SizeTarget.FormatBytes(bytes)} (original)"
            : $"about {SizeTarget.FormatBytes(bytes)} ({pct:0}%)";
    }

    /// <summary>Video bitrate for the slider's setting, or null at 100% (keep the original).</summary>
    private Budget? TargetBudget()
    {
        if (_info == null || FileSizeSlider.Value >= 99.5) return null;
        double bytes = SizeTarget.SourceBytes(_info, ClipLength) * Math.Round(FileSizeSlider.Value) / 100;
        return SizeTarget.Plan(bytes, ClipLength, _info);
    }

    /// <summary>GIFs are capped at 15 seconds: longer clips use 15 seconds from the playhead.</summary>
    private (double Start, double Length) GifRange()
    {
        double max = GifEditor.GifRenderer.MaxSeconds, len = ClipLength;
        if (len <= max + 0.05) return (0, len);
        double start = Math.Clamp(Media.Position.TotalSeconds, 0, len - max);
        return (start, max);
    }

    private bool ConfirmGifRange(string title, double start, double length) =>
        ClipLength <= GifEditor.GifRenderer.MaxSeconds + 0.05 ||
        MessageBox.Show(this, $"GIFs are limited to 15 seconds and this clip is {Fmt(ClipLength)}.\n\n" +
            $"{Fmt(start)} to {Fmt(start + length)} will be used. To use a different part, move the playhead there first.",
            title, MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK;

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_videoPath == null || _mode != Mode.Idle) return;
        var fmt = SelectedFormat;
        string ext = fmt switch { ExportFormat.Gif => "gif", ExportFormat.Mkv => "mkv", _ => "mp4" };
        double start = 0, end = ClipLength;
        if (fmt == ExportFormat.Gif)
        {
            var (gs, gl) = GifRange();
            if (!ConfirmGifRange("Export GIF", gs, gl)) return;
            start = gs; end = gs + gl;
        }

        var dlg = new SaveFileDialog
        {
            Title = "Export " + ext.ToUpperInvariant(),
            Filter = $"{ext.ToUpperInvariant()} file|*.{ext}",
            DefaultExt = ext,
            InitialDirectory = _unsavedPath != null && _unsavedPath == _videoPath ? OutputDir : Path.GetDirectoryName(_videoPath),
            FileName = Path.GetFileNameWithoutExtension(_videoPath) + " (export)." + ext,
        };
        if (dlg.ShowDialog(this) != true) return;
        if (string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(_videoPath), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Choose a different file name than the original recording.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var opt = new ExportOptions
        {
            Input = _videoPath,
            Output = dlg.FileName,
            Format = fmt,
            Start = start,
            End = end,
            SourceDuration = ClipLength,
            GifFps = int.Parse((string)((ComboBoxItem)GifFpsCombo.SelectedItem).Tag),
            GifWidth = int.Parse((string)((ComboBoxItem)GifWidthCombo.SelectedItem).Tag),
            SourceWidth = _info?.Width ?? _videoW,
            Budget = fmt == ExportFormat.Gif ? null : TargetBudget(),
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
            MarkClipKept();
            SetStatus($"Exported {Path.GetFileName(dlg.FileName)} ({SizeTarget.FormatBytes(new FileInfo(dlg.FileName).Length)})");
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

    private void OnOpenEditor(object sender, RoutedEventArgs e)
    {
        if (_videoPath == null || _mode != Mode.Idle) return;
        if (SelectedFormat == ExportFormat.Gif) { OpenGifEditor(); return; }

        PausePlayback();
        new VideoEditor.VideoEditorWindow(new VideoEditor.VideoEditorOptions
        {
            Input = _videoPath,
            Start = 0,
            End = ClipLength,
            Format = SelectedFormat,
            SizePercent = FileSizeSlider.Value,
        })
        { Owner = this }.Show();
    }

    private void OpenGifEditor()
    {
        var (start, length) = GifRange();
        if (!ConfirmGifRange("GIF Editor", start, length)) return;

        PausePlayback();
        var editor = new GifEditor.GifEditorWindow(new GifEditor.GifEditorOptions
        {
            Input = _videoPath!,
            Start = start,
            Length = length,
            Fps = int.Parse((string)((ComboBoxItem)GifFpsCombo.SelectedItem).Tag),
            Width = int.Parse((string)((ComboBoxItem)GifWidthCombo.SelectedItem).Tag),
            SourceWidth = _info?.Width ?? _videoW,
            SuggestedName = Path.GetFileNameWithoutExtension(_videoPath) + ".gif",
        })
        { Owner = this };
        editor.Show();
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
        if (dlg.ShowDialog(this) == true && ConfirmDiscardUnsaved("Open another video"))
        {
            DiscardUnsaved();
            LoadVideo(dlg.FileName);
        }
    }

    private void OnShowFolder(object sender, RoutedEventArgs e)
    {
        string? file = _lastExport ?? (_unsavedPath == _videoPath ? null : _videoPath);
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
        RewindButton.IsEnabled = ForwardButton.IsEnabled = StartButton.IsEnabled = EndButton.IsEnabled = idle && hasVideo;
        PauseButton.IsEnabled = _mode is Mode.Recording or Mode.Paused || (idle && hasVideo);
        StopButton.IsEnabled = _mode is Mode.Countdown or Mode.Recording or Mode.Paused || (idle && hasVideo);
        Timeline.IsEnabled = idle && hasVideo;
        EditPanel.IsEnabled = _mode is Mode.Idle or Mode.Exporting;
        ExportButton.IsEnabled = idle && hasVideo;
        EditorButton.IsEnabled = idle && hasVideo;
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
        UnregisterHotkeys();
        var failed = new List<string>();
        foreach (var (id, key) in new[] { (HotkeyRecord, _settings.RecordKey), (HotkeyPause, _settings.PauseKey), (HotkeyStop, _settings.StopKey), (HotkeyFrame, _settings.ToggleFrameKey) })
        {
            if (!NativeMethods.RegisterHotKey(hwnd, id, key.Modifiers | NativeMethods.MOD_NOREPEAT, key.VirtualKey))
                failed.Add(key.ToString());
        }
        HotkeyText.Text = failed.Count == 0
            ? $"Hotkeys: {_settings.RecordKey} record · {_settings.PauseKey} pause · {_settings.StopKey} stop · {_settings.ToggleFrameKey} frame"
            : "In use by another app, pick others: " + string.Join(", ", failed);
        HotkeyText.Foreground = failed.Count == 0 ? (Brush)FindResource("TextMuted") : new SolidColorBrush(Color.FromRgb(0xC0, 0x26, 0x2D));
        _bar.SetHotkeyHints(_settings.RecordKey.ToString(), _settings.PauseKey.ToString(), _settings.StopKey.ToString(), _settings.ToggleFrameKey.ToString());
        _region.SetHotkeyHints(_settings.RecordKey.ToString(), _settings.PauseKey.ToString(), _settings.StopKey.ToString(), _settings.ToggleFrameKey.ToString());
    }

    private void UnregisterHotkeys()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        foreach (int id in new[] { HotkeyRecord, HotkeyPause, HotkeyStop, HotkeyFrame }) NativeMethods.UnregisterHotKey(hwnd, id);
    }

    private void OnChangeHotkeys(object sender, RoutedEventArgs e)
    {
        // Release the current keys so they can be pressed in the dialog without triggering anything.
        UnregisterHotkeys();
        RestoreMainWindow();
        var dlg = new HotkeyDialog(_settings) { Owner = this, Topmost = true };   // never hidden behind the capture frame
        if (dlg.ShowDialog() == true)
        {
            _settings.RecordKey = dlg.RecordKey;
            _settings.PauseKey = dlg.PauseKey;
            _settings.StopKey = dlg.StopKey;
            _settings.ToggleFrameKey = dlg.ToggleFrameKey;
            _settings.Save();
        }
        RegisterHotkeys();
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
            case HotkeyFrame:
                ToggleFrame();
                break;
            case HotkeyStop:
                if (_mode is Mode.Countdown or Mode.Recording or Mode.Paused) OnStop(this, new RoutedEventArgs());
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>Hide or show the capture frame. A hidden frame still marks the area that gets recorded.</summary>
    private void ToggleFrame()
    {
        if (!AreaMode) return;
        _frameHidden = !_frameHidden;
        if (_frameHidden) _region.Hide(); else _region.Show();
        _bar.SetFrameState(true, _frameHidden);
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
            if (MessageBox.Show(this, "A recording is in progress. Stop and save it before closing?", "ScreenGank",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _closeAfterSave = true;
                await FinishRecordingAsync();
            }
            return;
        }
        if (_mode == Mode.Finishing) { e.Cancel = true; _closeAfterSave = true; return; }
        if (!_closeAfterSave && !ConfirmDiscardUnsaved("Close the app")) { e.Cancel = true; return; }

        _exportCts?.Cancel();
        _countdownCts?.Cancel();
        Media.Close();
        _region.Close();
        _bar.Close();
        UnregisterHotkeys();
        DiscardUnsaved();
    }

    // ------------------------------------------------------------------ autosave

    private void OnAutoSaveChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.AutoSave = AutoSaveCheck.IsChecked == true;
        _settings.Save();
    }

    /// <summary>Called when the loaded clip has been exported (here or from an editor), so it's no longer at risk.</summary>
    public void MarkClipKept() => _unsavedExported = true;

    /// <summary>Asks before an unsaved, never-exported recording would be thrown away.</summary>
    private bool ConfirmDiscardUnsaved(string action)
    {
        if (_unsavedPath == null || _unsavedExported || !File.Exists(_unsavedPath) || App.SelfTest) return true;
        return MessageBox.Show(this,
            $"{action}? The current recording hasn't been saved or exported and will be discarded.\n\n" +
            "To keep it, choose No and press Export… (MP4 at 100% saves it unchanged).",
            "Unsaved recording", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void DiscardUnsaved()
    {
        if (_unsavedPath == null) return;
        string path = _unsavedPath;
        _unsavedPath = null;
        if (_videoPath == path) Media.Close();
        try { File.Delete(path); } catch { /* still open in an editor: cleaned up on a later start */ }
    }

    /// <summary>Removes unsaved clips left over from earlier sessions (kept a week in case the app crashed).</summary>
    private static void CleanUnsavedLeftovers()
    {
        try
        {
            if (!Directory.Exists(UnsavedDir)) return;
            foreach (var f in Directory.GetFiles(UnsavedDir))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7))
                    try { File.Delete(f); } catch { }
        }
        catch { }
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
