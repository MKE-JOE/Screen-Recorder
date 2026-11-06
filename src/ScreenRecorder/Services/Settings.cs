using System.Text.Json;
using System.Windows.Input;
using ScreenRecorder.Native;

namespace ScreenRecorder.Services;

/// <summary>A global hotkey: Win32 modifier flags plus a virtual-key code.</summary>
public sealed record Hotkey(uint Modifiers, uint VirtualKey)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((Modifiers & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((Modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((Modifiers & NativeMethods.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(KeyName(KeyInterop.KeyFromVirtualKey((int)VirtualKey)));
        return string.Join("+", parts);
    }

    private static string KeyName(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (k - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (k - Key.NumPad0),
        Key.OemPlus => "=", Key.OemMinus => "-", Key.OemComma => ",", Key.OemPeriod => ".",
        Key.Oem1 => ";", Key.Oem2 => "/", Key.Oem3 => "`", Key.Oem4 => "[", Key.Oem5 => "\\", Key.Oem6 => "]", Key.Oem7 => "'",
        Key.Prior => "PageUp", Key.Next => "PageDown", Key.Snapshot => "PrintScreen",
        _ => k.ToString(),
    };
}

/// <summary>User settings, saved as JSON in %LOCALAPPDATA%\PortableScreenRecorder\settings.json.</summary>
public sealed class AppSettings
{
    private const uint CtrlAlt = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;
    public static readonly Hotkey DefaultRecord = new(CtrlAlt, 0x52); // R
    public static readonly Hotkey DefaultPause = new(CtrlAlt, 0x50);  // P
    public static readonly Hotkey DefaultStop = new(CtrlAlt, 0x51);   // Q
    public static readonly Hotkey DefaultToggleFrame = new(CtrlAlt, 0x48); // H

    public Hotkey RecordKey { get; set; } = DefaultRecord;
    public Hotkey PauseKey { get; set; } = DefaultPause;
    public Hotkey StopKey { get; set; } = DefaultStop;
    public Hotkey ToggleFrameKey { get; set; } = DefaultToggleFrame;

    private static string FilePath => Path.Combine(Ffmpeg.AppDataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* corrupt or unreadable: fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Ffmpeg.AppDataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
