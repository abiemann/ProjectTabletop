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
        Require(restored.Profiles[key] == profile && restored.Profiles[key].MeasuredBoardSize is null,
            "Version 2 settings without measured fields changed optics or invented a board reference.");
        const string legacy = "{\"Version\":1,\"LensHeightCentimeters\":123,\"GeometryConfirmed\":true,\"DisplayId\":\"old-id\"}";
        restored = ParseProjectionSettings(legacy, "old-id", fourThree);
        Require(restored.Profiles[key].LensHeightCentimeters == 123 && restored.Profiles[key].ThrowRatio is null &&
            ParseProjectionSettings(legacy, "different-id", fourThree).Profiles.Count == 0,
            "Legacy settings invented a throw ratio or migrated to a different output.");
        var unidentified = fourThree with { MonitorDevicePath = null };
        Require(ParseProjectionSettings(legacy, "old-id", unidentified).Profiles[
                ProjectionSizeProfile.SessionKey("old-id", unidentified)].LensHeightCentimeters == 123 &&
            ProjectionSizeProfile.SessionKey("old-id", unidentified) != ProjectionSizeProfile.SessionKey("old-id", unidentified with { Width = 800 }),
            "Unidentified output lost its legacy height or shared a session profile across modes.");
        try
        {
            ParseProjectionSettings("{\"Version\":2,\"Profiles\":{\"bad\":{\"ThrowRatio\":-1}}}", "id", fourThree);
            throw new InvalidOperationException("Invalid persisted optics were accepted.");
        }
        catch (InvalidDataException) { }
        foreach (string invalid in new[]
        {
            "{\"Version\":2,\"Profiles\":{\"bad\":{\"MeasuredBoardShortSideCentimeters\":72,\"MeasuredBoardLongSideCentimeters\":56}}}",
            "{\"Version\":2,\"Profiles\":{\"bad\":{\"MeasuredBoardShortSideCentimeters\":0}}}"
        })
        {
            try
            {
                ParseProjectionSettings(invalid, "id", fourThree);
                throw new InvalidOperationException("Invalid persisted measured dimensions were accepted.");
            }
            catch (InvalidDataException) { }
        }
        Point2[] corners = [new(.1, .2), new(.9, .2), new(.9, .8), new(.1, .8)];
        var image = profile.ImageSize(fourThree, out _)!.Value;
        var board = BoardSizeEstimate.FromImageDimensions(corners, image.Width, image.Height);
        Require(Math.Abs(board.ShortSideCentimeters - 36) < 1e-8 && Math.Abs(board.LongSideCentimeters - 64) < 1e-8,
            "Board edges were confused with the full output image.");
        return new { passed = true, optionalFields = true, genericAspectRatios = true, portrait = true,
            outputProfilesIsolated = true, persistenceAndLegacyMigration = true, invalidInputsRejected = true,
            boardUsesDetectedEdges = true, measuredReferenceIndependentOfOptics = true,
            measuredPartialAndClear = true, measuredPersistenceAndVersionTwoCompatibility = true,
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
