using NAudio.CoreAudioApi;

namespace ScreenRecorder.Services;

public sealed record AudioDeviceItem(string? Id, string Name, bool IsDefault)
{
    public override string ToString() => Name;
}

public static class AudioDevices
{
    /// <summary>Playback devices (recorded via WASAPI loopback). The system default is listed first.</summary>
    public static Task<List<AudioDeviceItem>> GetPlaybackDevicesAsync() => Task.Run(() => List(DataFlow.Render, "No system audio"));

    /// <summary>Microphones. "No microphone" is listed first and is the default choice.</summary>
    public static Task<List<AudioDeviceItem>> GetMicrophonesAsync() => Task.Run(() => List(DataFlow.Capture, "No microphone"));

    private static List<AudioDeviceItem> List(DataFlow flow, string noneLabel)
    {
        var items = new List<AudioDeviceItem>();
        try
        {
            using var en = new MMDeviceEnumerator();
            string? defaultId = null;
            try { defaultId = en.GetDefaultAudioEndpoint(flow, Role.Multimedia).ID; } catch { }

            foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                bool isDefault = d.ID == defaultId;
                items.Add(new AudioDeviceItem(d.ID, isDefault ? $"{d.FriendlyName} (default)" : d.FriendlyName, isDefault));
                d.Dispose();
            }
        }
        catch { /* no audio stack */ }

        items = items.OrderByDescending(i => i.IsDefault).ThenBy(i => i.Name).ToList();
        var none = new AudioDeviceItem(null, noneLabel, false);
        if (flow == DataFlow.Capture) items.Insert(0, none); else items.Add(none);
        return items;
    }
}
