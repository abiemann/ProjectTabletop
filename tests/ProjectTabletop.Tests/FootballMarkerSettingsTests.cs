using System.Text;
using System.Text.Json;
using ProjectTabletop.App;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public sealed class FootballMarkerSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tabletop-football-settings-" + Guid.NewGuid());
    private string SettingsPath => Path.Combine(_directory, "football-stick-tips.json");
    private const string CameraId = @"\\?\USB#VID_1234&PID_ABCD&MI_00#7&fixture&0&0000#{12345678-1234-1234-1234-123456789abc}\GLOBAL";
    private static BlackTipProfile Profile => new(1, .2, .6352941176470588,
        .00013274016203703703, 5.760196053720451, 2, .39755982246370536);

    [Fact]
    public void SavedDoubleBarRestoresForTheExactCameraWithoutChangingTheFile()
    {
        Write(new(2, new() { [CameraId] = new(Profile, null) }));
        byte[] original = File.ReadAllBytes(SettingsPath);

        var loaded = FootballMarkerSettingsStore.Read(SettingsPath);

        Assert.Equal("loaded", loaded.Status);
        var cameras = Assert.IsType<FootballMarkerSettings>(loaded.Settings).Cameras;
        Assert.Equal(CameraId, Assert.Single(cameras).Key);
        Assert.Equal(Profile, cameras[CameraId].ForPlayer(0));
        Assert.True(cameras[CameraId].Player1!.IsValid);
        Assert.Null(cameras[CameraId].ForPlayer(1));
        Assert.False(cameras.ContainsKey(CameraId.ToLowerInvariant()));
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public void AProfileThatAppearsAfterStartupCanBeReadOnTheNextAttempt()
    {
        Assert.Equal(new("not-found", null), FootballMarkerSettingsStore.Read(SettingsPath));
        Directory.CreateDirectory(_directory);
        Assert.Equal(new("not-found", null), FootballMarkerSettingsStore.Read(SettingsPath));
        Write(new(2, new() { [CameraId] = new(null, Profile) }));

        var loaded = FootballMarkerSettingsStore.Read(SettingsPath);

        Assert.Equal("loaded", loaded.Status);
        Assert.Null(loaded.Settings!.Cameras[CameraId].Player1);
        Assert.Equal(Profile, loaded.Settings.Cameras[CameraId].Player2);
    }

    [Fact]
    public void EmptyPlayerProfilesAreValidAndLegacyFilesRemainUntouched()
    {
        Write(new(2, new() { [CameraId] = new(null, null) }));
        Assert.Equal("loaded", FootballMarkerSettingsStore.Read(SettingsPath).Status);
        const string legacy = "{\"Version\":1,\"Cameras\":{\"old-camera\":{\"Hue\":10}}}";
        File.WriteAllText(SettingsPath, legacy);

        Assert.Equal(new("legacy-format", null), FootballMarkerSettingsStore.Read(SettingsPath));
        Assert.Equal(legacy, File.ReadAllText(SettingsPath));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Version\":\"2\"}")]
    [InlineData("{\"Version\":3,\"Cameras\":{}}")]
    [InlineData("{\"Version\":2,\"Cameras\":null}")]
    [InlineData("{\"Version\":2,\"Cameras\":{\" \" :{\"Player1\":null,\"Player2\":null}}}")]
    [InlineData("{\"Version\":2,\"Cameras\":{\"camera\":null}}")]
    public void InvalidSettingsAreReportedInsteadOfBecomingAnEmptyProfile(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, json);
        Assert.Throws<InvalidDataException>(() => FootballMarkerSettingsStore.Read(SettingsPath));
    }

    [Fact]
    public void InvalidProfilesOversizedFilesAndMalformedJsonAreRejected()
    {
        Write(new(2, new() { [CameraId] = new(Profile with { BarCount = 3 }, null) }));
        Assert.Throws<InvalidDataException>(() => FootballMarkerSettingsStore.Read(SettingsPath));
        Write(new(2, Enumerable.Range(0, 65).ToDictionary(i => "camera" + i, _ => new FootballCameraProfiles(Profile, null))));
        Assert.Throws<InvalidDataException>(() => FootballMarkerSettingsStore.Read(SettingsPath));
        File.WriteAllText(SettingsPath, new string(' ', 131073));
        Assert.Throws<InvalidDataException>(() => FootballMarkerSettingsStore.Read(SettingsPath));
        File.WriteAllText(SettingsPath, "{");
        Assert.ThrowsAny<JsonException>(() => FootballMarkerSettingsStore.Read(SettingsPath));
    }

    [Fact]
    public void FileAccessErrorsAreNotMisreportedAsMissingSettings()
    {
        Write(new(2, new() { [CameraId] = new(Profile, null) }));
        using var locked = new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => FootballMarkerSettingsStore.Read(SettingsPath));
    }

    [Fact]
    public void ExistingBomEncodedSettingsRemainReadable()
    {
        Directory.CreateDirectory(_directory);
        var settings = new FootballMarkerSettings(2, new() { [CameraId] = new(Profile, null) });
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings), Encoding.Unicode);
        Assert.Equal(Profile, FootballMarkerSettingsStore.Read(SettingsPath).Settings!.Cameras[CameraId].Player1);
    }

    private void Write(FootballMarkerSettings settings)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
