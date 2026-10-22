using System.Text.Json;
using ProjectTabletop.App.Settings;

namespace ProjectTabletop.Tests;

public sealed class DuckHuntMicrophoneSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tabletop-duck-hunt-mic-" + Guid.NewGuid());
    private string SettingsPath => Path.Combine(_directory, DuckHuntMicrophoneSettingsStore.FileName);
    private const string DeviceId = @"\\?\SWD#MMDEVAPI#{0.0.1.00000000}.{AaB12233-CCD4-EE55-FF66-001122334455}#{CAPTURE}";

    [Fact]
    public void MissingPreferenceUsesSystemDefaultWithoutCreatingFiles()
    {
        Assert.Null(DuckHuntMicrophoneSettingsStore.Read(SettingsPath));
        Assert.False(Directory.Exists(_directory));
        Directory.CreateDirectory(_directory);
        Assert.Null(DuckHuntMicrophoneSettingsStore.Read(SettingsPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    [Fact]
    public void ExactEndpointIdSurvivesRoundTripWithoutChangingTheFile()
    {
        DuckHuntMicrophoneSettingsStore.Write(SettingsPath, DeviceId);
        byte[] original = File.ReadAllBytes(SettingsPath);
        Assert.Equal(DeviceId, DuckHuntMicrophoneSettingsStore.Read(SettingsPath));
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        Assert.Equal(SettingsPath, Assert.Single(Directory.EnumerateFiles(_directory)));
    }

    [Fact]
    public void ChoosingDefaultReplacesTheExplicitPreference()
    {
        DuckHuntMicrophoneSettingsStore.Write(SettingsPath, DeviceId);
        DuckHuntMicrophoneSettingsStore.Write(SettingsPath, null);
        Assert.Null(DuckHuntMicrophoneSettingsStore.Read(SettingsPath));
        Assert.Contains("\"DeviceId\": null", File.ReadAllText(SettingsPath));
        Assert.Equal(SettingsPath, Assert.Single(Directory.EnumerateFiles(_directory)));
    }

    [Fact]
    public void UnknownPropertiesDoNotChangeThePreference()
    {
        WriteJson("{\"Version\":1,\"DeviceId\":\"USB microphone\",\"FutureSetting\":true}");
        Assert.Equal("USB microphone", DuckHuntMicrophoneSettingsStore.Read(SettingsPath));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Version\":2,\"DeviceId\":null}")]
    [InlineData("{\"Version\":\"1\",\"DeviceId\":null}")]
    [InlineData("{\"Version\":1}")]
    [InlineData("{\"Version\":1,\"DeviceId\":42}")]
    [InlineData("{\"Version\":1,\"DeviceId\":{}}")]
    [InlineData("{\"Version\":1,\"DeviceId\":\"\"}")]
    [InlineData("{\"Version\":1,\"DeviceId\":\"  \"}")]
    [InlineData("{\"Version\":1,\"DeviceId\":null,\"DeviceId\":\"another\"}")]
    public void InvalidSettingsAreRejectedAndPreserved(string json)
    {
        WriteJson(json);
        Assert.Throws<InvalidDataException>(() => DuckHuntMicrophoneSettingsStore.Read(SettingsPath));
        Assert.Equal(json, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void CorruptJsonIsNotTreatedAsSystemDefault()
    {
        WriteJson("{");
        Assert.ThrowsAny<JsonException>(() => DuckHuntMicrophoneSettingsStore.Read(SettingsPath));
        Assert.Equal("{", File.ReadAllText(SettingsPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void InvalidWriteKeepsExistingPreference(string invalid)
    {
        DuckHuntMicrophoneSettingsStore.Write(SettingsPath, DeviceId);
        byte[] original = File.ReadAllBytes(SettingsPath);
        Assert.Throws<InvalidDataException>(() => DuckHuntMicrophoneSettingsStore.Write(SettingsPath, invalid));
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        Assert.Equal(SettingsPath, Assert.Single(Directory.EnumerateFiles(_directory)));
    }

    [Fact]
    public void FailedReplacementRemovesTheTemporaryFile()
    {
        Directory.CreateDirectory(SettingsPath);
        var failure = Record.Exception(() => DuckHuntMicrophoneSettingsStore.Write(SettingsPath, DeviceId));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.True(Directory.Exists(SettingsPath));
        Assert.Empty(Directory.EnumerateFiles(_directory));
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
