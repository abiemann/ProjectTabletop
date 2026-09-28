using System.Text.RegularExpressions;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace ProjectTabletop.App.Media;

internal static class DisplayAudioDevices
{
    internal static async Task<DeviceInformation?> FindAsync(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
        DeviceInformation? match = null;
        for (int index = 0; index < devices.Count; index++)
        {
            var device = devices[index];
            if (!device.IsEnabled || !MatchesProjector(device.Name, displayName)) continue;
            // Equal names do not identify which physical display owns the speakers.
            if (match is not null) return null;
            match = device;
        }
        return match;
    }

    internal static bool MatchesProjector(string audioName, string? projectorName)
    {
        if (string.IsNullOrWhiteSpace(projectorName)) return false;
        string name = Regex.Replace(audioName.Trim(), @"^\d+\s*-\s*", "");
        string projector = projectorName.Trim();
        return name.Equals(projector, StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(projector + " (", StringComparison.OrdinalIgnoreCase);
    }
}
