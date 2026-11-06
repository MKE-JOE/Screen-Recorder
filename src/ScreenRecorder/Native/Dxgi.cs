using System.Runtime.InteropServices;

namespace ScreenRecorder.Native;

/// <summary>
/// Minimal DXGI enumeration (raw vtable calls) used to map a monitor to the
/// output index that FFmpeg's ddagrab (Desktop Duplication) expects.
/// ddagrab uses the default D3D11 adapter, which is adapter 0 of IDXGIFactory1.
/// </summary>
internal static unsafe class Dxgi
{
    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_OUTPUT_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public RECT DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    public readonly record struct OutputInfo(int Index, IntPtr Monitor, RECT Desktop);

    /// <summary>Outputs attached to the default adapter, in ddagrab's output_idx order.</summary>
    public static List<OutputInfo> GetDefaultAdapterOutputs()
    {
        var list = new List<OutputInfo>();
        IntPtr factory = IntPtr.Zero, adapter = IntPtr.Zero;
        try
        {
            var iid = IID_IDXGIFactory1;
            if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == IntPtr.Zero) return list;

            // IDXGIFactory::EnumAdapters is vtable slot 7.
            var enumAdapters = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(IntPtr**)factory)[7];
            IntPtr a;
            if (enumAdapters(factory, 0, &a) < 0 || a == IntPtr.Zero) return list;
            adapter = a;

            // IDXGIAdapter::EnumOutputs is vtable slot 7.
            var enumOutputs = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(IntPtr**)adapter)[7];
            for (uint i = 0; i < 16; i++)
            {
                IntPtr output;
                if (enumOutputs(adapter, i, &output) < 0 || output == IntPtr.Zero) break;
                try
                {
                    // IDXGIOutput::GetDesc is vtable slot 7.
                    var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)(*(IntPtr**)output)[7];
                    IntPtr buf = Marshal.AllocHGlobal(Marshal.SizeOf<DXGI_OUTPUT_DESC>());
                    try
                    {
                        if (getDesc(output, buf) >= 0)
                        {
                            var desc = Marshal.PtrToStructure<DXGI_OUTPUT_DESC>(buf);
                            list.Add(new OutputInfo((int)i, desc.Monitor, desc.DesktopCoordinates));
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                finally { Marshal.Release(output); }
            }
        }
        catch
        {
            // DXGI unavailable: caller falls back to GDI capture.
        }
        finally
        {
            if (adapter != IntPtr.Zero) Marshal.Release(adapter);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
        }
        return list;
    }
}
