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

        var timing = new CameraLatencyMeasurement();
        Require(timing.Record(0, first.DrawnAt, first.DrawnAt.AddMilliseconds(97)) &&
            timing.MedianMilliseconds == 80, "The 17 ms projector allowance was not subtracted.");
        timing.ProjectorMilliseconds = 32.5;
        Require(timing.MedianMilliseconds == 64.5, "Changing projector latency did not recalculate the result.");
        Require(ProjectorLatencyNumberBox.Value == _cameraLatency.ProjectorMilliseconds,
            "The entered projector latency does not match the calculation.");
        Require(CameraLatencyText is not null, "The laptop latency display is missing.");
        return new { passed = true, previewExcluded = true, firstDrawOnly = true,
            dotAndRescanRevisionsIsolated = true, projectedImageUnchanged = true,
            configurableProjectorLatency = true, projectorMilliseconds = _cameraLatency.ProjectorMilliseconds,
            liveHardwareUnchanged = true };

        void Draw(bool preview)
        {
            using var drawing = target.CreateDrawingSession();
            scene.Draw(drawing, 640, 400, preview, runningSlowly: false);
        }
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
#endif
