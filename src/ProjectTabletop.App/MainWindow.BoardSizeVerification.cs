#if DEBUG
using System.Numerics;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Isolated compositor pixels exercise annotation caching without altering the live
    // calibration, projector, camera, settings, or selected board.
    private async Task<object> VerifyBoardSizeAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen,
            _scene.HasBoardMediaClip, _scene.BlackjackState.Revision);
        const int size = 1200;
        Vector2[] corners = [new(.1f, .1f), new(.9f, .1f), new(.9f, .9f), new(.1f, .9f)];
        Point2[] unit = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        var cameraMap = Homography.FromFourPoints(unit, unit);
        using var scene = new SceneCompositor();
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 96);
        scene.SetDisplayAspect(1);
        scene.SetBoardSetup(true);
        double inset = scene.SetDetectedBoardGrid(corners, cameraMap);
        scene.SetBoardSetup(false);
        scene.ShowHandTrackingTest();
        var baseline = Draw();
        scene.SetEstimatedBoardSize(new(53.2, 71.4));
        var annotated = Draw();
        Require(DifferentPixels(baseline, annotated) > 1000,
            "Setting an estimate did not redraw the cached Hand-Tracking surface.");
        Require(DifferentPixels(baseline, annotated, aboveFooterOnly: true) == 0,
            "The estimate changed the tester's controls, gesture status, or central grid.");
        scene.SetEstimatedBoardSize(new(53.2, 71.4));
        Require(annotated.SequenceEqual(Draw()), "An unchanged estimate changed its rendered pixels.");
        scene.SetEstimatedBoardSize(new(53.2, 71.4), measured: true);
        var measuredSameSize = Draw();
        Require(DifferentPixels(annotated, measuredSameSize) > 100 &&
            DifferentPixels(baseline, measuredSameSize, aboveFooterOnly: true) == 0,
            "Changing only the dimension source failed to refresh the label or changed projection geometry.");
        scene.SetEstimatedBoardSize(new(53.2, 71.4), measured: true);
        Require(measuredSameSize.SequenceEqual(Draw()), "An unchanged measured annotation changed its rendered pixels.");
        scene.SetEstimatedBoardSize(new(53.2, 71.4));
        Require(annotated.SequenceEqual(Draw()), "Returning to optical dimensions retained the measured caption/source.");
        scene.SetEstimatedBoardSize(new(64.7, 83.9));
        Require(DifferentPixels(annotated, Draw()) > 20,
            "A changed estimate remained stale in the cached surface.");
        scene.SetEstimatedBoardSize(null);
        Require(baseline.SequenceEqual(Draw()), "Clearing the estimate left its footer on the grid.");

        scene.ShowBoardMenu();
        var menu = Draw();
        scene.SetEstimatedBoardSize(new(56, 72), measured: true);
        Require(menu.SequenceEqual(Draw()), "Measured board dimensions appeared on the main menu.");
        scene.SetEstimatedBoardSize(new(53.2, 71.4));
        Require(menu.SequenceEqual(Draw()), "A board-size annotation appeared on the main menu.");
        scene.ShowHandTrackingTest();
        Require(annotated.SequenceEqual(Draw()), "The saved current estimate did not appear when returning to the tester.");

        string directory = Path.Combine(_appDataDirectory, "BoardSizeSnapshots", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "hand-tracking-estimated-board-size.png");
        await target.SaveAsync(path, CanvasBitmapFileFormat.Png);

        scene.SetEstimatedBoardSize(new(56, 72), measured: true);
        var measured = Draw();
        Require(DifferentPixels(annotated, measured) > 100 &&
            DifferentPixels(baseline, measured, aboveFooterOnly: true) == 0,
            "Measured 56 × 72 cm dimensions failed to render without changing the working grid.");
        string measuredPath = Path.Combine(directory, "hand-tracking-measured-board-size.png");
        await target.SaveAsync(measuredPath, CanvasBitmapFileFormat.Png);
        scene.SetEstimatedBoardSize(null, measured: true);
        Require(baseline.SequenceEqual(Draw()), "Clearing measured dimensions retained their caption or source.");
        scene.SetEstimatedBoardSize(new(56, 72), measured: true);
        Draw();

        scene.ClearBoardMediaClip();
        var cleared = Draw();
        Require(Enumerable.Range(0, cleared.Length / 4).All(pixel =>
                cleared[pixel * 4] == 0 && cleared[pixel * 4 + 1] == 0 && cleared[pixel * 4 + 2] == 0),
            "Clearing the board clip left annotation or other board pixels visible.");
        // A late setter call made before the next valid scan must not preserve stale text.
        scene.SetEstimatedBoardSize(new(56, 72), measured: true);
        scene.SetBoardSetup(true);
        scene.SetDetectedBoardGrid(corners, cameraMap);
        scene.SetBoardSetup(false);
        Require(baseline.SequenceEqual(Draw()), "A new scan restored a stale estimate without a new measurement.");
        scene.SetEstimatedBoardSize(new(53.2, 71.4));
        Require(annotated.SequenceEqual(Draw()), "An optical estimate after rescan failed or retained stale measured-source text.");

        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen,
            _scene.HasBoardMediaClip, _scene.BlackjackState.Revision),
            "The isolated board-size verification changed live hardware or gameplay.");
        return new { passed = true, estimateRenders = true, cacheUpdates = true,
            measuredDimensionsRender = true, sourceOnlyChangesInvalidate = true, opticalFallbackRestored = true,
            clearingRestoresGrid = true, topControlsAndGridUnchanged = true, menuHasNoEstimate = true,
            clipLossHidesImmediately = true, staleEstimateDiscarded = true, liveHardwareUnchanged = true,
            directory, images = new[] { new { name = "hand-tracking-estimated-board-size", path },
                new { name = "hand-tracking-measured-board-size", path = measuredPath } } };

        byte[] Draw()
        {
            using (var drawing = target.CreateDrawingSession())
                scene.Draw(drawing, size, size, preview: false, runningSlowly: false);
            return target.GetPixelBytes();
        }
        int DifferentPixels(byte[] first, byte[] second, bool aboveFooterOnly = false)
        {
            // Leave room for the panel's anti-aliased border/shadow above logical Y=866.
            int rows = aboveFooterOnly ? (int)(size * (.1 + .8 * (inset / 2 + .84 * (1 - inset)))) : size;
            int count = 0;
            for (int pixel = 0; pixel < rows * size; pixel++)
            {
                int offset = pixel * 4;
                if (first[offset] != second[offset] || first[offset + 1] != second[offset + 1] ||
                    first[offset + 2] != second[offset + 2]) count++;
            }
            return count;
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
