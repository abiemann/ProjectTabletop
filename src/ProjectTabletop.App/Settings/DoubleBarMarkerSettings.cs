using System.Text.Json;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

// Keep the existing version and camera profiles; unrelated properties in older
// settings files are ignored and are not written back.
internal sealed record DoubleBarMarkerSettings(int Version, Dictionary<string, BlackTipProfile> Cameras)
{
    public static DoubleBarMarkerSettings Parse(string json)
    {
        var saved = JsonSerializer.Deserialize<DoubleBarMarkerSettings>(json);
        if (saved is not { Version: 1, Cameras: not null } || saved.Cameras.Count > 64 ||
            saved.Cameras.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 4096 ||
                DoubleBarProfileSelection.Usable(pair.Value) is null))
            throw new InvalidDataException("The saved double-bar marker settings are invalid.");
        return saved;
    }
}
