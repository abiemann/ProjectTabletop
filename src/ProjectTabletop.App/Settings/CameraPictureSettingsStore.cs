using System.Text.Json;

namespace ProjectTabletop.App.Settings;

public sealed record CameraPictureSettings(bool Enabled, Dictionary<string, CameraPictureDefaults> Cameras);

/// <summary>
/// Keeps original per-camera defaults across crashes and restarts. Save a newly captured Default
/// successfully before applying Dark. Recapture only after a verified restoration or explicit user reset.
/// </summary>
public static class CameraPictureSettingsStore
{
    public static CameraPictureSettings Read(string path)
    {
        string json;
        try { json = File.ReadAllText(path); }
        catch (FileNotFoundException) { return Empty(); }
        catch (DirectoryNotFoundException) { return Empty(); }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out int format) || format != 1 ||
            !root.TryGetProperty("Enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !root.TryGetProperty("Cameras", out var cameras) || cameras.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The saved camera picture profiles are invalid or unsupported.");

        var profiles = new Dictionary<string, CameraPictureDefaults>(StringComparer.Ordinal);
        foreach (var camera in cameras.EnumerateObject())
        {
            CameraPictureDefaults? profile;
            try { profile = camera.Value.Deserialize<CameraPictureDefaults>(); }
            catch (JsonException ex) { throw new InvalidDataException("A saved camera picture profile is invalid.", ex); }
            if (!ValidCamera(camera.Name) || profile is not { IsValid: true } ||
                !profiles.TryAdd(camera.Name, profile) || profiles.Count > 64)
                throw new InvalidDataException("The saved camera picture profiles contain an invalid camera or Default.");
        }
        return new(enabled.GetBoolean(), profiles);
    }

    public static void Write(string path, CameraPictureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Cameras is null || settings.Cameras.Count > 64)
            throw new InvalidDataException("Camera picture profiles must contain at most 64 cameras.");
        var snapshot = new Dictionary<string, CameraPictureDefaults>(StringComparer.Ordinal);
        foreach (var camera in settings.Cameras)
        {
            if (!ValidCamera(camera.Key) || camera.Value is not { IsValid: true } ||
                !snapshot.TryAdd(camera.Key, camera.Value))
                throw new InvalidDataException("Camera picture profiles require a device ID and valid Default controls.");
        }
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, settings.Enabled, Cameras = snapshot },
            new JsonSerializerOptions { WriteIndented = true });
        string target = Path.GetFullPath(path), directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool ValidCamera(string id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 4096;
    private static CameraPictureSettings Empty() => new(true, new(StringComparer.Ordinal));
}
