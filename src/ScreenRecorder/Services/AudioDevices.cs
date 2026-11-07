using NAudio.CoreAudioApi;

namespace ScreenRecorder.Services;

public sealed record AudioDeviceItem(string? Id, string Name, bool IsDefault)
{
    public override string ToString() => Name;
}

/// <summary>One choice in the "Audio source" dropdown: system sound, a microphone, both, or nothing.</summary>
public sealed record AudioSourceItem(string Name, string? SystemId, string? MicId)
{
    public override string ToString() => Name;
}

public static class AudioDevices
{
    /// <summary>
    /// Every audio choice the app can record: each playback device (system sound), each microphone, system sound
    /// plus each microphone, and no audio. The default playback device comes first and is the default.
    /// </summary>
    public static async Task<List<AudioSourceItem>> GetSourcesAsync()
    {
        var playback = (await GetPlaybackDevicesAsync()).Where(d => d.Id != null).ToList();
        var mics = (await GetMicrophonesAsync()).Where(d => d.Id != null).ToList();
        var items = new List<AudioSourceItem>();
        foreach (var p in playback) items.Add(new($"System sound: {p.Name}", p.Id, null));
        foreach (var m in mics) items.Add(new($"Microphone: {m.Name}", null, m.Id));
        if (playback.Count > 0)
            foreach (var m in mics) items.Add(new($"System sound + {m.Name}", playback[0].Id, m.Id));
        items.Add(new("No audio", null, null));
        return items;
    }

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
