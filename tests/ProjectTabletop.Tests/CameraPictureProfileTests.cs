using System.Text.Json;
using ProjectTabletop.App.Settings;

namespace ProjectTabletop.Tests;

public sealed class CameraPictureProfileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tabletop-camera-picture-" + Guid.NewGuid());
    private string SettingsPath => Path.Combine(_directory, "camera-picture.json");
    private static readonly CameraPictureControl Brightness = new(50, 0, 100, 1);
    private static readonly CameraPictureControl Contrast = new(116, 0, 200, 1);
    private static readonly CameraPictureDefaults Defaults = new(Brightness, Contrast);

    [Fact]
    public void DarkSceneUsesOriginalContrastAndLeavesBrightnessAlone()
    {
        var dark = CameraPictureProfilePolicy.CreateTarget(Defaults, Brightness, Contrast, darkScene: true);
        Assert.Equal(50, dark.Brightness);
        Assert.Equal(100, dark.Contrast);

        var currentDark = Contrast with { Value = 100 };
        Assert.Equal(dark, CameraPictureProfilePolicy.CreateTarget(Defaults, Brightness, currentDark, darkScene: true));
        var restored = CameraPictureProfilePolicy.CreateTarget(Defaults, Brightness, currentDark, darkScene: false);
        Assert.Equal(50, restored.Brightness);
        Assert.Equal(116, restored.Contrast);
    }

    [Theory]
    [InlineData(0, 0, 200, 1, 0)]
    [InlineData(4, 0, 200, 1, 0)]
    [InlineData(22, 20, 120, 2, 20)]
    [InlineData(0, -64, 64, 2, -10)]
    [InlineData(126, 10, 210, 4, 110)]
    [InlineData(117, 5, 205, 8, 101)]
    [InlineData(117, 0, 200, 3, 102)]
    [InlineData(20, 0, 200, 20, 0)]
    [InlineData(30, 30, 30, 1, 30)]
    [InlineData(.56, .1, 1.1, .02, .48)]
    public void DarkTargetIsQuantizedClampedAndNeverRaisesDefault(double value, double minimum,
        double maximum, double step, double expected)
    {
        var control = new CameraPictureControl(value, minimum, maximum, step);
        Assert.True(control.IsValid);
        var target = CameraPictureProfilePolicy.CreateTarget(new(null, control), null, control, darkScene: true);
        Assert.Null(target.Brightness);
        Assert.Equal(expected, target.Contrast!.Value, precision: 8);
        Assert.InRange(target.Contrast.Value, minimum, value);
        Assert.True((control with { Value = target.Contrast.Value }).IsValid);
    }

    [Fact]
    public void UnsupportedAndInvalidControlsReceiveNoOverride()
    {
        Assert.Null(CameraPictureProfilePolicy.CaptureDefaults(null, null));
        Assert.Null(CameraPictureProfilePolicy.CaptureDefaults(Brightness with { Step = 0 }, null));
        Assert.Equal(new CameraPictureDefaults(null, Contrast),
            CameraPictureProfilePolicy.CaptureDefaults(Brightness with { Value = double.NaN }, Contrast));
        var missingContrast = CameraPictureProfilePolicy.CreateTarget(Defaults, Brightness, null, darkScene: true);
        Assert.Equal(50, missingContrast.Brightness);
        Assert.Null(missingContrast.Contrast);
        Assert.Equal(new CameraPictureTarget(null, null),
            CameraPictureProfilePolicy.CreateTarget(Defaults, null, Contrast with { Step = 0 }, darkScene: true));
    }

    [Theory]
    [InlineData(0, 201, 1)]
    [InlineData(-1, 200, 1)]
    [InlineData(0, 200, 2)]
    public void AChangedSessionRangeRequiresAnExplicitNewDefault(double minimum, double maximum, double step)
    {
        var changed = new CameraPictureControl(100, minimum, maximum, step);
        Assert.True(changed.IsValid);
        var target = CameraPictureProfilePolicy.CreateTarget(Defaults, Brightness, changed, darkScene: true);
        Assert.Equal(50, target.Brightness);
        Assert.Null(target.Contrast);
    }

    [Theory]
    [InlineData(double.NaN, 0, 200, 1)]
    [InlineData(100, double.NegativeInfinity, 200, 1)]
    [InlineData(100, 0, double.PositiveInfinity, 1)]
    [InlineData(100, 0, 200, 0)]
    [InlineData(100, 0, 200, -1)]
    [InlineData(100, 201, 200, 1)]
    [InlineData(-1, 0, 200, 1)]
    [InlineData(201, 0, 200, 1)]
    [InlineData(99, 0, 200, 2)]
    [InlineData(0, -1e308, 1e308, 1)]
    [InlineData(100, 0, 200, double.Epsilon)]
    public void MalformedDriverValuesCannotBecomeDefaults(double value, double minimum, double maximum, double step)
    {
        var control = new CameraPictureControl(value, minimum, maximum, step);
        Assert.False(control.IsValid);
        Assert.Null(CameraPictureProfilePolicy.CaptureDefaults(null, control));
    }

    [Fact]
    public void MissingFileEnablesAutomaticProfilesWithoutInventingDefaultsOrWritingFiles()
    {
        var settings = CameraPictureSettingsStore.Read(SettingsPath);
        Assert.True(settings.Enabled);
        Assert.Empty(settings.Cameras);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void DefaultsSurviveRestartWhileTheCameraStillReportsDarkValues()
    {
        var settings = new CameraPictureSettings(true, new(StringComparer.Ordinal) { ["Camera-A"] = Defaults });
        CameraPictureSettingsStore.Write(SettingsPath, settings);
        byte[] before = File.ReadAllBytes(SettingsPath);

        var restarted = CameraPictureSettingsStore.Read(SettingsPath);
        var current = Contrast with { Value = 100 };
        var dark = CameraPictureProfilePolicy.CreateTarget(restarted.Cameras["Camera-A"], Brightness, current, true);
        var normal = CameraPictureProfilePolicy.CreateTarget(restarted.Cameras["Camera-A"], Brightness, current, false);

        Assert.Equal(100, dark.Contrast);
        Assert.Equal(116, normal.Contrast);
        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
        Assert.DoesNotContain("IsValid", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void CamerasAreIsolatedByExactDeviceIdAndDisablePreferencePersists()
    {
        var second = new CameraPictureDefaults(null, new(20, -64, 64, 2));
        CameraPictureSettingsStore.Write(SettingsPath, new(false, new(StringComparer.Ordinal)
        {
            ["Camera-A"] = Defaults,
            ["camera-a"] = second
        }));
        var settings = CameraPictureSettingsStore.Read(SettingsPath);
        Assert.False(settings.Enabled);
        Assert.Equal(2, settings.Cameras.Count);
        Assert.Equal(Defaults, settings.Cameras["Camera-A"]);
        Assert.Equal(second, settings.Cameras["camera-a"]);
        Assert.False(settings.Cameras.ContainsKey("CAMERA-A"));
        Assert.Equal(SettingsPath, Assert.Single(Directory.EnumerateFiles(_directory)));
    }

    [Fact]
    public void ExplicitRecaptureReplacesOnlyTheChosenCameraDefault()
    {
        CameraPictureSettingsStore.Write(SettingsPath, new(false, new(StringComparer.Ordinal)
        {
            ["camera-one"] = Defaults, ["camera-two"] = Defaults
        }));
        var settings = CameraPictureSettingsStore.Read(SettingsPath);
        settings.Cameras["camera-one"] = CameraPictureProfilePolicy.CaptureDefaults(Brightness,
            Contrast with { Value = 104 })!;
        CameraPictureSettingsStore.Write(SettingsPath, settings);
        var reloaded = CameraPictureSettingsStore.Read(SettingsPath);
        Assert.Equal(104, reloaded.Cameras["camera-one"].Contrast!.Value);
        Assert.Equal(116, reloaded.Cameras["camera-two"].Contrast!.Value);
        Assert.False(reloaded.Enabled);
        Assert.Equal(SettingsPath, Assert.Single(Directory.EnumerateFiles(_directory)));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Version\":2,\"Enabled\":true,\"Cameras\":{}}")]
    [InlineData("{\"Version\":\"1\",\"Enabled\":true,\"Cameras\":{}}")]
    [InlineData("{\"Version\":1,\"Enabled\":\"true\",\"Cameras\":{}}")]
    [InlineData("{\"Version\":1,\"Cameras\":{}}")]
    [InlineData("{\"Version\":1,\"Enabled\":true,\"Cameras\":[]}")]
    [InlineData("{\"Version\":1,\"Enabled\":true,\"Cameras\":{\"camera\":null}}")]
    [InlineData("{\"Version\":1,\"Enabled\":true,\"Cameras\":{\"camera\":{\"Brightness\":null,\"Contrast\":null}}}")]
    [InlineData("{\"Version\":1,\"Enabled\":true,\"Cameras\":{\"camera\":{\"Contrast\":{\"Value\":116,\"Minimum\":0,\"Maximum\":200,\"Step\":1}}}}")]
    [InlineData("{\"Version\":1,\"Enabled\":true,\"Cameras\":{\"camera\":{\"Brightness\":null,\"Contrast\":{\"Minimum\":0,\"Maximum\":200,\"Step\":1}}}}")]
    public void InvalidFilesAreNotTreatedAsMissingAndRemainUntouched(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, json);
        Assert.Throws<InvalidDataException>(() => CameraPictureSettingsStore.Read(SettingsPath));
        Assert.Equal(json, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void DuplicateCameraIdsAreRejectedInsteadOfSilentlyOverwritingDefaults()
    {
        string profile = JsonSerializer.Serialize(Defaults);
        string json = $"{{\"Version\":1,\"Enabled\":true,\"Cameras\":{{\"camera\":{profile},\"camera\":{profile}}}}}";
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, json);
        Assert.Throws<InvalidDataException>(() => CameraPictureSettingsStore.Read(SettingsPath));
        Assert.Equal(json, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void MalformedJsonAndReadFailuresCannotTriggerFreshBaselineCapture()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{");
        Assert.ThrowsAny<JsonException>(() => CameraPictureSettingsStore.Read(SettingsPath));
        using var locked = new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => CameraPictureSettingsStore.Read(SettingsPath));
    }

    [Fact]
    public void InvalidWritesPreserveExistingDefaults()
    {
        CameraPictureSettingsStore.Write(SettingsPath, new(true, new() { ["camera"] = Defaults }));
        byte[] before = File.ReadAllBytes(SettingsPath);
        Assert.Throws<InvalidDataException>(() => CameraPictureSettingsStore.Write(SettingsPath,
            new(false, new() { ["camera"] = new(null, Contrast with { Step = 0 }) })));
        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
        Assert.Equal(SettingsPath, Assert.Single(Directory.EnumerateFiles(_directory)));
    }

    [Fact]
    public void FailedAtomicPublishRemovesItsTemporaryFile()
    {
        Directory.CreateDirectory(SettingsPath);
        var error = Record.Exception(() => CameraPictureSettingsStore.Write(SettingsPath,
            new(true, new() { ["camera"] = Defaults })));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.True(Directory.Exists(SettingsPath));
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
