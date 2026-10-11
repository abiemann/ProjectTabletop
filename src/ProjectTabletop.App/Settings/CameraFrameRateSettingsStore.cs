using System.Text.Json;

namespace ProjectTabletop.App.Settings;

/// <summary>Stores the requested capture rate for each exact camera device ID.</summary>
public static class CameraFrameRateSettingsStore
{
    public static Dictionary<string, int> Read(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (FileNotFoundException) { return new(StringComparer.Ordinal); }
        catch (DirectoryNotFoundException) { return new(StringComparer.Ordinal); }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("Version", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int format) || format != 1 ||
            !root.TryGetProperty("Cameras", out var cameras) || cameras.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The saved camera frame-rate settings are invalid or unsupported.");

        var rates = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var camera in cameras.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(camera.Name) || camera.Value.ValueKind != JsonValueKind.Number ||
                !camera.Value.TryGetInt32(out int rate) || !IsSupported(rate) || !rates.TryAdd(camera.Name, rate))
                throw new InvalidDataException("The saved camera frame-rate settings contain an invalid camera or rate.");
        }
        return rates;
    }

    public static void Write(string path, IReadOnlyDictionary<string, int> rates)
    {
        ArgumentNullException.ThrowIfNull(rates);
        var snapshot = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in rates)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || !IsSupported(pair.Value) || !snapshot.TryAdd(pair.Key, pair.Value))
                throw new InvalidDataException("Camera frame rates must be 30, 60 or 100 fps and have a camera device ID.");
        }
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Cameras = snapshot },
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
            // The same-directory rename publishes the complete file in one operation.
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static bool IsSupported(int rate) => rate is 30 or 60 or 100;
}
