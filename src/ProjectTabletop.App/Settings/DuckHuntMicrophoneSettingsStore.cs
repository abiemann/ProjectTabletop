using System.Text.Json;

namespace ProjectTabletop.App.Settings;

/// <summary>Duck Hunt's exact microphone endpoint. Null follows the Windows default.</summary>
public static class DuckHuntMicrophoneSettingsStore
{
    public const string FileName = "duck-hunt-microphone.json";

    public static string? Read(string path)
    {
        string json;
        try { json = File.ReadAllText(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("Version", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int format) || format != 1 ||
            !root.TryGetProperty("DeviceId", out var device) ||
            device.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) ||
            root.EnumerateObject().Count(property => property.NameEquals("DeviceId")) != 1)
            throw new InvalidDataException("The saved Duck Hunt microphone preference is invalid or unsupported.");
        string? deviceId = device.GetString();
        Validate(deviceId);
        return deviceId;
    }

    public static void Write(string path, string? deviceId)
    {
        Validate(deviceId);
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, DeviceId = deviceId },
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
            // Publish a complete preference, preserving the prior file on write failure.
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void Validate(string? deviceId)
    {
        if (deviceId is not null && string.IsNullOrWhiteSpace(deviceId))
            throw new InvalidDataException("A microphone preference must contain an exact device ID, or null for the Windows default.");
    }
}
