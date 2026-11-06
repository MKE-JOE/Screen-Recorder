using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScreenRecorder.Native;
using ScreenRecorder.Services;

namespace ScreenRecorder;

/// <summary>Lets the user pick the global hotkeys by pressing them.</summary>
public partial class HotkeyDialog : Window
{
    private readonly Dictionary<TextBox, Hotkey> _keys = new();

    public Hotkey RecordKey => _keys[RecordBox];
    public Hotkey PauseKey => _keys[PauseBox];
    public Hotkey StopKey => _keys[StopBox];
    public Hotkey ToggleFrameKey => _keys[FrameBox];

    public HotkeyDialog(AppSettings current)
    {
        InitializeComponent();
        Set(RecordBox, current.RecordKey);
        Set(PauseBox, current.PauseKey);
        Set(StopBox, current.StopKey);
        Set(FrameBox, current.ToggleFrameKey);
    }

    private void Set(TextBox box, Hotkey key)
    {
        _keys[box] = key;
        box.Text = key.ToString();
    }

    private void OnFocus(object sender, KeyboardFocusChangedEventArgs e) => ((TextBox)sender).Text = "Press keys…";
    private void OnBlur(object sender, KeyboardFocusChangedEventArgs e) => ((TextBox)sender).Text = _keys[(TextBox)sender].ToString();

    private void OnCapture(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;

        if (key is Key.Tab) { e.Handled = false; return; }   // keep Tab for moving between boxes
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.None)
            return;

        uint mods = 0;
        var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Control)) mods |= NativeMethods.MOD_CONTROL;
        if (m.HasFlag(ModifierKeys.Alt)) mods |= NativeMethods.MOD_ALT;
        if (m.HasFlag(ModifierKeys.Shift)) mods |= NativeMethods.MOD_SHIFT;
        if (m.HasFlag(ModifierKeys.Windows)) mods |= NativeMethods.MOD_WIN;

        bool isFunctionKey = key >= Key.F1 && key <= Key.F24;
        if (mods == 0 && !isFunctionKey)
        {
            ShowError("Add Ctrl, Alt, Shift or Win so the key still works normally in other apps.");
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;
        _keys[box] = new Hotkey(mods, (uint)KeyInterop.VirtualKeyFromKey(key));
        box.Text = _keys[box].ToString();
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        Set(RecordBox, AppSettings.DefaultRecord);
        Set(PauseBox, AppSettings.DefaultPause);
        Set(StopBox, AppSettings.DefaultStop);
        Set(FrameBox, AppSettings.DefaultToggleFrame);
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_keys.Values.Distinct().Count() < _keys.Count)
        {
            ShowError("Each action needs its own key combination.");
            return;
        }
        DialogResult = true;
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }
}
