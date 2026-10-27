using System.Text.Json;
using ProjectTabletop.App.Settings;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public sealed class ClickSoundProfileSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tabletop-click-profiles-" + Guid.NewGuid());
    private string SettingsPath => Path.Combine(_directory, ClickSoundProfileSettingsStore.FileName);
    private const string DeviceId = @"\\?\SWD#MMDEVAPI#{0.0.1.00000000}.{AaB12233-CCD4-EE55-FF66-001122334455}#{CAPTURE}";
    private static AudioClickProfile Profile => new()
    {
        Version = 1, TrainingNoiseRms = .001,
        MinimumPeak = .15, MaximumPeak = .9, MinimumRms = .02, MaximumRms = .4,
        MinimumDurationMs = 4, MaximumDurationMs = 80,
        MinimumHighFrequencyRatio = .1, MaximumHighFrequencyRatio = .9
    };

    [Fact]
    public void MissingProfilesAreEmptyWithoutCreatingFiles()
    {
        Assert.Empty(ClickSoundProfileSettingsStore.Read(SettingsPath));
        Assert.False(Directory.Exists(_directory));
        Directory.CreateDirectory(_directory);
        Assert.Empty(ClickSoundProfileSettingsStore.Read(SettingsPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    [Fact]
    public void RetrainingOneExactEndpointPreservesEveryOtherProfile()
    {
        string other = DeviceId.ToLowerInvariant();
        var second = Profile with { MaximumPeak = .8 };
        var retrained = Profile with { TrainingNoiseRms = .002, MinimumPeak = .2 };
        ClickSoundProfileSettingsStore.Save(SettingsPath, DeviceId, Profile);
        ClickSoundProfileSettingsStore.Save(SettingsPath, other, second);
        ClickSoundProfileSettingsStore.Save(SettingsPath, DeviceId, retrained);
        byte[] written = File.ReadAllBytes(SettingsPath);

        var loaded = ClickSoundProfileSettingsStore.Read(SettingsPath);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(retrained, loaded[DeviceId]);
        Assert.Equal(second, loaded[other]);
        Assert.Equal(written, File.ReadAllBytes(SettingsPath));
        Assert.Equal(SettingsPath, Assert.Single(Directory.EnumerateFiles(_directory)));
        using var document = JsonDocument.Parse(written);
        var features = document.RootElement.GetProperty("Profiles").GetProperty(DeviceId);
        Assert.Equal(10, features.EnumerateObject().Count());
        Assert.All(features.EnumerateObject(), property => Assert.Equal(JsonValueKind.Number, property.Value.ValueKind));
    }

    [Fact]
    public void NewLearningRoundTripsAlongsideUnchangedLegacyProfiles()
    {
        var current = Profile with { Version = 2, MaximumDurationMs = 125 };
        ClickSoundProfileSettingsStore.Save(SettingsPath, "legacy-mic", Profile);
        ClickSoundProfileSettingsStore.Save(SettingsPath, DeviceId, current);
        var loaded = ClickSoundProfileSettingsStore.Read(SettingsPath);
        Assert.Equal(Profile, loaded["legacy-mic"]);
        Assert.Equal(current, loaded[DeviceId]);
        Assert.Throws<InvalidDataException>(() => ClickSoundProfileSettingsStore.Save(SettingsPath,
            DeviceId, current with { MaximumDurationMs = 130 }));
        Assert.Equal(current, ClickSoundProfileSettingsStore.Read(SettingsPath)[DeviceId]);
    }

    [Fact]
    public void ConcurrentSavesDoNotLoseOtherMicrophones()
    {
        Parallel.For(0, 8, index => ClickSoundProfileSettingsStore.Save(SettingsPath, "endpoint-" + index, Profile));
        var loaded = ClickSoundProfileSettingsStore.Read(SettingsPath);
        Assert.Equal(8, loaded.Count);
        for (int index = 0; index < 8; index++) Assert.Equal(Profile, loaded["endpoint-" + index]);
        Assert.Equal(SettingsPath, Assert.Single(Directory.EnumerateFiles(_directory)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    public void DefaultPreferenceAndBlankIdsCannotReceiveTraining(string invalid)
    {
        ClickSoundProfileSettingsStore.Save(SettingsPath, DeviceId, Profile);
        byte[] original = File.ReadAllBytes(SettingsPath);
        Assert.Throws<InvalidDataException>(() => ClickSoundProfileSettingsStore.Save(SettingsPath, invalid, Profile));
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        WriteJson(Document($"{JsonSerializer.Serialize(invalid)}:{JsonSerializer.Serialize(Profile)}"));
        Assert.Throws<InvalidDataException>(() => ClickSoundProfileSettingsStore.Read(SettingsPath));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Version\":2,\"Profiles\":{}}")]
    [InlineData("{\"Version\":1,\"Version\":1,\"Profiles\":{}}")]
    [InlineData("{\"Version\":1,\"Profiles\":[],\"Unused\":true}")]
    [InlineData("{\"Version\":1,\"Profiles\":{},\"Profiles\":{}}")]
    [InlineData("{\"Version\":1,\"Profiles\":{\"endpoint\":null}}")]
    public void MalformedOrUnsupportedDocumentsAreRejectedWithoutChanges(string json)
    {
        WriteJson(json);
        Assert.Throws<InvalidDataException>(() => ClickSoundProfileSettingsStore.Read(SettingsPath));
        Assert.Throws<InvalidDataException>(() => ClickSoundProfileSettingsStore.Save(SettingsPath, DeviceId, Profile));
        Assert.Equal(json, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void DuplicateEndpointsAndMissingOrDuplicateFeaturesAreRejected()
    {
        string profile = JsonSerializer.Serialize(Profile);
        string[] malformed =
        [
            Document($"\"endpoint\":{profile},\"endpoint\":{profile}"),
            Document("\"endpoint\":" + ProfileJson("TrainingNoiseRms", null)),
            Document("\"endpoint\":" + profile.Insert(1, "\"MinimumPeak\":0.15,"))
        ];
        foreach (string json in malformed)
        {
            WriteJson(json);
            Assert.Throws<InvalidDataException>(() => ClickSoundProfileSettingsStore.Read(SettingsPath));
            Assert.Equal(json, File.ReadAllText(SettingsPath));
        }
    }

    [Theory]
    [InlineData("Version", "3")]
    [InlineData("TrainingNoiseRms", "-0.01")]
    [InlineData("TrainingNoiseRms", "0.2")]
    [InlineData("TrainingNoiseRms", "1e400")]
    [InlineData("MinimumPeak", "\"NaN\"")]
    [InlineData("MaximumPeak", "1.1")]
    [InlineData("MinimumRms", "0")]
    [InlineData("MaximumRms", "0.01")]
    [InlineData("MinimumDurationMs", "0")]
    [InlineData("MaximumDurationMs", "121")]
    [InlineData("MinimumHighFrequencyRatio", "-0.1")]
    [InlineData("MaximumHighFrequencyRatio", "1.1")]
    public void InvalidStoredFeaturesCannotLoad(string property, string value)
    {
        string json = Document("\"endpoint\":" + ProfileJson(property, value));
        WriteJson(json);
        var failure = Record.Exception(() => ClickSoundProfileSettingsStore.Read(SettingsPath));
        Assert.True(failure is InvalidDataException or JsonException);
        Assert.Equal(json, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void InvalidProfilesCannotReplaceExistingTraining()
    {
        ClickSoundProfileSettingsStore.Save(SettingsPath, DeviceId, Profile);
        byte[] original = File.ReadAllBytes(SettingsPath);
        AudioClickProfile[] invalid =
        [
            Profile with { TrainingNoiseRms = double.NaN },
            Profile with { MaximumPeak = double.PositiveInfinity },
            Profile with { MaximumDurationMs = 121 },
            Profile with { MinimumRms = .5, MaximumRms = .4 },
            Profile with { Version = 3 }
        ];
        foreach (var profile in invalid)
        {
            Assert.Throws<InvalidDataException>(() => ClickSoundProfileSettingsStore.Save(SettingsPath, DeviceId, profile));
            Assert.Equal(original, File.ReadAllBytes(SettingsPath));
            Assert.Equal(SettingsPath, Assert.Single(Directory.EnumerateFiles(_directory)));
        }
    }

    [Fact]
    public void CorruptJsonIsPreservedInsteadOfReplacingOtherTraining()
    {
        WriteJson("{");
        Assert.ThrowsAny<JsonException>(() => ClickSoundProfileSettingsStore.Read(SettingsPath));
        Assert.ThrowsAny<JsonException>(() => ClickSoundProfileSettingsStore.Save(SettingsPath, DeviceId, Profile));
        Assert.Equal("{", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void FailedReplacementPreservesPriorFileAndRemovesTemporaryFile()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows sharing blocks atomic replacement.
        ClickSoundProfileSettingsStore.Save(SettingsPath, DeviceId, Profile);
        byte[] original = File.ReadAllBytes(SettingsPath);
        using (var held = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = Record.Exception(() => ClickSoundProfileSettingsStore.Save(SettingsPath, "another-endpoint", Profile));
            Assert.True(failure is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        Assert.Equal(SettingsPath, Assert.Single(Directory.EnumerateFiles(_directory)));
    }

    private static string Document(string entries) => "{\"Version\":1,\"Profiles\":{" + entries + "}}";

    private static string ProfileJson(string field, string? replacement)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(Profile));
        return "{" + string.Join(',', document.RootElement.EnumerateObject()
            .Where(property => property.Name != field || replacement is not null)
            .Select(property => JsonSerializer.Serialize(property.Name) + ":" +
                (property.Name == field ? replacement : property.Value.GetRawText()))) + "}";
    }

    private void WriteJson(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, json);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
