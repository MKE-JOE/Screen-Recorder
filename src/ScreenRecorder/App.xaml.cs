using System.Windows;
using System.Windows.Threading;

namespace ScreenRecorder;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Screen Recorder", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
