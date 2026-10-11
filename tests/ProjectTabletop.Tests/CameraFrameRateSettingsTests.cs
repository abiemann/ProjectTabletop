using System.Text.Json;
using ProjectTabletop.App.Settings;

namespace ProjectTabletop.Tests;

public sealed class CameraFrameRateSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tabletop-camera-rates-" + Guid.NewGuid());
    private string SettingsPath => Path.Combine(_directory, "camera-frame-rates.json");
    private const string CameraId = @"\\?\USB#VID_1234&PID_ABCD&MI_00#7&fixture&0&0000\GLOBAL";

    [Fact]
    public void MissingSettingsReturnNoPreferencesWithoutCreatingFiles()
    {
        Assert.Empty(CameraFrameRateSettingsStore.Read(SettingsPath));
        Assert.False(Directory.Exists(_directory));
        Directory.CreateDirectory(_directory);
        Assert.Empty(CameraFrameRateSettingsStore.Read(SettingsPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    [Fact]
    public void EachCameraRetainsItsOwnRequestedRateAndReadingDoesNotChangeTheFile()
    {
        CameraFrameRateSettingsStore.Write(SettingsPath, new Dictionary<string, int>
        {
            [CameraId] = 100,
            ["android-camera"] = 30,
            ["other-usb-camera"] = 60
        });
        byte[] original = File.ReadAllBytes(SettingsPath);

        var rates = CameraFrameRateSettingsStore.Read(SettingsPath);

        Assert.Equal(3, rates.Count);
        Assert.Equal(100, rates[CameraId]);
        Assert.Equal(30, rates["android-camera"]);
        Assert.Equal(60, rates["other-usb-camera"]);
        Assert.False(rates.ContainsKey(CameraId.ToLowerInvariant()));
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public void UnknownRootPropertiesAreIgnored()
    {
        WriteJson("{\"Version\":1,\"Cameras\":{\"camera\":60},\"FutureSetting\":{\"value\":true}}");
        Assert.Equal(60, Assert.Single(CameraFrameRateSettingsStore.Read(SettingsPath)).Value);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Version\":0,\"Cameras\":{}}")]
    [InlineData("{\"Version\":2,\"Cameras\":{}}")]
    [InlineData("{\"Version\":\"1\",\"Cameras\":{}}")]
    [InlineData("{\"Version\":1,\"Cameras\":null}")]
    [InlineData("{\"Version\":1,\"Cameras\":[]}")]
    [InlineData("{\"Version\":1,\"Cameras\":{\" \":30}}")]
    [InlineData("{\"Version\":1,\"Cameras\":{\"camera\":0}}")]
    [InlineData("{\"Version\":1,\"Cameras\":{\"camera\":59}}")]
    [InlineData("{\"Version\":1,\"Cameras\":{\"camera\":120}}")]
    [InlineData("{\"Version\":1,\"Cameras\":{\"camera\":60.5}}")]
    [InlineData("{\"Version\":1,\"Cameras\":{\"camera\":\"60\"}}")]
    [InlineData("{\"Version\":1,\"Cameras\":{\"camera\":null}}")]
    [InlineData("{\"Version\":1,\"Cameras\":{\"camera\":30,\"camera\":60}}")]
    public void InvalidSavedSettingsAreRejectedAndRemainUntouched(string json)
    {
        WriteJson(json);
        Assert.Throws<InvalidDataException>(() => CameraFrameRateSettingsStore.Read(SettingsPath));
        Assert.Equal(json, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void MalformedJsonIsNotTreatedAsMissingSettings()
    {
        WriteJson("{");
        Assert.ThrowsAny<JsonException>(() => CameraFrameRateSettingsStore.Read(SettingsPath));
        Assert.Equal("{", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void AccessFailuresAreNotTreatedAsMissingSettings()
    {
        CameraFrameRateSettingsStore.Write(SettingsPath, new Dictionary<string, int> { [CameraId] = 30 });
        using var locked = new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => CameraFrameRateSettingsStore.Read(SettingsPath));
    }

    [Fact]
    public void WritingReplacesTheWholeExistingFileWithoutLeavingTemporaryFiles()
    {
        CameraFrameRateSettingsStore.Write(SettingsPath, new Dictionary<string, int> { [CameraId] = 30 });
        CameraFrameRateSettingsStore.Write(SettingsPath, new Dictionary<string, int> { ["replacement"] = 100 });

        var pair = Assert.Single(CameraFrameRateSettingsStore.Read(SettingsPath));
        Assert.Equal("replacement", pair.Key);
        Assert.Equal(100, pair.Value);
        Assert.Equal(SettingsPath, Assert.Single(Directory.EnumerateFiles(_directory)));
    }

    [Theory]
    [InlineData("camera", 59)]
    [InlineData(" ", 60)]
    public void InvalidWritesPreserveTheExistingFile(string camera, int rate)
    {
        CameraFrameRateSettingsStore.Write(SettingsPath, new Dictionary<string, int> { [CameraId] = 60 });
        byte[] original = File.ReadAllBytes(SettingsPath);

        Assert.Throws<InvalidDataException>(() => CameraFrameRateSettingsStore.Write(SettingsPath,
            new Dictionary<string, int> { [camera] = rate }));

        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        Assert.Equal(SettingsPath, Assert.Single(Directory.EnumerateFiles(_directory)));
    }

    [Fact]
    public void AFailedRenameRemovesTheTemporaryFile()
    {
        Directory.CreateDirectory(SettingsPath);

        var failure = Record.Exception(() => CameraFrameRateSettingsStore.Write(SettingsPath,
            new Dictionary<string, int> { [CameraId] = 100 }));
        // Windows reports a directory destination as access denied; other platforms
        // report an I/O error. Both must preserve the destination and clean up.
        Assert.True(failure is IOException or UnauthorizedAccessException,
            $"Expected a file-system rename failure, got {failure?.GetType().Name ?? "no exception"}.");

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
