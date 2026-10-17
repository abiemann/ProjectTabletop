#if DEBUG
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Uses an injected clock and isolated GPU scene. Nothing is learned from,
    // drawn onto, or saved over the user's live board or marker profile.
    private async Task<object> VerifyPaintMarkerAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip);
        const int size = 800;
        var now = MonotonicClock.UtcNow.AddMinutes(1);
        string directory = Path.Combine(_appDataDirectory, "PaintMarkerVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<object>();
        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, size, size, 96);
        using var scene = new SceneCompositor(blackjackClock: () => now, paintClock: () => now);
        scene.SetDisplayAspect(1);
        scene.SetBoardFacingDegrees(0);
        Calibrate();
        scene.ShowPaint();
        Require(!scene.HandDetectionRequired && !HandFrameOwnsCaptions(BoardScreen.Paint) &&
                HandFrameOwnsCaptions(BoardScreen.PhotoCopy),
            "Paint still requires a hand model or routes its caption controls through hand inference.");
        scene.DisablePaintIdleStreaksForVerification();
        long revision = Reset();
        Draw();
        var blank = scene.CapturePaintArtworkForVerification();
        foreach (var point in new[] { new Point2(.15, .3), new Point2(.5, .5), new Point2(.8, .5) })
            Require(IsNeutralLight(blank, point), "Paint's unpainted surface is still dark or has a colour cast.");
        var blankOutput = target.GetPixelBytes();
        Require(blankOutput[0] < 3 && blankOutput[1] < 3 && blankOutput[2] < 3,
            "The brighter Paint surface leaked outside the calibrated board.");
        await Save("paint-marker-neutral-canvas");

        // Moving bars leave ink behind their path. A fresh placement seeds the
        // stroke, then fresh stationary observations pour at the current centre.
        Require(!Feed(new(.25, .35)) && !Feed(new(.27, .35)) && Count() == 0,
            "Placing the marker deposited ink before it had moved clear of its first position.");
        var deposited = new List<(Point2 Center, float Radius)>();
        foreach (double x in new[] { .31, .35, .39, .43 }) FeedAndDraw(new(x, .35));
        var straight = deposited.ToArray();
        Require(straight.Length >= 5 && straight.All(drop => Math.Abs(drop.Center.Y - .35) < .001 &&
                drop.Center.X >= .249 && drop.Center.X < .41) &&
                .43 - straight[^1].Center.X is > .025 and < .09,
            "The rightward ink trail did not remain a short distance behind the marker's travelled line.");
        long straightCount = Count();
        int movingDeposits = deposited.Count;
        for (int frame = 0; frame < 50; frame++) FeedAndDraw(new(.43, .35));
        var stationary = deposited.Skip(movingDeposits).ToArray();
        Require(Count() == straightCount + 5 && stationary.Length == 5 && Pending().Length == 0 &&
                stationary.All(drop => Math.Abs(drop.Center.X - .43) < .001 && Math.Abs(drop.Center.Y - .35) < .001),
            "A held marker did not pour five evenly paced drops per second at its current centre.");
        long paintedCount = Count();
        Require(!scene.SetPaintMarkerTip(Camera(new(.50, .35)), .025, now, revision) &&
                !scene.SetPaintMarkerTip(Camera(new(.50, .35)), .025, now.AddMilliseconds(-1), revision) &&
                !scene.SetPaintMarkerTip(Camera(new(.50, .35)), .025, now.AddSeconds(1), revision) &&
                Count() == paintedCount,
            "A duplicate, reordered or future camera result changed the marker trail.");
        Draw();
        Require(Pending().Length == 0 && Count() == paintedCount && scene.CanSavePaint,
            "Rendering the marker trail lost paint, deposited it twice, or left Save unavailable.");
        Require(scene.TryBeginPaintSave(out var painted), "The marker painting could not be captured for Save.");
        Require(ChangedPixels(blank, painted, new(.22, .30, .23, .10)) > 100 &&
                IsNeutralLight(painted, new(.8, .5)) && scene.CompletePaintSave(painted),
            "The saved marker painting has no visible ink or lost the neutral canvas outside the stroke.");
        await Save("paint-marker-straight-trail");

        // Reverse the horizontal travel, then take a right-angle turn. Deposits
        // must follow the old path through the corner, rather than swing around
        // an instantaneous velocity offset and cut across untouched canvas.
        revision = Reset();
        deposited.Clear();
        foreach (double x in new[] { .70, .66, .62, .58 }) FeedAndDraw(new(x, .55));
        var reverse = deposited.ToArray();
        Require(reverse.Length > 0 && reverse.All(drop => drop.Center.X > .60) &&
                reverse[^1].Center.X - .58 is > .025 and < .09,
            "Leftward movement did not move the trailing ink to the other side of the marker.");
        foreach (double y in new[] { .59, .63, .67 }) FeedAndDraw(new(.58, y));
        var corner = deposited.ToArray();
        Require(corner.Any(drop => drop.Center.Y > .58) &&
                corner.All(drop => Math.Abs(drop.Center.Y - .55) < .015 || Math.Abs(drop.Center.X - .58) < .015) &&
                .67 - corner[^1].Center.Y is > .025 and < .09,
            "The marker trail cut across the inside of a turn or ran ahead of the bars.");
        Draw();
        await Save("paint-marker-turn");

        revision = Reset();
        for (int step = 0; step <= 12; step++) Feed(new(.20 + step * .04, .40));
        Require(Pending().Length <= 2 && Count() is > 0 and <= 2,
            "A stalled renderer accumulated a long queue of expensive marker deposits.");
        Draw();
        long stalledCount = Count();
        Feed(new(.20 + 12 * .04, .40));
        Require(Pending().Length == 0 && Count() == stalledCount,
            "Recovering from a render stall replayed old marker motion while the bars were stationary.");

        revision = Reset();
        Feed(new(.5, .45));
        for (int frame = 0; frame < 50; frame++) Feed(new(.5, .45));
        Require(Pending().Length == 2 && Count() == 2,
            "Stationary pouring accumulated more than two drops while rendering was stalled.");
        Draw();
        for (int frame = 0; frame < 9; frame++) Feed(new(.5, .45));
        Require(Pending().Length == 0 && Count() == 2,
            "Recovering from a render stall replayed missed stationary pours.");
        Require(Feed(new(.5, .45)) && Pending().Length == 1 && Count() == 3,
            "Stationary pouring did not resume at its normal cadence after a render stall.");
        Draw();
        now += TimeSpan.FromMilliseconds(20);
        scene.SetPaintMarkerTip(null, .025, now, revision);
        now += TimeSpan.FromMilliseconds(600);
        Draw();
        Require(Count() == 3 && Pending().Length == 0 &&
                !scene.SetPaintMarkerTip(Camera(new(.5, .45)), .025, now.AddMilliseconds(-300), revision),
            "A lifted marker or stale camera observation continued pouring paint.");
        Require(!Feed(new(.5, .45)), "Reacquiring a held marker poured immediately without a fresh hold.");
        for (int frame = 0; frame < 9; frame++) Feed(new(.5, .45));
        Require(Count() == 3 && Pending().Length == 0,
            "A reacquired marker reused the previous hold's pour timer.");
        Require(Feed(new(.5, .45)) && Count() == 4,
            "A reacquired marker did not resume pouring after a fresh hold.");

        // A lost observation, a camera stall and a discontinuous detection all
        // start a new stroke. They must never connect disconnected positions.
        foreach (string interruption in new[] { "lift", "stale", "gap", "jump" })
        {
            revision = Reset();
            Feed(new(.30, .45));
            Feed(new(.32, .45));
            double restartX = interruption == "jump" ? .70 : .44;
            if (interruption == "lift")
            {
                now += TimeSpan.FromMilliseconds(20);
                scene.SetPaintMarkerTip(null, .025, now, revision);
            }
            else if (interruption == "stale")
            {
                now += TimeSpan.FromMilliseconds(600);
                Require(!scene.SetPaintMarkerTip(Camera(new(.40, .45)), .025, now.AddMilliseconds(-300), revision),
                    "A stale camera frame was accepted as current marker motion.");
            }
            else if (interruption == "gap") now += TimeSpan.FromMilliseconds(300);
            Require(!Feed(new(restartX, .45)) && Count() == 0,
                $"The first position after a marker {interruption} drew a connecting line.");
            Feed(new(restartX + .04, .45));
            Feed(new(restartX + .08, .45));
            Require(Pending().Length > 0 && Pending().All(drop => drop.Center.X >= restartX - .001),
                $"Motion after a marker {interruption} replayed the old stroke.");
        }

        revision = Reset();
        Require(scene.ActivatePaintButton("paint-drawer-open"), "The marker control-exclusion fixture could not open Paint's drawer.");
        now += BoardSession.PaintDrawerOpeningDuration;
        scene.TickPaint(now);
        foreach (var button in scene.CurrentBoardButtons)
        {
            var point = new Point2(button.Bounds.X + button.Bounds.Width / 2,
                button.Bounds.Y + button.Bounds.Height / 2);
            Feed(point);
            Feed(new(point.X + .01, point.Y));
        }
        Require(Count() == 0, "Moving the marker over Exit or Save painted beneath the controls.");
        Feed(new(.30, .45));
        Feed(new(.34, .45));
        Feed(new(.38, .45));
        long beforeInvalid = Count();
        now += TimeSpan.FromMilliseconds(20);
        Require(!scene.SetPaintMarkerTip(new(double.NaN, 0), .025, now, revision) &&
                !scene.SetPaintMarkerTip(Camera(new(.45, .45)), double.NaN, now, revision) && Count() == beforeInvalid,
            "A non-finite marker measurement deposited paint.");

        // The default canvas is marker-only before, during and after a marker
        // visit. Losing the bars must never restore whole-hand painting.
        revision = Reset();
        Draw();
        now += TimeSpan.FromSeconds(1);
        Draw();
        RequireMarkerOnlyInput();
        Feed(new(.4, .4));
        RequireMarkerOnlyInput();
        Require(Count() == 0, "Recognising the marker deposited an extra blob before its first pour.");
        now += TimeSpan.FromMilliseconds(20);
        scene.SetPaintMarkerTip(null, .025, now, revision);
        RequireMarkerOnlyInput();
        now += TimeSpan.FromMilliseconds(600);
        Draw();
        RequireMarkerOnlyInput();
        Require(Count() == 0, "Losing the marker resumed hand-disturbance painting.");

        foreach (string change in new[] { "reset", "navigation", "calibration", "black-output", "trainer" })
        {
            revision = Reset();
            Feed(new(.30, .45));
            Feed(new(.34, .45));
            long obsoleteRevision = revision;
            var obsoleteCameraPoint = Camera(new(.42, .45));
            switch (change)
            {
                case "reset": scene.ResetPaint(); break;
                case "navigation": scene.ShowBoardMenu(); scene.ShowPaint(); scene.ResetPaint(); break;
                case "calibration": Calibrate(); break;
                case "black-output":
                    scene.SetBlackOutput(true);
                    Require(scene.GetWandMarkerCameraPolygon() is null &&
                            !scene.TryMapWandMarkerCameraPoint(new(size / 2, size / 2), out _) &&
                            scene.GetPaintMarkerRadius(new(size / 2, size / 2), 20) == 0,
                        "Blanked projector output still offered an active Paint marker surface.");
                    // Do not query the marker revision while the surface is
                    // inactive: lifecycle hooks must invalidate it themselves.
                    scene.SetBlackOutput(false);
                    break;
                case "trainer":
                    scene.BeginMarkerSetup(Projection.MarkerSetupStage.NeutralAcquisition, "Fixture", 0, new(.4, .4, .2, .2));
                    scene.EndMarkerSetup();
                    break;
            }
            revision = scene.PaintMarkerInputRevision;
            long countBefore = Count();
            now += TimeSpan.FromMilliseconds(20);
            Require(revision != obsoleteRevision &&
                    !scene.SetPaintMarkerTip(obsoleteCameraPoint, .025, now, obsoleteRevision) && Count() == countBefore,
                $"A marker result captured before {change} painted into the changed scene.");
            revision = scene.PaintMarkerInputRevision;
            Require(!Feed(new(.45, .45)), $"The first fresh marker after {change} continued an obsolete stroke.");
        }

        int mappingChecks = 0;
        foreach (double displayAspect in new[] { 1.0, 16.0 / 9 })
        foreach (int facing in new[] { 0, 90, 180 })
        {
            scene.SetDisplayAspect(displayAspect);
            scene.SetBoardFacingDegrees(facing);
            var polygon = scene.GetWandMarkerCameraPolygon();
            Require(polygon is { Count: 4 } && polygon.Distinct().Count() == 4 &&
                    ReferenceEquals(polygon, scene.GetWandMarkerCameraPolygon()),
                "Paint's camera search polygon is invalid or reallocates without a registration change.");
            var corners = polygon ?? throw new InvalidOperationException("The marker polygon is unavailable.");
            var map = BoardToCamera();
            foreach (var point in new[] { new Point2(.1, .2), new Point2(.5, .5), new Point2(.85, .7) })
            {
                var camera = map.Transform(point);
                Require(scene.TryMapWandMarkerCameraPoint(new(camera.X, camera.Y), out var mapped) &&
                        Math.Abs(mapped.X - point.X) < .00001 && Math.Abs(mapped.Y - point.Y) < .00001,
                    $"Paint's marker mapping failed at facing {facing} and display aspect {displayAspect}.");
                double physicalHeight = Math.Sqrt(Math.Pow((corners[3].X - corners[0].X) * displayAspect, 2) +
                    Math.Pow(corners[3].Y - corners[0].Y, 2));
                double expectedRadius = 20 * Math.Max(displayAspect, 1) / physicalHeight;
                double radius = scene.GetPaintMarkerRadius(new(camera.X, camera.Y), 20);
                Require(radius > 0 && Math.Abs(radius - expectedRadius) < .00001,
                    "The marker footprint changed scale or axis after rotating the Paint board.");
                mappingChecks++;
            }
            Require(!scene.TryMapWandMarkerCameraPoint(new(-100, -100), out _),
                "Paint mapped a camera point outside the calibrated board into its canvas.");
        }

        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip),
            "The isolated marker verification changed the live camera, projector or board.");
        return new { passed = true, trailingInk = true, stationaryMarkerPoursAtCentre = true,
            stationaryPourCadenceBounded = true, duplicateAndStaleFramesDoNotPaint = true,
            liftStopsPouringAndReacquisitionWaits = true,
            turnsAndReversalsFollowTravelledPath = true, interruptionsDoNotBridge = true,
            stalledRendererHasBoundedDeposits = true,
            controlRegionsExcluded = true, markerOnlyWithoutHandInference = true,
            markerLossDoesNotEnableHandPainting = true,
            oldSessionResultsRejected = true, neutralCanvasAndSaveVerified = true, mappingChecks, images };

        void Calibrate()
        {
            scene.SetBoardSetup(true);
            scene.SetDetectedBoardGrid([new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                Homography.FromFourPoints([new(0, 0), new(size, 0), new(size, size), new(0, size)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            scene.SetBoardSetup(false);
        }
        void RequireMarkerOnlyInput()
        {
            var input = System.Text.Json.JsonSerializer.SerializeToElement(scene.GetPaintInputDiagnostics());
            Require(!scene.HandDetectionRequired && input.GetProperty("active").GetBoolean() &&
                    input.GetProperty("inputMode").GetString() == "double-bars" &&
                    !input.GetProperty("handPaintingEnabled").GetBoolean() &&
                    !input.GetProperty("handDetectionEnabled").GetBoolean(),
                "Paint enabled hand tracking or hand painting instead of its independent marker input.");
        }
        long Reset()
        {
            scene.ResetPaint();
            return scene.PaintMarkerInputRevision;
        }
        Homography BoardToCamera()
        {
            var polygon = scene.GetWandMarkerCameraPolygon() ??
                throw new InvalidOperationException("Paint has no calibrated marker search polygon.");
            return Homography.FromFourPoints([new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                polygon.Select(point => new Point2(point.X, point.Y)).ToArray());
        }
        Point2 Camera(Point2 point) => BoardToCamera().Transform(point);
        bool Feed(Point2 point)
        {
            now += TimeSpan.FromMilliseconds(20);
            return scene.SetPaintMarkerTip(Camera(point), .025, now, revision);
        }
        void FeedAndDraw(Point2 point)
        {
            Feed(point);
            var pending = Pending();
            Require(pending.Length <= 2, "A marker frame exceeded the bounded GPU deposit queue.");
            deposited.AddRange(pending);
            Draw();
        }
        long Count() => scene.GetPaintDiagnostics().DropCount;
        (Point2 Center, float Radius)[] Pending() => scene.GetPendingPaintMarkerDropsForVerification();
        void Draw()
        {
            using var drawing = target.CreateDrawingSession();
            scene.Draw(drawing, size, size, preview: false, runningSlowly: false);
        }
        async Task Save(string name)
        {
            string path = Path.Combine(directory, name + ".png");
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(new { name, path });
        }
        static bool IsNeutralLight(SceneCompositor.PaintMemoryImage image, Point2 point)
        {
            int offset = ((int)(point.Y * image.Height) * image.Width + (int)(point.X * image.Width)) * 4;
            int low = Math.Min(image.BgraPixels[offset], Math.Min(image.BgraPixels[offset + 1], image.BgraPixels[offset + 2]));
            int high = Math.Max(image.BgraPixels[offset], Math.Max(image.BgraPixels[offset + 1], image.BgraPixels[offset + 2]));
            return low >= 45 && high <= 180 && high - low <= 3;
        }
        static int ChangedPixels(SceneCompositor.PaintMemoryImage before, SceneCompositor.PaintMemoryImage after, BoardRect region)
        {
            Require(before.Width == after.Width && before.Height == after.Height,
                "Saving marker ink changed the artwork dimensions.");
            int changed = 0;
            for (int y = (int)(region.Y * before.Height); y < (region.Y + region.Height) * before.Height; y++)
            for (int x = (int)(region.X * before.Width); x < (region.X + region.Width) * before.Width; x++)
            {
                int offset = (y * before.Width + x) * 4;
                if (Enumerable.Range(0, 3).Any(channel => Math.Abs(before.BgraPixels[offset + channel] -
                        after.BgraPixels[offset + channel]) > 12)) changed++;
            }
            return changed;
        }
        static void Require(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
        }
    }
}
#endif
