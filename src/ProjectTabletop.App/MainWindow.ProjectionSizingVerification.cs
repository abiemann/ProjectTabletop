#if DEBUG
using System.Text.Json;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private object VerifyProjectionSizing()
    {
        var empty = new ProjectionSizeProfile();
        var profile = new ProjectionSizeProfile { LensHeightCentimeters = 120, ThrowRatio = 1.5 };
        var fourThree = Mode("projector-a", 1600, 1200);
        Require(empty.IsValid && empty.ImageSize(fourThree, out _) is null, "New outputs invented optics.");
        Require((profile with { ThrowRatio = null }).ImageSize(fourThree, out _) is null &&
            (profile with { LensHeightCentimeters = null }).ImageSize(fourThree, out _) is null,
            "An incomplete profile produced a size.");
        Check(profile, fourThree, 80, 60);
        Check(profile, Mode("projector-b", 1920, 1200), 80, 50);
        Check(profile, Mode("projector-c", 1920, 1080), 80, 45);
        Check(profile, fourThree with { Width = 1200, Height = 1600, RotationDegrees = 90 }, 60, 80);
        Check(profile, fourThree with { Width = 1200, Height = 1600, RotationDegrees = 270 }, 60, 80);
        Check(profile, fourThree with { RotationDegrees = 180 }, 80, 60);
        Require(profile.ImageSize(fourThree with { PreferredPixelWidth = 1920, PreferredPixelHeight = 1080 }, out _) is null,
            "A potentially letterboxed mode silently used the entire optical field.");
        Require(profile.ImageSize(null, out _) is null, "Missing output resolution produced a size.");
        foreach (double invalid in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
            Require((profile with { ThrowRatio = invalid }).ImageSize(fourThree, out _) is null &&
                (profile with { LensHeightCentimeters = invalid }).ImageSize(fourThree, out _) is null,
                "Invalid input produced a size.");

        var measured = new ProjectionSizeProfile
        {
            MeasuredBoardShortSideCentimeters = 56, MeasuredBoardLongSideCentimeters = 72
        };
        var reference = new BoardSizeEstimate(56, 72);
        Require(measured.IsValid && measured.MeasuredBoardSize == reference &&
                measured.ImageSize(fourThree, out _) is null && empty.MeasuredBoardSize is null,
            "A measured reference required optics, or missing dimensions invented a board size.");
        Require((measured with { ThrowRatio = -1 }).MeasuredBoardSize == reference &&
                !(measured with { ThrowRatio = -1 }).IsValid,
            "Invalid optional optics changed the independently supplied board reference or became persistable.");
        foreach (var partial in new[]
        {
            measured with { MeasuredBoardShortSideCentimeters = null },
            measured with { MeasuredBoardLongSideCentimeters = null },
            measured with { MeasuredBoardShortSideCentimeters = null, MeasuredBoardLongSideCentimeters = null }
        })
            Require(partial.IsValid && partial.MeasuredBoardSize is null,
                "Clearing either measured dimension retained a complete reference or rejected a partial entry.");
        Require((measured with { MeasuredBoardLongSideCentimeters = 56 }).MeasuredBoardSize == new BoardSizeEstimate(56, 56),
            "A valid square-board reference was rejected.");
        foreach (double invalid in new[] { 0d, -1d, .01, 10001, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Require(!(measured with { MeasuredBoardShortSideCentimeters = invalid }).IsValid &&
                    !(measured with { MeasuredBoardLongSideCentimeters = invalid }).IsValid &&
                    (measured with { MeasuredBoardShortSideCentimeters = invalid }).MeasuredBoardSize is null &&
                    (measured with { MeasuredBoardLongSideCentimeters = invalid }).MeasuredBoardSize is null,
                "An invalid measured dimension produced a reference or a persistable profile.");
        var reversed = measured with { MeasuredBoardShortSideCentimeters = 72, MeasuredBoardLongSideCentimeters = 56 };
        Require(!reversed.IsValid && reversed.MeasuredBoardSize is null,
            "Reversed short and long dimensions were silently sorted or accepted.");
        var combined = profile with { MeasuredBoardShortSideCentimeters = 56, MeasuredBoardLongSideCentimeters = 72 };
        Check(combined, fourThree, 80, 60);
        Check(combined, fourThree with { Width = 1200, Height = 1600, RotationDegrees = 90 }, 60, 80);
        Check(profile with { MeasuredBoardShortSideCentimeters = 72, MeasuredBoardLongSideCentimeters = 56 }, fourThree, 80, 60);

        string key = ProjectionSizeProfile.PersistentKey(fourThree)!;
        Require(!ProjectionModeChanged(fourThree, null) &&
            !ProjectionModeChanged(fourThree, fourThree with { FriendlyName = "Temporary name" }) &&
            !ProjectionModeChanged(fourThree, fourThree with { MonitorDevicePath = null, PreferredPixelWidth = null, PreferredPixelHeight = null }) &&
            ProjectionModeChanged(fourThree, fourThree with { Width = 800, Height = 600 }),
            "Transient metadata failure reset alignment, or a real mode change was missed.");
        Require(key == ProjectionSizeProfile.PersistentKey(fourThree with { FriendlyName = "Renamed display", DeviceName = "DISPLAY9" }) &&
            key != ProjectionSizeProfile.PersistentKey(Mode("projector-b", 1600, 1200)) &&
            key != ProjectionSizeProfile.PersistentKey(fourThree with { Width = 800, Height = 600 }) &&
            key != ProjectionSizeProfile.PersistentKey(fourThree with { RotationDegrees = 180 }) &&
            ProjectionSizeProfile.PersistentKey(fourThree with { MonitorDevicePath = null }) is null,
            "Profile identity leaked across devices or modes, or depended on a display number.");
        var settings = new ProjectionSetupSettings();
        settings.Profiles[key] = profile;
        settings.Profiles[ProjectionSizeProfile.PersistentKey(Mode("projector-b", 1920, 1080))!] = empty;
        var restored = ParseProjectionSettings(JsonSerializer.Serialize(settings), "unused", fourThree);
        Require(restored.Profiles[key] == profile && restored.Profiles.Count == 2,
            "Per-output settings did not survive persistence.");
        settings.Profiles[key] = profile with { ThrowRatio = null, LensHeightCentimeters = null };
        restored = ParseProjectionSettings(JsonSerializer.Serialize(settings), "unused", fourThree);
        Require(restored.Profiles[key] == empty, "Cleared optional fields returned after reload.");

        string secondKey = ProjectionSizeProfile.PersistentKey(Mode("projector-b", 1920, 1080))!;
        Require(empty.BoardFacingDegrees is null, "A new profile invented the user's viewing side.");
        settings.Profiles[key] = combined with { BoardFacingDegrees = 181.5 };
        settings.Profiles[secondKey] = empty with { BoardFacingDegrees = 90 };
        restored = ParseProjectionSettings(JsonSerializer.Serialize(settings), "unused", fourThree);
        Require(restored.Profiles[key].BoardFacingDegrees == 181.5 &&
            restored.Profiles[secondKey].BoardFacingDegrees == 90 &&
            restored.Profiles[key].MeasuredBoardSize == reference,
            "Board facing did not survive reload independently for each output.");
        foreach (double invalid in new[] { -1d, 360d, double.NaN, double.PositiveInfinity })
            Require(!(empty with { BoardFacingDegrees = invalid }).IsValid, "Invalid board facing was accepted.");
        var editedFacing = EnteredProjectionProfile();
        Require(editedFacing.BoardFacingDegrees == _boardFacingDegrees,
            "Editing dimensions or audio would discard the current board facing.");
        var otherReference = measured with { MeasuredBoardShortSideCentimeters = 40, MeasuredBoardLongSideCentimeters = 60 };
        settings.Profiles[key] = combined;
        settings.Profiles[secondKey] = otherReference;
        string measuredJson = JsonSerializer.Serialize(settings);
        restored = ParseProjectionSettings(measuredJson, "unused", fourThree);
        Require(restored.Profiles[key] == combined && restored.Profiles[key].MeasuredBoardSize == reference &&
                restored.Profiles[secondKey] == otherReference &&
                restored.Profiles[secondKey].MeasuredBoardSize == new BoardSizeEstimate(40, 60) &&
                !measuredJson.Contains("\"MeasuredBoardSize\"", StringComparison.Ordinal),
            "Measured inputs lost persistence, crossed output profiles, or serialized derived state.");
        settings.Profiles[key] = combined with { MeasuredBoardLongSideCentimeters = null };
        restored = ParseProjectionSettings(JsonSerializer.Serialize(settings), "unused", fourThree);
        Require(restored.Profiles[key].MeasuredBoardShortSideCentimeters == 56 &&
                restored.Profiles[key].MeasuredBoardLongSideCentimeters is null &&
                restored.Profiles[key].MeasuredBoardSize is null,
            "A partial measured entry could not be persisted or invented its missing dimension.");
        settings.Profiles[key] = profile;
        restored = ParseProjectionSettings(JsonSerializer.Serialize(settings), "unused", fourThree);
        Require(restored.Profiles[key] == profile && restored.Profiles[key].MeasuredBoardSize is null &&
                restored.Profiles[secondKey].MeasuredBoardSize == new BoardSizeEstimate(40, 60),
            "Clearing a measured reference restored old values or changed another output.");
        string oldVersionTwo = JsonSerializer.Serialize(new
        {
            Version = 2, Profiles = new Dictionary<string, object>
            {
                [key] = new { LensHeightCentimeters = 120, ThrowRatio = 1.5 }
            }
        });
        restored = ParseProjectionSettings(oldVersionTwo, "unused", fourThree);
        Require(restored.Profiles[key] == profile && restored.Profiles[key].MeasuredBoardSize is null &&
                restored.Profiles[key].EnableDisplayAudio,
            "Version 2 settings without new fields changed optics, invented a board reference, or disabled display audio.");

        var audioOnly = new ProjectionSizeProfile { EnableDisplayAudio = false };
        Require(audioOnly.IsValid && audioOnly.ImageSize(fourThree, out _) is null &&
                audioOnly.MeasuredBoardSize is null,
            "An audio-only output preference required size settings or invented dimensions.");
        var audioSettings = new ProjectionSetupSettings();
        audioSettings.Profiles[key] = combined with { EnableDisplayAudio = false };
        audioSettings.Profiles[secondKey] = otherReference with { EnableDisplayAudio = true };
        var restoredAudio = ParseProjectionSettings(JsonSerializer.Serialize(audioSettings), "unused", fourThree);
        Require(restoredAudio.Profiles[key] == (combined with { EnableDisplayAudio = false }) &&
                restoredAudio.Profiles[secondKey] == (otherReference with { EnableDisplayAudio = true }),
            "Display audio preferences were lost on reload or crossed output profiles.");
        audioSettings.Profiles[key] = audioOnly;
        restoredAudio = ParseProjectionSettings(JsonSerializer.Serialize(audioSettings), "unused", fourThree);
        Require(restoredAudio.Profiles[key] == audioOnly && restoredAudio.Profiles[secondKey].EnableDisplayAudio &&
                restoredAudio.Profiles[secondKey].MeasuredBoardSize == new BoardSizeEstimate(40, 60),
            "Saving an audio-only preference changed its value or another display's settings.");
        const string legacy = "{\"Version\":1,\"LensHeightCentimeters\":123,\"GeometryConfirmed\":true,\"DisplayId\":\"old-id\"}";
        restored = ParseProjectionSettings(legacy, "old-id", fourThree);
        Require(restored.Profiles[key].LensHeightCentimeters == 123 && restored.Profiles[key].ThrowRatio is null &&
            restored.Profiles[key].EnableDisplayAudio &&
            ParseProjectionSettings(legacy, "different-id", fourThree).Profiles.Count == 0,
            "Legacy settings invented a throw ratio or migrated to a different output.");
        var unidentified = fourThree with { MonitorDevicePath = null };
        Require(ParseProjectionSettings(legacy, "old-id", unidentified).Profiles[
                ProjectionSizeProfile.SessionKey("old-id", unidentified)].LensHeightCentimeters == 123 &&
            ProjectionSizeProfile.SessionKey("old-id", unidentified) != ProjectionSizeProfile.SessionKey("old-id", unidentified with { Width = 800 }),
            "Unidentified output lost its legacy height or shared a session profile across modes.");
        // An invalid profile is skipped without discarding another output's settings.
        foreach (string invalid in new[]
        {
            "{\"ThrowRatio\":-1}",
            "{\"MeasuredBoardShortSideCentimeters\":72,\"MeasuredBoardLongSideCentimeters\":56}",
            "{\"MeasuredBoardShortSideCentimeters\":0}",
            "{\"ThrowRatio\":\"oops\"}",
            "{\"EnableDisplayAudio\":{}}",
            "{\"LastAlignment\":{\"Width\":\"oops\"}}",
            "null", "[]", "true"
        })
        {
            var partial = ParseProjectionSettings("{\"Version\":2,\"Profiles\":{\"bad\":" + invalid +
                ",\"good\":{\"LensHeightCentimeters\":120}}}", "id", fourThree, out int skipped);
            Require(skipped == 1 && !partial.Profiles.ContainsKey("bad") &&
                partial.Profiles["good"].LensHeightCentimeters == 120,
                "An invalid persisted profile was accepted or discarded another output's settings.");
        }
        try
        {
            ParseProjectionSettings("{\"Version\":3,\"Profiles\":{}}", "id", fourThree);
            throw new InvalidOperationException("An unsupported settings version was accepted.");
        }
        catch (InvalidDataException) { }
        Point2[] corners = [new(.1, .2), new(.9, .2), new(.9, .8), new(.1, .8)];
        var image = profile.ImageSize(fourThree, out _)!.Value;
        var board = BoardSizeEstimate.FromImageDimensions(corners, image.Width, image.Height);
        Require(Math.Abs(board.ShortSideCentimeters - 36) < 1e-8 && Math.Abs(board.LongSideCentimeters - 64) < 1e-8,
            "Board edges were confused with the full output image.");

        string files = Path.Combine(Path.GetTempPath(), "ProjectTabletop", "ProjectionSizingVerification", Guid.NewGuid().ToString("N"));
        string settingsPath = Path.Combine(files, "projection-setup.json");
        var fileSettings = new ProjectionSetupSettings();
        fileSettings.Profiles[key] = combined with { BoardFacingDegrees = .4565473707345973 };
        fileSettings.Profiles[secondKey] = otherReference with { BoardFacingDegrees = 90 };
        Require(TrySaveProjectionSettings(fileSettings, settingsPath, out var saveError) && saveError is null,
            "A fresh settings file could not be saved, or a successful write reported an error.");
        var firstSaved = ParseProjectionSettings(File.ReadAllText(settingsPath), "unused", fourThree);
        Require(firstSaved.Profiles[key] == fileSettings.Profiles[key],
            "A fresh settings file changed the saved facing.");
        var correctedFacing = combined with { BoardFacingDegrees = 180.4565473707345973 };
        fileSettings.Profiles[key] = correctedFacing;
        Require(TrySaveProjectionSettings(fileSettings, settingsPath, out saveError) && saveError is null,
            "An existing settings file could not be replaced with the corrected facing.");
        string savedJson = File.ReadAllText(settingsPath);
        var savedFacing = ParseProjectionSettings(savedJson, "unused", fourThree);
        Require(savedFacing.Profiles[key] == correctedFacing &&
                savedFacing.Profiles[secondKey] == fileSettings.Profiles[secondKey],
            "Replacing the saved facing restored the old angle or changed another output's profile.");

        fileSettings.Profiles[key] = firstSaved.Profiles[key];
        using (var lockedSettings = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Require(!TrySaveProjectionSettings(fileSettings, settingsPath, out saveError) &&
                    !string.IsNullOrWhiteSpace(saveError),
                "A locked settings target reported success or hid its write error.");
            Require(File.ReadAllText(settingsPath) == savedJson,
                "A failed settings replacement changed the last saved facing.");
        }
        string directoryTarget = Path.Combine(files, "directory-target.json");
        Directory.CreateDirectory(directoryTarget);
        string retainedFile = Path.Combine(directoryTarget, "retained.txt");
        File.WriteAllText(retainedFile, "keep");
        Require(!TrySaveProjectionSettings(fileSettings, directoryTarget, out saveError) &&
                !string.IsNullOrWhiteSpace(saveError) && File.ReadAllText(retainedFile) == "keep" &&
                File.ReadAllText(settingsPath) == savedJson,
            "A directory destination reported success, hid its error, or damaged existing files.");

        File.Delete(settingsPath + ".tmp");
        Directory.CreateDirectory(settingsPath + ".tmp");
        Require(!TrySaveProjectionSettings(fileSettings, settingsPath, out saveError) &&
                !string.IsNullOrWhiteSpace(saveError) && File.ReadAllText(settingsPath) == savedJson,
            "A blocked temporary write reported success, hid its error, or changed the saved facing.");
        Directory.Delete(settingsPath + ".tmp");
        fileSettings.Profiles[key] = correctedFacing;
        Require(TrySaveProjectionSettings(fileSettings, settingsPath, out saveError) && saveError is null &&
                ParseProjectionSettings(File.ReadAllText(settingsPath), "unused", fourThree).Profiles[key] == correctedFacing,
            "Retrying a settings save after the obstruction was removed did not recover or retained the old error.");

        string rejectedPath = Path.Combine(files, "projection-setup.rejected.json");
        const string partiallyInvalid = "{\"Version\":2,\"Profiles\":{\"bad\":{\"ThrowRatio\":\"oops\"},\"good\":{\"LensHeightCentimeters\":120}}}";
        File.WriteAllText(settingsPath, partiallyInvalid);
        var salvaged = ParseProjectionSettings(partiallyInvalid, "unused", fourThree, out int rejectedCount);
        Require(rejectedCount == 1, "The backup fixture did not encounter its invalid profile.");
        Directory.CreateDirectory(rejectedPath);
        Require(!TryKeepRejectedProjectionSettings(settingsPath, rejectedPath, out var backupError) &&
                !string.IsNullOrWhiteSpace(backupError), "A blocked rejected-file backup hid its failure.");
        Require(!TrySaveProjectionSettings(salvaged, settingsPath, out saveError, rejectedPath) &&
                !string.IsNullOrWhiteSpace(saveError) && File.ReadAllText(settingsPath) == partiallyInvalid,
            "A later settings save overwrote the rejected original after its backup failed.");
        Directory.Delete(rejectedPath);
        Require(TrySaveProjectionSettings(salvaged, settingsPath, out saveError, rejectedPath) && saveError is null &&
                File.ReadAllText(rejectedPath) == partiallyInvalid &&
                ParseProjectionSettings(File.ReadAllText(settingsPath), "unused", fourThree).Profiles["good"].LensHeightCentimeters == 120,
            "Retrying the backup did not preserve the exact original before saving the valid profiles.");
        return new { passed = true, optionalFields = true, genericAspectRatios = true, portrait = true,
            outputProfilesIsolated = true, persistenceAndLegacyMigration = true, invalidInputsRejected = true,
            boardUsesDetectedEdges = true, measuredReferenceIndependentOfOptics = true,
            measuredPartialAndClear = true, measuredPersistenceAndVersionTwoCompatibility = true,
            displayAudioDefaultsAndPersistence = true, displayAudioProfilesIsolated = true,
            displayAudioWithoutSizeSettings = true,
            settingsFileWriteAndOverwrite = true, settingsFileFailuresPreserveSavedFacing = true,
            settingsFileSaveRetryRecovery = true,
            invalidProfileTypeAndShapeSalvage = true, rejectedBackupFailureBlocksOverwrite = true,
            rejectedBackupRetryPreservesOriginal = true,
            opticalFormulaUnchanged = true, liveSettingsUnchanged = true };

        static DisplayModeInfo Mode(string id, int width, int height) => new("DISPLAY1", width, height, 60)
        {
            MonitorDevicePath = id, FriendlyName = "Generic output", PreferredPixelWidth = width,
            PreferredPixelHeight = height, RotationDegrees = 0
        };
        static void Check(ProjectionSizeProfile profile, DisplayModeInfo mode, double width, double height)
        {
            var size = profile.ImageSize(mode, out _) ?? throw new InvalidOperationException("A complete profile had no estimate.");
            Require(Math.Abs(size.Width - width) < 1e-8 && Math.Abs(size.Height - height) < 1e-8,
                "Wrong optical dimensions for the output aspect or rotation.");
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
