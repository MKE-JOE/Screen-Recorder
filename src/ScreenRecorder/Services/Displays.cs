using ScreenRecorder.Native;

namespace ScreenRecorder.Services;

public sealed record DisplayInfo(int Number, IntPtr Handle, RECT Bounds, bool IsPrimary, string DeviceName)
{
    public string Label => $"Full screen: Display {Number} ({Bounds.Width}×{Bounds.Height}){(IsPrimary ? ", primary" : "")}";
    public override string ToString() => Label;
}

/// <summary>A capture rectangle in physical pixels, resolved to the method FFmpeg should use.</summary>
public sealed record CaptureTarget(RECT Rect, int? DdaOutputIndex, RECT DdaOutputDesktop)
{
    public int Width => Rect.Width;
    public int Height => Rect.Height;
    /// <summary>Offset inside the Desktop Duplication output (ddagrab uses output-relative coordinates).</summary>
    public int DdaOffsetX => Rect.Left - DdaOutputDesktop.Left;
    public int DdaOffsetY => Rect.Top - DdaOutputDesktop.Top;
}

public static class Displays
{
    public static List<DisplayInfo> GetAll()
    {
        var result = new List<DisplayInfo>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT r, IntPtr _) =>
        {
            var mi = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (NativeMethods.GetMonitorInfo(h, ref mi))
                result.Add(new DisplayInfo(0, h, mi.rcMonitor, (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0, mi.szDevice));
            return true;
        }, IntPtr.Zero);

        // Primary first, then left-to-right.
        return result
            .OrderByDescending(d => d.IsPrimary).ThenBy(d => d.Bounds.Left).ThenBy(d => d.Bounds.Top)
            .Select((d, i) => d with { Number = i + 1 })
            .ToList();
    }

    /// <summary>
    /// Clips a rectangle to the single monitor it mostly covers, makes the size even
    /// (required for H.264 4:2:0), and works out the Desktop Duplication output.
    /// </summary>
    public static CaptureTarget Resolve(RECT requested)
    {
        var r = requested;
        IntPtr mon = NativeMethods.MonitorFromRect(ref r, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        RECT bounds = NativeMethods.GetMonitorInfo(mon, ref mi) ? mi.rcMonitor : requested;

        int left = Math.Max(requested.Left, bounds.Left);
        int top = Math.Max(requested.Top, bounds.Top);
        int right = Math.Min(requested.Right, bounds.Right);
        int bottom = Math.Min(requested.Bottom, bounds.Bottom);
        int w = Math.Max(16, (right - left) & ~1);
        int h = Math.Max(16, (bottom - top) & ~1);
        if (left + w > bounds.Right) left = bounds.Right - w;
        if (top + h > bounds.Bottom) top = bounds.Bottom - h;
        var clipped = new RECT(left, top, left + w, top + h);

        foreach (var o in Dxgi.GetDefaultAdapterOutputs())
        {
            if (o.Monitor == mon)
                return new CaptureTarget(clipped, o.Index, o.Desktop);
        }
        return new CaptureTarget(clipped, null, default);
    }
}
