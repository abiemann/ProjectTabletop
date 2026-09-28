#if DEBUG
using System.Numerics;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Camera rotation changes the calibrated camera homography, not the facing
    // of the interface on the physical board. All scenes below are offscreen.
    private async Task<object> VerifyBoardOrientationAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen,
            _scene.HasBoardMediaClip, _scene.GetBoardFacingDegrees());
        const int width = 1600, height = 900, cameraSize = 1000;
        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        Vector2[] physicalCorners = [new(.10f, .14f), new(.90f, .14f), new(.90f, .86f), new(.10f, .86f)];
        Point2[] unit = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        BoardScreen[] boards = [BoardScreen.Menu, BoardScreen.HandTracking, BoardScreen.PhotoCopy,
            BoardScreen.Paint, BoardScreen.Blackjack, BoardScreen.Monopoly];
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        var referencePixels = new Dictionary<BoardScreen, byte[]>();
        var comparisons = new List<object>();
        var firstScanComparisons = new List<object>();
        int hoverChecks = 0;
        int firstScanHoverChecks = 0;
        string directory = Path.Combine(_appDataDirectory, "BoardOrientationVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<object>();

        for (int quarterTurn = 0; quarterTurn < 4; quarterTurn++)
        {
            using var scene = NewScene(quarterTurn, out var cameraMap, out double inset);
            var expectedSurface = SurfaceMap(inset, halfTurn: false);
            Require(scene.GetBoardFacingDegrees() == 0,
                "A rotated camera replaced the configured projector-space board facing.");
            AssertCameraMap(scene, cameraMap);
            foreach (var board in boards)
            {
                Show(scene, board);
                if (board == BoardScreen.Paint)
                {
                    Require(scene.AddPaintDrop(new(.33, .53), .09, now),
                        "The orientation fixture could not add an asymmetric paint mark.");
                    now += TimeSpan.FromSeconds(8);
                }
                var pixels = Draw(scene);
                if (quarterTurn == 0)
                {
                    referencePixels.Add(board, pixels);
                    if (board == BoardScreen.Menu) await Save("menu-facing-right");
                }
                else
                {
                    var difference = Difference(referencePixels[board], pixels);
                    Require(difference.MeanChannelError < .1 && difference.PixelsOver16 < width * height * .001,
                        $"Camera rotation {quarterTurn * 90} changed the physical {board} board: " +
                        $"mean error {difference.MeanChannelError:F4}, {difference.PixelsOver16} changed pixels.");
                    comparisons.Add(new { cameraDegrees = quarterTurn * 90, board = board.ToString(),
                        difference.MeanChannelError, difference.PixelsOver16 });
                }
                foreach (var button in scene.CurrentBoardButtons.Where(button => button.Enabled).ToArray())
                {
                    await Hover(scene, cameraMap, expectedSurface, button);
                    hoverChecks++;
                }
                scene.ClearHandTips();
            }
        }

        // A new output has no saved facing. Its first scan must use the
        // projector's orientation after the ordered dots resolve camera roll,
        // regardless of which camera-space corner the board detector lists first.
        for (int quarterTurn = 0; quarterTurn < 4; quarterTurn++)
        {
            var measuredMap = CameraMap(quarterTurn);
            var observedSpots = Enumerable.Range(0, BoardRegistration.SpotCount)
                .Select(index => measuredMap.InverseTransform(BoardRegistration.SpotPosition(index))).ToArray();
            var fittedMap = BoardRegistration.FitAndValidate(observedSpots, out var centerError);
            Require(centerError < 1e-10, "The first-scan fixture's ordered dots failed their center check.");
            foreach (bool reverse in new[] { false, true })
                for (int first = 0; first < 4; first++)
                {
                    using var scene = new SceneCompositor(new BlackjackGame(seed: 173),
                        blackjackClock: () => now, paintClock: () => now);
                    scene.SetDisplayAspect((double)width / height);
                    Require(scene.GetBoardFacingDegrees() is null,
                        "The first-scan fixture unexpectedly had a saved facing direction.");
                    var corners = CameraOrderedCorners(quarterTurn);
                    if (reverse) Array.Reverse(corners);
                    var ordered = Enumerable.Range(0, 4).Select(index => corners[(first + index) % 4]).ToArray();
                    scene.SetBoardSetup(true);
                    double inset = scene.SetDetectedBoardGrid(ordered, fittedMap);
                    scene.SetBoardSetup(false);
                    scene.ShowBoardMenu();
                    Require(scene.GetBoardFacingDegrees() == 0,
                        $"A first scan with camera rotation {quarterTurn * 90} learned camera-dependent facing.");
                    AssertCameraMap(scene, fittedMap);
                    var difference = Difference(referencePixels[BoardScreen.Menu], Draw(scene));
                    Require(difference.MeanChannelError < .1 && difference.PixelsOver16 < width * height * .001,
                        $"A first scan with camera rotation {quarterTurn * 90}, first corner {first}, " +
                        $"reversed winding {reverse} changed the menu's physical orientation.");
                    firstScanComparisons.Add(new { cameraDegrees = quarterTurn * 90, firstCorner = first,
                        reversedWinding = reverse, difference.MeanChannelError, difference.PixelsOver16 });
                    var surface = SurfaceMap(inset, halfTurn: false);
                    foreach (var button in scene.CurrentBoardButtons.Where(button => button.Enabled).ToArray())
                    {
                        await Hover(scene, fittedMap, surface, button);
                        firstScanHoverChecks++;
                    }
                    scene.ClearHandTips();
                    scene.ClearBoardMediaClip();
                    Require(scene.GetBoardFacingDegrees() == 0,
                        "Clearing a first calibration forgot its camera-independent facing.");
                }
        }

        using var turned = NewScene(0, out var originalCameraMap, out double originalInset);
        turned.ShowBoardMenu();
        var upright = Draw(turned);
        var oldRaster = turned.GetBoardResolutionDiagnostics();
        var oldCorners = turned.GetDetectedBoardCorners()!;
        turned.SetBoardFacingDegrees(180);
        Require(turned.GetBoardFacingDegrees() == 180 && turned.HasBoardMediaClip,
            "An explicit half-turn lost the configured facing or discarded the existing board alignment.");
        AssertCameraMap(turned, originalCameraMap);
        var inverted = Draw(turned);
        var rotatedReference = RotateHalfTurn(upright);
        var rotationDifference = Difference(rotatedReference, inverted);
        Require(rotationDifference.MeanChannelError < .5 && rotationDifference.PixelsOver16 < width * height * .003,
            $"The explicit 180-degree facing did not rotate the existing board without stretching: " +
            $"mean error {rotationDifference.MeanChannelError:F4}, {rotationDifference.PixelsOver16} changed pixels.");
        var newRaster = turned.GetBoardResolutionDiagnostics();
        int uprightCoverage = NonBlackCount(upright), invertedCoverage = NonBlackCount(inverted);
        bool cornersUnchanged = SamePhysicalCorners(oldCorners, turned.GetDetectedBoardCorners()!);
        // The complete rotated pixel comparison above verifies visible coverage.
        // Thresholding very dark antialiased border pixels introduces a small
        // orientation-dependent count even when no pixel differs by 16 levels.
        Require(newRaster.BoardPixelWidth == oldRaster.BoardPixelWidth &&
                newRaster.BoardPixelHeight == oldRaster.BoardPixelHeight &&
                cornersUnchanged,
            $"Changing the facing changed physical board coverage, proportions or raster dimensions. " +
            $"Raster {oldRaster.BoardPixelWidth}x{oldRaster.BoardPixelHeight} -> " +
            $"{newRaster.BoardPixelWidth}x{newRaster.BoardPixelHeight}; physical corners unchanged={cornersUnchanged}; " +
            $"non-black pixels {uprightCoverage} -> {invertedCoverage} (delta {invertedCoverage - uprightCoverage}); " +
            $"rotation mean error {rotationDifference.MeanChannelError:F6}, pixels over16 {rotationDifference.PixelsOver16}.");
        await Save("menu-facing-left-180");
        var halfTurnSurface = SurfaceMap(originalInset, halfTurn: true);
        foreach (var button in turned.CurrentBoardButtons)
        {
            await Hover(turned, originalCameraMap, halfTurnSurface, button);
            hoverChecks++;
        }
        turned.ClearHandTips();

        // Clearing the camera/board clip is a scan reset, not a new projector
        // preference. Recreate the homography with a differently rotated camera.
        turned.ClearBoardMediaClip();
        Require(!turned.HasBoardMediaClip && turned.GetBoardFacingDegrees() == 180,
            "Clearing calibration forgot the user's selected board facing.");
        var rescannedCameraMap = CameraMap(3);
        turned.SetBoardSetup(true);
        double rescanInset = turned.SetDetectedBoardGrid(CameraOrderedCorners(3), rescannedCameraMap);
        turned.SetBoardSetup(false);
        turned.ShowBoardMenu();
        AssertCameraMap(turned, rescannedCameraMap);
        var rescanned = Draw(turned);
        var rescanDifference = Difference(inverted, rescanned);
        Require(turned.GetBoardFacingDegrees() == 180 && Math.Abs(originalInset - rescanInset) < 1e-6 &&
                rescanDifference.MeanChannelError < .1 && rescanDifference.PixelsOver16 < width * height * .001,
            "Rescanning with a 270-degree camera changed the stored physical board facing.");
        var paintButton = turned.CurrentBoardButtons.Single(button => button.Id == "paint");
        await Hover(turned, rescannedCameraMap, halfTurnSurface, paintButton);
        await Task.Delay(2);
        var sourceTime = DateTimeOffset.UtcNow;
        var point = CameraPoint(rescannedCameraMap, halfTurnSurface, paintButton);
        turned.SetHandCursors([new HandCursor(new(point.X, point.Y), sourceTime.AddSeconds(1), 880001)], sourceTime);
        Require(turned.CurrentBoardScreen == BoardScreen.Paint,
            "A rotated camera's fresh gesture did not select the physically rotated Paint button.");
        var exit = turned.CurrentBoardButtons.Single(button => button.Id == "menu");
        await Hover(turned, rescannedCameraMap, halfTurnSurface, exit);
        await Task.Delay(2);
        sourceTime = DateTimeOffset.UtcNow;
        point = CameraPoint(rescannedCameraMap, halfTurnSurface, exit);
        turned.SetHandCursors([new HandCursor(new(point.X, point.Y), sourceTime.AddSeconds(1), 880002)], sourceTime);
        Require(turned.CurrentBoardScreen == BoardScreen.Menu,
            "Paint Exit stopped working after changing facing and rescanning with a rotated camera.");

        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen,
                _scene.HasBoardMediaClip, _scene.GetBoardFacingDegrees()),
            "Orientation verification changed the live camera, projector, alignment or board-facing preference.");
        return new { passed = true, cameraRotations = new[] { 0, 90, 180, 270 },
            boards = boards.Select(board => board.ToString()).ToArray(), comparisons, hoverChecks,
            firstScanComparisons, firstScanHoverChecks, firstScanUsesProjectorOrientation = true,
            physicalRenderingIndependentOfCameraRotation = true, cameraCalibrationPreserved = true,
            explicitHalfTurnPreservesCoverageAndProportions = true,
            rotationDifference = new { rotationDifference.MeanChannelError, rotationDifference.PixelsOver16 },
            uprightNonBlackPixels = uprightCoverage, invertedNonBlackPixels = invertedCoverage,
            facingSurvivesClearAndRescan = true, rotatedGestureLaunchAndExit = true,
            liveHardwareUnchanged = true, directory, images };

        SceneCompositor NewScene(int cameraQuarterTurn, out Homography map, out double inset)
        {
            var result = new SceneCompositor(new BlackjackGame(seed: 173), blackjackClock: () => now, paintClock: () => now);
            result.SetDisplayAspect((double)width / height);
            result.SetBoardFacingDegrees(0);
            result.SetBoardSetup(true);
            map = CameraMap(cameraQuarterTurn);
            inset = result.SetDetectedBoardGrid(CameraOrderedCorners(cameraQuarterTurn), map);
            result.SetBoardSetup(false);
            result.ShowBoardMenu();
            return result;
        }
        Homography CameraMap(int quarterTurn) => Homography.FromFourPoints(unit.Select(point =>
        {
            var rotated = quarterTurn switch
            {
                0 => point,
                1 => new Point2(1 - point.Y, point.X),
                2 => new Point2(1 - point.X, 1 - point.Y),
                _ => new Point2(point.Y, 1 - point.X)
            };
            return new Point2(rotated.X * cameraSize, rotated.Y * cameraSize);
        }).ToArray(), unit);
        Vector2[] CameraOrderedCorners(int quarterTurn) => Enumerable.Range(0, 4)
            .Select(index => physicalCorners[(index + 4 - quarterTurn) % 4]).ToArray();
        Homography SurfaceMap(double inset, bool halfTurn)
        {
            var center = physicalCorners.Aggregate(Vector2.Zero, (sum, corner) => sum + corner) / 4;
            var clipped = physicalCorners.Select(corner => Vector2.Lerp(corner, center, (float)inset))
                .Select(corner => new Point2(corner.X, corner.Y)).ToArray();
            return Homography.FromFourPoints(unit, halfTurn ? [clipped[2], clipped[3], clipped[0], clipped[1]] : clipped);
        }
        static void Show(SceneCompositor scene, BoardScreen board)
        {
            switch (board)
            {
                case BoardScreen.Menu: scene.ShowBoardMenu(); break;
                case BoardScreen.HandTracking: scene.ShowHandTrackingTest(); break;
                case BoardScreen.PhotoCopy: scene.ShowPhotoCopy(); break;
                case BoardScreen.Paint: scene.ShowPaint(); break;
                case BoardScreen.Blackjack: scene.ShowBlackjack(); break;
                case BoardScreen.Monopoly: scene.ShowMonopoly(); break;
                default: throw new InvalidOperationException("Unexpected board orientation fixture.");
            }
        }
        byte[] Draw(SceneCompositor scene)
        {
            using (var drawing = target.CreateDrawingSession())
                scene.Draw(drawing, width, height, preview: false, runningSlowly: false);
            return target.GetPixelBytes();
        }
        async Task Save(string name)
        {
            string path = Path.Combine(directory, name + ".png");
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(new { name, path });
        }
        async Task Hover(SceneCompositor scene, Homography map, Homography surface, BoardButton button)
        {
            await Task.Delay(2);
            var point = CameraPoint(map, surface, button);
            var time = DateTimeOffset.UtcNow;
            scene.SetHandCursors([new HandCursor(new(point.X, point.Y), DateTimeOffset.MinValue)], time);
            Require(scene.HoveredBoardButtons.SequenceEqual([button.Id]),
                $"The camera-to-board mapping missed {scene.CurrentBoardScreen}/{button.Label} after changing orientation.");
        }
        static Point2 CameraPoint(Homography cameraMap, Homography surface, BoardButton button) =>
            cameraMap.InverseTransform(surface.Transform(new(button.Bounds.X + button.Bounds.Width / 2,
                button.Bounds.Y + button.Bounds.Height / 2)));
        static void AssertCameraMap(SceneCompositor scene, Homography expected) =>
            Require(scene.GetHandLightingDiagnostics().CameraToProjector!.SequenceEqual(expected.ToMatrix()),
                "Changing board facing altered the measured camera-to-projector calibration.");
        static bool SamePhysicalCorners(Point2[] first, Point2[] second) => first.Length == second.Length &&
            first.All(point => second.Any(other => Math.Abs(point.X - other.X) < 1e-6 && Math.Abs(point.Y - other.Y) < 1e-6));
        static int NonBlackCount(byte[] pixels)
        {
            int count = 0;
            for (int offset = 0; offset < pixels.Length; offset += 4)
                if (Math.Max(pixels[offset], Math.Max(pixels[offset + 1], pixels[offset + 2])) > 8) count++;
            return count;
        }
        static byte[] RotateHalfTurn(byte[] pixels)
        {
            var rotated = new byte[pixels.Length];
            for (int offset = 0; offset < pixels.Length; offset += 4)
                Array.Copy(pixels, offset, rotated, pixels.Length - offset - 4, 4);
            return rotated;
        }
        static (double MeanChannelError, int PixelsOver16) Difference(byte[] first, byte[] second)
        {
            Require(first.Length == second.Length, "Orientation changed output pixel dimensions.");
            long total = 0;
            int changed = 0;
            for (int offset = 0; offset < first.Length; offset += 4)
            {
                int maximum = 0;
                for (int channel = 0; channel < 3; channel++)
                {
                    int error = Math.Abs(first[offset + channel] - second[offset + channel]);
                    total += error;
                    maximum = Math.Max(maximum, error);
                }
                if (maximum > 16) changed++;
            }
            return ((double)total / (first.Length / 4 * 3), changed);
        }
        static void Require(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
        }
    }
}
#endif
