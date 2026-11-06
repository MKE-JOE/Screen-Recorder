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

    protected override void OnStartup(StartupEventArgs e)
    {
        SelfTest = e.Args.Contains("--selftest");
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
