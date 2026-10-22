using System.Collections.ObjectModel;
using System.Text.Json;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App.Settings;

/// <summary>Learned click features keyed by the exact capture endpoint, never the
/// changing Windows-default preference. No audio samples are persisted.</summary>
public static class ClickSoundProfileSettingsStore
{
    public const string FileName = "click-sound-profiles.json";
    private static readonly object Gate = new();
    private static readonly string[] ProfileFields =
    [
        nameof(AudioClickProfile.Version), nameof(AudioClickProfile.TrainingNoiseRms),
        nameof(AudioClickProfile.MinimumPeak), nameof(AudioClickProfile.MaximumPeak),
        nameof(AudioClickProfile.MinimumRms), nameof(AudioClickProfile.MaximumRms),
        nameof(AudioClickProfile.MinimumDurationMs), nameof(AudioClickProfile.MaximumDurationMs),
        nameof(AudioClickProfile.MinimumHighFrequencyRatio), nameof(AudioClickProfile.MaximumHighFrequencyRatio)
    ];

    public static IReadOnlyDictionary<string, AudioClickProfile> Read(string path)
    {
        lock (Gate) return new ReadOnlyDictionary<string, AudioClickProfile>(ReadProfiles(path));
    }

    public static void Save(string path, string actualDeviceId, AudioClickProfile profile)
    {
        ValidateDeviceId(actualDeviceId);
        ValidateProfile(profile);
        lock (Gate)
        {
            var profiles = ReadProfiles(path);
            profiles[actualDeviceId] = profile;
            byte[] content = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Profiles = profiles },
                new JsonSerializerOptions { WriteIndented = true });
            string targetPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(targetPath)!;
            Directory.CreateDirectory(directory);
            string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(content);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, targetPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }

    private static Dictionary<string, AudioClickProfile> ReadProfiles(string path)
    {
        var profiles = new Dictionary<string, AudioClickProfile>(StringComparer.Ordinal);
        string json;
        try { json = File.ReadAllText(path); }
        catch (FileNotFoundException) { return profiles; }
        catch (DirectoryNotFoundException) { return profiles; }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !SingleProperty(root, "Version", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int format) || format != 1 ||
            !SingleProperty(root, "Profiles", out var entries) || entries.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The saved click profiles are invalid or unsupported.");

        foreach (var entry in entries.EnumerateObject())
        {
            ValidateDeviceId(entry.Name);
            if (entry.Value.ValueKind != JsonValueKind.Object || ProfileFields.Any(field =>
                    !SingleProperty(entry.Value, field, out var value) || value.ValueKind != JsonValueKind.Number))
                throw new InvalidDataException("A saved click profile has missing, duplicate or invalid features.");
            var profile = entry.Value.Deserialize<AudioClickProfile>();
            ValidateProfile(profile);
            if (!profiles.TryAdd(entry.Name, profile!))
                throw new InvalidDataException("The saved click profiles contain a duplicate microphone endpoint.");
        }
        return profiles;
    }

    private static bool SingleProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        int count = 0;
        foreach (var property in element.EnumerateObject())
            if (property.NameEquals(name)) { value = property.Value; count++; }
        return count == 1;
    }

    private static void ValidateDeviceId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.Equals(deviceId, "default", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A click profile must identify the actual microphone endpoint.");
    }

    private static void ValidateProfile(AudioClickProfile? profile)
    {
        if (profile is null) throw new InvalidDataException("A saved click profile is missing.");
        try { profile.Validate(); }
        catch (ArgumentException error) { throw new InvalidDataException("The saved click profile features are invalid.", error); }
    }
}
