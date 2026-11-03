#if DEBUG
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Private render targets: no webcam, projector window or live scene is changed.
    private object VerifyCameraLatency()
    {
        using var scene = new SceneCompositor();
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 640, 400, 96);
        scene.SetBoardSetup(true);
        scene.ShowBoardCalibrationSpot(0);
        Require(scene.CalibrationSpotPresentation is null, "Request time was used as display time.");
        Draw(preview: true);
        Require(scene.CalibrationSpotPresentation is null, "Laptop preview started the projector timer.");
        Draw(preview: false);
        var first = scene.CalibrationSpotPresentation;
        Require(first is { SpotIndex: 0 }, "Projector dot draw was not timestamped.");
        var pixels = target.GetPixelBytes();
        Draw(preview: false);
        Require(ReferenceEquals(first, scene.CalibrationSpotPresentation), "Repeated draw restarted the timer.");
        Require(pixels.SequenceEqual(target.GetPixelBytes()), "Timing changed the projected dot image.");
        scene.ShowBoardCalibrationSpot(1);
        Require(scene.CalibrationSpotPresentation is null, "The next dot retained a previous timestamp.");
        Draw(preview: true);
        Require(scene.CalibrationSpotPresentation is null, "A second preview started the timer.");
        Draw(preview: false);
        var second = scene.CalibrationSpotPresentation;
        Require(second is { SpotIndex: 1 } && second.Revision != first!.Revision, "Dot revisions were reused.");
        scene.SetBlackOutput(true);
        Require(scene.CalibrationSpotPresentation is null, "Black output exposed an active dot timestamp.");
        scene.SetBoardSetup(false);
        Require(scene.CalibrationSpotPresentation is null, "Ended setup retained a dot timestamp.");
        scene.SetBoardSetup(true);
        scene.ShowBoardCalibrationSpot(0);
        Draw(preview: false);
        Require(scene.CalibrationSpotPresentation?.Revision != first!.Revision, "Rescan reused the old dot.");

        var timing = new CameraLatencyMeasurement { ProjectorMilliseconds = 1000d / 60 };
        Require(timing.Record(0, first.DrawnAt, first.DrawnAt.AddMilliseconds(97)) &&
            Math.Abs(timing.MedianMilliseconds!.Value - (97 - 1000d / 60)) < 1e-9,
            "The refresh-derived projector allowance was not subtracted.");
        Require(_cameraLatency.ProjectorMilliseconds == (_projectorFrameMilliseconds ?? 0),
            "The automatic projector allowance does not match the calculation.");
        VerifyAutomaticProjectorLatency();
        Require(CameraLatencyText is not null, "The laptop latency display is missing.");
        return new { passed = true, previewExcluded = true, firstDrawOnly = true,
            dotAndRescanRevisionsIsolated = true, projectedImageUnchanged = true,
            automaticProjectorLatency = true, projectorMilliseconds = _projectorFrameMilliseconds,
            liveHardwareUnchanged = true };

        void Draw(bool preview)
        {
            using var drawing = target.CreateDrawingSession();
            scene.Draw(drawing, 640, 400, preview, runningSlowly: false);
        }
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }

    // Also runnable without creating an app window, camera, or projection output.
    private static object VerifyAutomaticProjectorLatency()
    {
        int checks = 0;
        var mode = new Projection.DisplayModeInfo("DISPLAY1", 3840, 2160, 60)
        {
            MonitorDevicePath = "test-projector", RotationDegrees = 0, HasPreciseRefreshRate = true
        };
        Check(Projection.NativeDisplayMetadata.RefreshHertzFromRatio(60000, 1001) == 60000d / 1001,
            "Fractional active refresh was rounded.");
        foreach (var pair in new[] { (0u, 1u), (60u, 0u), (1u, 1u) })
            Check(Projection.NativeDisplayMetadata.RefreshHertzFromRatio(pair.Item1, pair.Item2) is null,
                "Unknown native refresh invented an allowance.");
        foreach (double rate in new[] { 30d, 120d, 60000d / 1001 })
            Check(ProjectionModeChanged(mode, mode with { RefreshHertz = rate }),
                "A refresh change with unchanged desktop bounds was missed.");
        Check(!ProjectionModeChanged(mode, mode with { FriendlyName = "Renamed" }) &&
            !ProjectionModeChanged(mode, null), "Transient metadata loss reset calibration.");
        var fractional = mode with { RefreshHertz = 60000d / 1001 };
        foreach (double rate in new[] { 0d, 1d, 59d, 60d })
            Check(!ProjectionModeChanged(fractional, fractional with
                { RefreshHertz = rate, HasPreciseRefreshRate = false }),
                "Temporary integer or missing refresh metadata reset calibration.");
        Check(ProjectionModeChanged(fractional, fractional with
            { RefreshHertz = 30, HasPreciseRefreshRate = false }), "A real integer refresh change was ignored.");
        Check(ProjectionModeChanged(mode with { RefreshHertz = 0 }, mode),
            "Recovery from initially unknown refresh was ignored.");
        Check(ProjectionModeChanged(mode with { HasPreciseRefreshRate = false }, fractional),
            "A newly available exact refresh rate was ignored.");

        string key = Projection.ProjectionSizeProfile.PersistentKey(mode)!;
        var expected = new Projection.ProjectionSizeProfile
        {
            LensHeightCentimeters = 120, ThrowRatio = 1.5,
            MeasuredBoardShortSideCentimeters = 56, MeasuredBoardLongSideCentimeters = 72,
            BoardFacingDegrees = 90, EnableDisplayAudio = false
        };
        // Even malformed obsolete values must not discard unrelated saved preferences.
        foreach (object? oldDelay in new object?[] { 17d, 32.5d, -1d, 2001d, "obsolete", null })
        {
            string json = System.Text.Json.JsonSerializer.Serialize(new
            {
                Version = 2, Profiles = new Dictionary<string, object>
                {
                    [key] = new
                    {
                        expected.LensHeightCentimeters, expected.ThrowRatio,
                        expected.MeasuredBoardShortSideCentimeters, expected.MeasuredBoardLongSideCentimeters,
                        expected.BoardFacingDegrees, expected.EnableDisplayAudio,
                        ProjectorLatencyMilliseconds = oldDelay
                    }
                }
            });
            var restored = ParseProjectionSettings(json, "unused", mode);
            Check(restored.Profiles[key] == expected, "An obsolete delay changed saved output preferences.");
            Check(!System.Text.Json.JsonSerializer.Serialize(restored).Contains("ProjectorLatencyMilliseconds",
                StringComparison.Ordinal), "A manual projector delay was persisted again.");
        }
        return new { passed = true, checks, fractionalRefresh = true, refreshChangesInvalidateTiming = true,
            legacyDelayIgnored = true, liveHardwareUnchanged = true };

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
    }
}
#endif
