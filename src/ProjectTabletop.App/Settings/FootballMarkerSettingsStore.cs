using System.Text;
using System.Text.Json;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

internal sealed record FootballCameraProfiles(BlackTipProfile? Player1, BlackTipProfile? Player2)
{
    public BlackTipProfile? ForPlayer(int player) => player == 0 ? Player1 : Player2;
}

internal sealed record FootballMarkerSettings(int Version, Dictionary<string, FootballCameraProfiles> Cameras);
internal sealed record FootballMarkerSettingsLoad(string Status, FootballMarkerSettings? Settings);

/// <summary>Reads saved marker profiles without treating access failures as an absent file.</summary>
internal static class FootballMarkerSettingsStore
{
    private const int MaximumBytes = 131072;

    public static FootballMarkerSettingsLoad Read(string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (FileNotFoundException) { return new("not-found", null); }
        catch (DirectoryNotFoundException) { return new("not-found", null); }

        using (stream)
        {
            if (stream.Length > MaximumBytes)
                throw new InvalidDataException("The saved football marker settings are too large.");

            // Bound the read itself as well, in case another process grows the file.
            byte[] bytes = new byte[MaximumBytes + 1];
            int count = 0, read;
            while (count < bytes.Length && (read = stream.Read(bytes, count, bytes.Length - count)) != 0)
                count += read;
            if (count > MaximumBytes)
                throw new InvalidDataException("The saved football marker settings are too large.");

            using var text = new StreamReader(new MemoryStream(bytes, 0, count), Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            using var document = JsonDocument.Parse(text.ReadToEnd());
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("Version", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int format))
                throw new InvalidDataException("The saved football marker settings are invalid.");

            // Version 1 described the earlier colour-marker experiment, not a black bar.
            if (format == 1) return new("legacy-format", null);

            var saved = document.Deserialize<FootballMarkerSettings>();
            if (saved is not { Version: 2, Cameras: not null } || saved.Cameras.Count > 64 ||
                saved.Cameras.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 4096 ||
                    pair.Value is null || pair.Value.Player1 is { IsValid: false } ||
                    pair.Value.Player2 is { IsValid: false }))
                throw new InvalidDataException("The saved football marker settings are invalid.");

            return new("loaded", saved);
        }
    }
}
