using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenRecorder.Native;

namespace ScreenRecorder;

/// <summary>
/// The movable, resizable frame that marks the capture area. The 10 px band and handles sit
/// outside the recorded rectangle, and the whole window is hidden from screen capture.
/// </summary>
public partial class RegionWindow : Window
{
    private const double BandDip = 10;
    private const double MinSizeDip = 80;
    private IntPtr _hwnd;

    public event EventHandler? CaptureRectChanged;
    public event Action? HideRequested;
    private RECT _lastRect;

    public RegionWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            WindowHelpers.SetExStyle(_hwnd, NativeMethods.WS_EX_TOOLWINDOW, true);
            WindowHelpers.ExcludeFromCapture(_hwnd, true);
        };
        WindowHelpers.KeepOnTop(this);
        SizeChanged += (_, _) => Changed();
        LocationChanged += (_, _) => Changed();
        DpiChanged += (_, _) => Changed();
    }

    private void Changed()
    {
        if (!IsLoaded) return;
        var r = GetCaptureRect();
        _lastRect = r;
        SizeText.Text = $"{r.Width} × {r.Height}";
        CaptureRectChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The recorded rectangle in physical screen pixels.</summary>
    public RECT GetCaptureRect()
    {
        if (!IsVisible && _lastRect.Width > 0) return _lastRect;   // hidden: keep recording the same area
        if (PresentationSource.FromVisual(CaptureArea) == null) return _lastRect;
        var tl = CaptureArea.PointToScreen(new Point(0, 0));
        var br = CaptureArea.PointToScreen(new Point(CaptureArea.ActualWidth, CaptureArea.ActualHeight));
        return new RECT((int)Math.Round(tl.X), (int)Math.Round(tl.Y), (int)Math.Round(br.X), (int)Math.Round(br.Y));
    }

    /// <summary>Resize so the capture area is exactly w × h physical pixels (keeps the centre).</summary>
    public void SetCaptureSize(int w, int h)
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double newW = w / scale + 2 * BandDip, newH = h / scale + 2 * BandDip;
        double cx = Left + Width / 2, cy = Top + Height / 2;
        Width = newW; Height = newH;
        Left = cx - newW / 2; Top = cy - newH / 2;
        var wa = SystemParameters.WorkArea;
        if (Left < wa.Left - BandDip) Left = wa.Left - BandDip;
        if (Top < wa.Top - BandDip) Top = wa.Top - BandDip;
    }

    public void SetHotkeyHints(string record, string pause, string stop, string frame) =>
        HotkeysText.Text = $"Record {record} · Pause {pause} · Stop {stop} · Hide/show {frame}";

    /// <summary>While recording the frame turns red and becomes click-through.</summary>
    public void SetRecording(bool recording)
    {
        Handles.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        ToolPill.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        InfoPill.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        var c = recording ? Color.FromRgb(0xE5, 0x48, 0x4D) : Color.FromRgb(0x25, 0x63, 0xEB);
        Outline.Stroke = new SolidColorBrush(c);
        Band.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, c.R, c.G, c.B));
        IsHitTestVisible = !recording;
        WindowHelpers.SetExStyle(_hwnd, NativeMethods.WS_EX_TRANSPARENT, recording);
    }

    private void OnHide(object sender, RoutedEventArgs e) => HideRequested?.Invoke();

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void OnResize(object sender, DragDeltaEventArgs e)
    {
        string tag = (string)((Thumb)sender).Tag;
        double min = MinSizeDip + 2 * BandDip;
        if (tag.Contains('L'))
        {
            double w = Math.Max(min, Width - e.HorizontalChange);
            Left += Width - w; Width = w;
        }
        if (tag.Contains('R')) Width = Math.Max(min, Width + e.HorizontalChange);
        if (tag.Contains('T'))
        {
            double h = Math.Max(min, Height - e.VerticalChange);
            Top += Height - h; Height = h;
        }
        if (tag.Contains('B')) Height = Math.Max(min, Height + e.VerticalChange);
    }
}
