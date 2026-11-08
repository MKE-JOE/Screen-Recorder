using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ScreenRecorder.Native;

namespace ScreenRecorder;

/// <summary>
/// Small always-on-top bar with Record / Pause / Stop and the timer. It can be dragged anywhere
/// and is hidden from the recording, so the main window can stay minimized.
/// </summary>
public partial class ControlBar : Window
{
    public event Action? RecordRequested, PauseRequested, StopRequested, ShowAppRequested, ToggleFrameRequested;
    private string _frameKey = "";
    private bool _frameHidden;

    public ControlBar()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            WindowHelpers.SetExStyle(hwnd, NativeMethods.WS_EX_TOOLWINDOW, true);
            WindowHelpers.ExcludeFromCapture(hwnd, true);
        };
        WindowHelpers.KeepOnTop(this);
        Loaded += (_, _) =>
        {
            // Start at the top centre of the primary screen's work area.
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Top + 8;
        };
    }

    public void SetHotkeyHints(string record, string pause, string stop, string frame)
    {
        _frameKey = frame;
        UpdateFrameTip();
        RecordBtn.ToolTip = $"Record ({record})";
        PauseBtn.ToolTip = $"Pause / resume ({pause})";
        StopBtn.ToolTip = $"Stop and save ({stop})";
    }

    public void SetFrameState(bool available, bool hidden)
    {
        _frameHidden = hidden;
        FrameBtn.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        FrameIcon.Opacity = hidden ? 0.45 : 1;
        UpdateFrameTip();
    }

    private void UpdateFrameTip() =>
        FrameBtn.ToolTip = (_frameHidden ? "Show the capture frame" : "Hide the capture frame") + (_frameKey.Length > 0 ? $" ({_frameKey})" : "");

    public void SetState(bool canRecord, bool canPause, bool canStop, bool live, bool paused, string time)
    {
        RecordBtn.IsEnabled = canRecord;
        PauseBtn.IsEnabled = canPause;
        StopBtn.IsEnabled = canStop;
        LiveDot.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        LiveDot.Opacity = paused ? 0.35 : 1;
        TimeText.Text = time;
    }

    /// <summary>Shows or hides the "falling behind" warning.</summary>
    public void SetWarning(bool on) => WarnText.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void OnRecord(object sender, RoutedEventArgs e) => RecordRequested?.Invoke();
    private void OnPause(object sender, RoutedEventArgs e) => PauseRequested?.Invoke();
    private void OnStop(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
    private void OnToggleFrame(object sender, RoutedEventArgs e) => ToggleFrameRequested?.Invoke();
    private void OnShowApp(object sender, RoutedEventArgs e) => ShowAppRequested?.Invoke();
}
