using System.Windows;
using System.Windows.Threading;

namespace ScreenRecorder;

public partial class App : Application
{
    /// <summary>
    /// "--selftest": start the UI, prepare FFmpeg, enumerate displays and audio devices, then exit.
    /// Exit code 0 means the app starts cleanly; CI runs this on every build.
    /// </summary>
    public static bool SelfTest { get; private set; }
    /// <summary>"--selftest-gif &lt;video&gt; &lt;out.gif&gt;": open the GIF editor on a video, edit, export, exit.</summary>
    public static (string Input, string Output)? SelfTestGif { get; private set; }
    /// <summary>"--selftest-video &lt;video&gt; &lt;out.mp4&gt;": open the video editor, add text, crop, resize, export, exit.</summary>
    public static (string Input, string Output)? SelfTestVideo { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        SelfTest = e.Args.Contains("--selftest") || e.Args.Contains("--selftest-gif") || e.Args.Contains("--selftest-video");
        int vi = Array.IndexOf(e.Args, "--selftest-video");
        if (vi >= 0 && vi + 2 < e.Args.Length) SelfTestVideo = (Path.GetFullPath(e.Args[vi + 1]), Path.GetFullPath(e.Args[vi + 2]));
        int gi = Array.IndexOf(e.Args, "--selftest-gif");
        if (gi >= 0 && gi + 2 < e.Args.Length) SelfTestGif = (Path.GetFullPath(e.Args[gi + 1]), Path.GetFullPath(e.Args[gi + 2]));
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => { if (SelfTest) Fail(a.ExceptionObject?.ToString()); };
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (SelfTest) Fail(e.Exception.ToString());
        MessageBox.Show(e.Exception.Message, "Screen Recorder", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    public static void Fail(string? message)
    {
        try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest.log"), "FAIL\n" + message); } catch { }
        Environment.Exit(1);
    }

    public static void Pass(string message)
    {
        try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest.log"), "PASS\n" + message); } catch { }
        Environment.Exit(0);
    }
}
