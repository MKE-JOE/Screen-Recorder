using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ScreenRecorder.Native;

namespace ScreenRecorder;

/// <summary>3-2-1 countdown shown over the centre of the capture area. Never appears in the recording.</summary>
public partial class CountdownWindow : Window
{
    private readonly RECT _target;

    private CountdownWindow(RECT target)
    {
        InitializeComponent();
        _target = target;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            WindowHelpers.SetExStyle(hwnd, NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE, true);
            WindowHelpers.ExcludeFromCapture(hwnd, true);
            CenterOnTarget(hwnd);
        };
    }

    private void CenterOnTarget(IntPtr hwnd)
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = (int)(Width * scale), h = (int)(Height * scale);
        int x = _target.Left + (_target.Width - w) / 2;
        int y = _target.Top + (_target.Height - h) / 2;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 /*NOSIZE*/ | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>Returns false if cancelled.</summary>
    public static async Task<bool> RunAsync(RECT target, CancellationToken ct)
    {
        var w = new CountdownWindow(target);
        w.Show();
        try
        {
            for (int i = 3; i >= 1; i--)
            {
                w.Number.Text = i.ToString();
                var pulse = new DoubleAnimation(1.0, 0.25, TimeSpan.FromMilliseconds(950));
                w.Number.BeginAnimation(OpacityProperty, pulse);
                await Task.Delay(1000, ct);
            }
            return true;
        }
        catch (TaskCanceledException) { return false; }
        finally { w.Close(); }
    }
}
