using System.Windows;
using System.Windows.Controls;

namespace ScreenRecorder.Controls;

/// <summary>
/// Jump to start, rewind, play, pause, stop, fast-forward and jump to end. Each window decides what the
/// buttons do (seconds in the video windows, frames in the GIF editor) by handling the events.
/// The buttons are reachable by name (e.g. <c>Transport.PlayButton</c>) to enable them or change tooltips.
/// </summary>
public partial class TransportControls : UserControl
{
    public event RoutedEventHandler? JumpStart, Rewind, Play, Pause, Stop, Forward, JumpEnd;

    public TransportControls() => InitializeComponent();

    private void OnStart(object sender, RoutedEventArgs e) => JumpStart?.Invoke(this, e);
    private void OnRewind(object sender, RoutedEventArgs e) => Rewind?.Invoke(this, e);
    private void OnPlay(object sender, RoutedEventArgs e) => Play?.Invoke(this, e);
    private void OnPause(object sender, RoutedEventArgs e) => Pause?.Invoke(this, e);
    private void OnStop(object sender, RoutedEventArgs e) => Stop?.Invoke(this, e);
    private void OnForward(object sender, RoutedEventArgs e) => Forward?.Invoke(this, e);
    private void OnEnd(object sender, RoutedEventArgs e) => JumpEnd?.Invoke(this, e);
}
