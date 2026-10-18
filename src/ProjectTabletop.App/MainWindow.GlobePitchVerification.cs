#if DEBUG
using System.Numerics;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // These scenes, clocks and camera images are isolated. The fixture uses
    // actual projected pixels without opening a camera or changing the live board.
    private async Task<object> VerifyGlobePitchAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip);
        string directory = Path.Combine(_appDataDirectory, "GlobePitchVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var sessionField = typeof(SceneCompositor).GetField("_boardSession", fields)!;
        var surfaceMapField = typeof(SceneCompositor).GetField("_boardSurfaceMap", fields)!;
        var lightClipMethod = typeof(SceneCompositor).GetMethod("HoldButtonLightClip", fields)!;
        var results = new List<object>();
        var images = new List<string>();
        int assertions = 0, protectedSamples = 0;
        foreach (var test in new[]
        {
            (Name: "landscape", Width: 1600, Height: 1250, Facing: 0d, Skewed: false),
            (Name: "portrait", Width: 1250, Height: 1600, Facing: 0d, Skewed: false),
            (Name: "perspective-quarter-turn", Width: 1600, Height: 1250, Facing: 90d, Skewed: true)
        })
        {
            var now = MonotonicClock.UtcNow.AddMinutes(1);
            using var scene = new SceneCompositor(blackjackClock: () => now, globeClock: () => now,
                boardRevealClock: () => now);
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), test.Width, test.Height, 96);
            var session = (BoardSession)sessionField.GetValue(scene)!;
            var output = new Rect(0, 0, test.Width, test.Height);
            scene.SetDisplayAspect(test.Width / (double)test.Height);
            scene.SetBoardSetup(true);
            Vector2[] corners = test.Skewed
                ? [new(.08f, .06f), new(.94f, .11f), new(.87f, .94f), new(.12f, .88f)]
                : [new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)];
            scene.SetDetectedBoardGrid(corners,
                Homography.FromFourPoints([new(0, 0), new(test.Width, 0), new(test.Width, test.Height), new(0, test.Height)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            scene.SetBoardSetup(false);
            scene.SetBoardFacingDegrees(test.Facing);
            scene.ShowGlobe();
            await scene.EnsureGlobeResourcesAsync(target.Device);
            now += GlobeState.EntranceDuration + TimeSpan.FromMilliseconds(100);
            Draw();
            Check(scene.GetGlobePitchRailGeometry() is null && scene.GetGlobeZoomRailGeometry() is null,
                test.Name + ": the closed drawer exposed an input rail.");
            Check(scene.ActivateGlobeButton("globe-drawer-open"), "The fixture could not open Globe's drawer.");
            Draw();
            Check(scene.GetGlobePitchRailGeometry() is null, "The just-opened drawer accepted rail input.");
            now += TimeSpan.FromMilliseconds(150);
            Draw();
            Check(scene.GetGlobePitchRailGeometry() is null, "The moving drawer accepted rail input.");
            now += TimeSpan.FromMilliseconds(151);
            scene.TickGlobe(now);
            Draw();
            var rail = scene.GetGlobePitchRailGeometry() ?? throw new InvalidOperationException("No settled Globe pitch rail.");
            var zoomRail = scene.GetGlobeZoomRailGeometry() ?? throw new InvalidOperationException("No settled Globe zoom rail.");
            var readyAt = now;
            now += TimeSpan.FromMilliseconds(100);
            Check(scene.GetGlobePitchRailGeometry(now, readyAt) is null &&
                    ReferenceEquals(rail, scene.GetGlobePitchRailGeometry(now, now.AddMilliseconds(-80))),
                "Projection-delay gating accepted a frame from before the rail settled, or rejected a fresh frame.");
            Rect bounds = scene.GlobePitchRailBounds();
            Check(bounds.Bottom <= .80 + .000001 && bounds.Right < 1 && bounds.X > .85 &&
                    Math.Abs(bounds.Width * scene.GlobePreviewAspect / bounds.Height -
                        PatternRailGeometry.ReferenceHeight / (double)PatternRailGeometry.ReferenceWidth) < .000001,
                "The rail is stretched, outside the right edge, or overlaps the bottom swipe/credit area.");
            var surfaceMap = (Homography)surfaceMapField.GetValue(scene)!;
            Point2[] expected = [new(bounds.X, bounds.Bottom), new(bounds.X, bounds.Y),
                new(bounds.Right, bounds.Y), new(bounds.Right, bounds.Bottom)];
            for (int i = 0; i < expected.Length; i++)
            {
                var point = surfaceMap.Transform(expected[i]);
                Check(Math.Abs(point.X * test.Width - rail.CameraCorners[i].X) < .01 &&
                        Math.Abs(point.Y * test.Height - rail.CameraCorners[i].Y) < .01,
                    "Rail coordinates did not preserve bottom-to-top input through board orientation/perspective.");
            }
            var canonicalMap = Homography.FromFourPoints([new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                rail.CameraCorners.Select(point => new Point2(point.X, point.Y)).ToArray());
            var zoomMap = Homography.FromFourPoints([new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                zoomRail.CameraCorners.Select(point => new Point2(point.X, point.Y)).ToArray());
            byte[] clear = Draw();
            CheckDotCenters(clear);
            CheckDotCenters(clear, zoomMap);
            await Save("drawer-open");
            var detector = new PatternRailDetector();
            var zoomDetector = new PatternRailDetector();
            var initial = Observe(clear);
            var initialZoom = ObserveZoom(clear);
            Check(initial is { Reliable: true, Position: null, DamageCount: 0 },
                test.Name + ": the actual rendered dots did not register as intact: " + initial.Reason);
            Check(initialZoom is { Reliable: true, Position: null, DamageCount: 0 },
                test.Name + ": rendered zoom dots did not register as intact: " + initialZoom.Reason);

            now += TimeSpan.FromSeconds(20);
            byte[] movingSky = Draw();
            Check(ChangedPixels(clear, movingSky) > 1000, "The animated sky fixture did not change its background.");
            CheckRailUnchanged(clear, movingSky);
            CheckRailUnchanged(clear, movingSky, zoomMap);
            var skyObservation = Observe(movingSky);
            var zoomSkyObservation = ObserveZoom(movingSky);
            Check(skyObservation is { Reliable: true, Position: null, DamageCount: 0 } &&
                    ReferenceEquals(rail, scene.GetGlobePitchRailGeometry()),
                "Sky/Earth animation generated pitch input or reset optical registration.");
            Check(zoomSkyObservation is { Reliable: true, Position: null, DamageCount: 0 } &&
                    ReferenceEquals(zoomRail, scene.GetGlobeZoomRailGeometry()),
                "Sky/Earth animation generated zoom input or reset optical registration.");

            var contacts = new List<object>();
            foreach (var touch in new[] { (First: 0, Count: 3, Expected: -60d),
                (First: 23, Count: 3, Expected: 0d), (First: 46, Count: 3, Expected: 60d) })
            {
                now += TimeSpan.FromMilliseconds(20);
                var released = Observe(Draw());
                Check(released.Position is null, "The uncovered rail retained a contact.");
                Check(scene.SetGlobePitchRailPosition(null, now, rail.Revision), "Fresh release was rejected.");
                byte[] touched = Cover(touch.First, touch.Count);
                now += TimeSpan.FromMilliseconds(20);
                Check(Observe(touched).Position is null, "One damaged frame bypassed rail contact confirmation.");
                now += TimeSpan.FromMilliseconds(20);
                var observed = Observe(touched);
                Check(observed.Position is { } position && observed.Reliable && !observed.Ambiguous &&
                        Math.Abs(position - (touch.First + touch.Count / 2d) / PatternRailGeometry.Columns) < .025,
                    test.Name + ": rendered rail contact was lost or reversed: " + observed);
                Check(scene.SetGlobePitchRailPosition(observed.Position, now, rail.Revision), "Fresh pitch contact was rejected.");
                var untouchedZoom = ObserveZoom(touched);
                Check(untouchedZoom.Position is null && scene.SetGlobeZoomRailPosition(null, now, zoomRail.Revision) &&
                        scene.GlobeState.TargetZoom == GlobeState.DefaultZoom,
                    "Pitch contact moved zoom or consumed the other rail's same-frame timestamp.");
                var acceptedAt = now;
                Check(!scene.SetGlobePitchRailPosition(.5f, acceptedAt, rail.Revision) &&
                        !scene.SetGlobePitchRailPosition(.5f, acceptedAt.AddTicks(-1), rail.Revision) &&
                        !scene.SetGlobePitchRailPosition(.5f, now.AddMilliseconds(1), rail.Revision) &&
                        !scene.SetGlobePitchRailPosition(.5f, now.AddMilliseconds(-251), rail.Revision),
                    "Equal, reordered, future or stale camera results changed pitch.");
                now += TimeSpan.FromMilliseconds(150);
                double latitude = scene.GlobeState.ViewLatitudeDegrees;
                Check(Math.Abs(latitude - touch.Expected) < 2.5,
                    "Bottom/middle/top contact did not reach the corresponding latitude: " + latitude);
                var lifted = Observe(Draw());
                Check(lifted.Position is null && scene.SetGlobePitchRailPosition(null, now, rail.Revision),
                    "Lifting off the dots was not accepted as clear.");
                if (test.Name == "landscape") await Save("pitch-" + touch.Expected);
                now += TimeSpan.FromMilliseconds(200);
                Check(Math.Abs(scene.GlobeState.ViewLatitudeDegrees - latitude) < .000001 &&
                        ReferenceEquals(rail, scene.GetGlobePitchRailGeometry()),
                    "Lifting reset the selected pitch or optical rail geometry.");
                contacts.Add(new { observed.Position, latitude, observed.Reason });
            }

            var zoomContacts = new List<object>();
            double retainedPitch = scene.GlobeState.ViewLatitudeDegrees;
            foreach (var touch in new[] { (First: 0, Count: 3, Expected: GlobeState.MinimumZoom),
                (First: 23, Count: 3, Expected: (GlobeState.MinimumZoom + GlobeState.MaximumZoom) / 2),
                (First: 46, Count: 3, Expected: GlobeState.MaximumZoom) })
            {
                now += TimeSpan.FromMilliseconds(20);
                Check(ObserveZoom(Draw()).Position is null, "The uncovered zoom rail retained a contact.");
                byte[] touched = Cover(touch.First, touch.Count, zoomMap);
                now += TimeSpan.FromMilliseconds(20);
                Check(ObserveZoom(touched).Position is null, "Zoom accepted an unconfirmed one-frame contact.");
                now += TimeSpan.FromMilliseconds(20);
                var observed = ObserveZoom(touched);
                Check(observed is { Position: not null, Reliable: true, Ambiguous: false } &&
                        scene.SetGlobeZoomRailPosition(observed.Position, now, zoomRail.Revision),
                    test.Name + ": a real zoom-dot obstruction was rejected: " + observed.Reason);
                var untouchedPitch = Observe(touched);
                Check(untouchedPitch.Position is null && scene.SetGlobePitchRailPosition(null, now, rail.Revision),
                    "Zoom contact corrupted pitch or consumed the pitch rail's same-frame timestamp.");
                Check(!scene.SetGlobeZoomRailPosition(.5f, now, zoomRail.Revision) &&
                        !scene.SetGlobeZoomRailPosition(.5f, now.AddTicks(-1), zoomRail.Revision) &&
                        !scene.SetGlobeZoomRailPosition(.5f, now.AddMilliseconds(1), zoomRail.Revision) &&
                        !scene.SetGlobeZoomRailPosition(.5f, now.AddMilliseconds(-251), zoomRail.Revision),
                    "Equal, reordered, future or stale observations changed zoom.");
                now += TimeSpan.FromMilliseconds(150);
                Check(Math.Abs(scene.GlobeState.Zoom - touch.Expected) < .05 &&
                        Math.Abs(scene.GlobeState.ViewLatitudeDegrees - retainedPitch) < .000001,
                    "Absolute zoom missed its limit/midpoint or disturbed the retained pitch.");
                double retainedZoom = scene.GlobeState.Zoom;
                Check(ObserveZoom(Draw()).Position is null && scene.SetGlobeZoomRailPosition(null, now, zoomRail.Revision),
                    "A lifted zoom contact was rejected.");
                now += TimeSpan.FromMilliseconds(150);
                Check(Math.Abs(scene.GlobeState.Zoom - retainedZoom) < .000001 &&
                        ReferenceEquals(zoomRail, scene.GetGlobeZoomRailGeometry()),
                    "Release reset absolute zoom or its optical registration.");
                zoomContacts.Add(new { observed.Position, zoom = scene.GlobeState.Zoom, observed.Reason });
            }
            now += TimeSpan.FromMilliseconds(20);
            byte[] bothTouched = Cover(23, 3);
            bothTouched = Cover(23, 3, zoomMap, redraw: false);
            now += TimeSpan.FromMilliseconds(20);
            Observe(bothTouched); ObserveZoom(bothTouched);
            now += TimeSpan.FromMilliseconds(20);
            var bothPitch = Observe(bothTouched);
            var bothZoom = ObserveZoom(bothTouched);
            Check(bothPitch.Position is not null && bothZoom.Position is not null &&
                    scene.SetGlobePitchRailPosition(bothPitch.Position, now, rail.Revision) &&
                    scene.SetGlobeZoomRailPosition(bothZoom.Position, now, zoomRail.Revision),
                "Simultaneous confirmed rail contacts were not independently accepted from the same camera frame.");
            now += TimeSpan.FromMilliseconds(150);
            Check(Math.Abs(scene.GlobeState.ViewLatitudeDegrees) < 2.5 &&
                    Math.Abs(scene.GlobeState.Zoom - (GlobeState.MinimumZoom + GlobeState.MaximumZoom) / 2) < .05,
                "Simultaneous contacts did not settle at independent pitch/zoom targets.");

            byte[] beforeLight = Draw();
            using (var drawing = target.CreateDrawingSession())
            using (var clip = (CanvasGeometry?)lightClipMethod.Invoke(scene, [drawing, output]))
            {
                Check(clip is not null, "The Globe rail has no shared spotlight protection.");
                using (drawing.CreateLayer(1, clip!)) drawing.FillRectangle(output, Colors.White);
            }
            byte[] lit = target.GetPixelBytes();
            CheckRailUnchanged(beforeLight, lit);
            CheckRailUnchanged(beforeLight, lit, zoomMap);
            var litObservation = Observe(lit);
            Check(litObservation.Position is null && litObservation.Reliable,
                "A shared search/hand spotlight corrupted the pitch rail.");
            Check(ObserveZoom(lit) is { Position: null, Reliable: true },
                "A shared search/hand spotlight corrupted the zoom rail.");
            await Save("spotlight-protected");
            Draw();

            if (test.Name == "landscape")
            {
                Check(scene.ActivateGlobeButton("globe-model"), "MODEL could not reveal its choices.");
                now += TimeSpan.FromMilliseconds(1);
                Check(scene.ActivateGlobeButton("globe-model-night"), "NIGHT could not be selected for the hold fixture.");
                await scene.EnsureGlobeResourcesAsync(target.Device);
                now += TimeSpan.FromMilliseconds(500);
                Draw();
                rail = scene.GetGlobePitchRailGeometry() ?? throw new InvalidOperationException("MODEL expansion lost pitch geometry.");
                zoomRail = scene.GetGlobeZoomRailGeometry() ?? throw new InvalidOperationException("MODEL expansion lost zoom geometry.");
                now += TimeSpan.FromMilliseconds(100);
                var hold = scene.GetHoldButtonContext(now) ?? throw new InvalidOperationException("No Globe model caption context.");
                Check(hold.EnabledHoldIds.Contains("globe-model-night") && hold.EnabledHoldIds.Contains("globe-exit"),
                    "The rails removed the model or EXIT hold buttons.");
                scene.ObserveHoldButtons(hold, [], now, ["globe-model-night"]);
                int modelActivations = 0;
                for (int frame = 0; frame <= 10; frame++)
                {
                    now += TimeSpan.FromMilliseconds(100);
                    if (frame == 4)
                        Check(scene.SetGlobePitchRailPosition(.4f, now, rail.Revision) &&
                                scene.SetGlobeZoomRailPosition(.4f, now, zoomRail.Revision),
                            "Same-frame pitch/zoom input failed during a model caption hold.");
                    Draw();
                    Check(scene.GetHoldButtonContext(now)?.Revision == hold.Revision,
                        "Changing pitch/zoom reset stationary caption references during a model hold.");
                    modelActivations += scene.ObserveHoldButtons(hold, ["globe-model-night"], now, []).Count;
                }
                Check(modelActivations == 1 && scene.GlobeState.Model == GlobeModel.Night,
                    $"Rail input interrupted the normal model hold: activations={modelActivations}.");
                // The action rejects camera frames at/before its timestamp,
                // but neither stationary rail moved or changed appearance.
                Check(ReferenceEquals(rail, scene.GetGlobePitchRailGeometry()) &&
                        ReferenceEquals(zoomRail, scene.GetGlobeZoomRailGeometry()),
                    "A model choice reset stationary optical rail registration.");
                Check(scene.GetGlobePitchRailGeometry(now) is null && scene.GetGlobeZoomRailGeometry(now) is null,
                    "A camera frame at the model action's timestamp bypassed its fresh-input barrier.");
                now += TimeSpan.FromMilliseconds(1);
                Check(ReferenceEquals(rail, scene.GetGlobePitchRailGeometry(now)) &&
                        ReferenceEquals(zoomRail, scene.GetGlobeZoomRailGeometry(now)),
                    "A fresh post-model frame lost unchanged rail registration.");

                Check(session.SetButtonsVisible(false, now), "Could not hide Globe controls for the input-barrier test.");
                Check(scene.GetGlobePitchRailGeometry() is null && scene.GetGlobeZoomRailGeometry() is null &&
                        !scene.SetGlobePitchRailPosition(0, now.AddTicks(-1), rail.Revision) &&
                        !scene.SetGlobeZoomRailPosition(0, now.AddTicks(-1), zoomRail.Revision),
                    "A hiding rail accepted an in-flight contact.");
                now += TimeSpan.FromMilliseconds(301);
                Draw();
                Check(scene.GetGlobePitchRailGeometry() is null && scene.GetGlobeZoomRailGeometry() is null,
                    "A hidden rail still exposed input geometry.");
                Check(session.SetButtonsVisible(true, now), "Could not restore Globe controls.");
                now += TimeSpan.FromMilliseconds(150);
                Draw();
                Check(scene.GetGlobePitchRailGeometry() is null && scene.GetGlobeZoomRailGeometry() is null,
                    "Fading-in dots accepted contact before settling.");
                now += TimeSpan.FromMilliseconds(151);
                Draw();
                var restored = scene.GetGlobePitchRailGeometry() ?? throw new InvalidOperationException("Restored dots lacked geometry.");
                var restoredZoom = scene.GetGlobeZoomRailGeometry() ?? throw new InvalidOperationException("Restored zoom dots lacked geometry.");
                Check(restored.Revision != rail.Revision &&
                        restoredZoom.Revision != zoomRail.Revision &&
                        !scene.SetGlobePitchRailPosition(0, now, rail.Revision) &&
                        !scene.SetGlobeZoomRailPosition(0, now, zoomRail.Revision),
                    "Restored dots reused or accepted the hidden rail epoch.");
                now += TimeSpan.FromMilliseconds(100);
                Check(scene.SetGlobePitchRailPosition(0, now, restored.Revision) &&
                        scene.SetGlobeZoomRailPosition(0, now, restoredZoom.Revision), "Fresh restored dots did not accept input.");
                Check(scene.ActivateGlobeButton("globe-drawer-close") && scene.GetGlobePitchRailGeometry() is null &&
                        scene.GetGlobeZoomRailGeometry() is null && !scene.SetGlobePitchRailPosition(1, now, restored.Revision) &&
                        !scene.SetGlobeZoomRailPosition(1, now, restoredZoom.Revision),
                    "Closing the drawer left a rail active.");
                now += TimeSpan.FromMilliseconds(20);
                Check(scene.ActivateGlobeButton("globe-drawer-open"), "Could not reopen Globe's drawer.");
                now += TimeSpan.FromMilliseconds(301);
                scene.TickGlobe(now);
                Draw();
                var reopened = scene.GetGlobePitchRailGeometry() ?? throw new InvalidOperationException("Reopened drawer lacked a rail.");
                Check(reopened.Revision != restored.Revision &&
                        !scene.SetGlobePitchRailPosition(1, now, restored.Revision),
                    "Reopened drawer accepted the previous visit's rail contact.");
                scene.ClearGlobePitchInput();
                var reset = scene.GetGlobePitchRailGeometry() ?? throw new InvalidOperationException("Reset rail lacked geometry.");
                Check(reset.Revision != reopened.Revision &&
                        !scene.SetGlobePitchRailPosition(1, now, reopened.Revision),
                    "Camera/input reset retained the old rail epoch.");
                scene.SetBoardFacingDegrees(90);
                Draw();
                // Changing board facing reapplies calibration and resets input,
                // including its open drawer. Reopen it in the new orientation.
                Check(!scene.GlobeDrawerOpen && scene.GetGlobePitchRailGeometry() is null &&
                        !scene.SetGlobePitchRailPosition(1, now, reset.Revision),
                    "Board rotation retained the old drawer or camera-space rail contact.");
                now += TimeSpan.FromMilliseconds(1);
                Check(scene.ActivateGlobeButton("globe-drawer-open"), "Could not reopen the rotated Globe drawer.");
                now += TimeSpan.FromMilliseconds(301);
                scene.TickGlobe(now);
                Draw();
                var turned = scene.GetGlobePitchRailGeometry() ?? throw new InvalidOperationException("Turned board lacked rail geometry.");
                Check(turned.Revision != reset.Revision && !turned.CameraCorners.SequenceEqual(reset.CameraCorners) &&
                        !scene.SetGlobePitchRailPosition(1, now, reset.Revision),
                    "Board rotation accepted an old camera-space rail contact.");
                scene.ShowBoardMenu();
                Check(scene.GetGlobePitchRailGeometry() is null &&
                        !scene.SetGlobePitchRailPosition(1, now, turned.Revision),
                    "Leaving Globe retained active pitch input.");
            }
            results.Add(new { test.Name, test.Width, test.Height, test.Facing, test.Skewed,
                railBounds = bounds, contacts, zoomContacts });

            byte[] Draw()
            {
                using (var drawing = target.CreateDrawingSession())
                    scene.Draw(drawing, test.Width, test.Height, preview: false, runningSlowly: false);
                return target.GetPixelBytes();
            }
            PatternRailObservation Observe(byte[] pixels) =>
                detector.Observe(pixels, test.Width, test.Height, test.Width * 4, rail, now);
            PatternRailObservation ObserveZoom(byte[] pixels) =>
                zoomDetector.Observe(pixels, test.Width, test.Height, test.Width * 4, zoomRail, now);
            byte[] Cover(int first, int count, Homography? map = null, bool redraw = true)
            {
                if (redraw) Draw();
                double left = first / (double)PatternRailGeometry.Columns;
                double right = (first + count) / (double)PatternRailGeometry.Columns;
                Vector2[] polygon = new[] { new Point2(left, 0), new Point2(right, 0), new Point2(right, 1), new Point2(left, 1) }
                    .Select(point => (map ?? canonicalMap).Transform(point)).Select(point => new Vector2((float)point.X, (float)point.Y)).ToArray();
                using (var drawing = target.CreateDrawingSession())
                using (var shape = CanvasGeometry.CreatePolygon(target.Device, polygon))
                    drawing.FillGeometry(shape, Windows.UI.Color.FromArgb(255, 176, 143, 119));
                return target.GetPixelBytes();
            }
            void CheckDotCenters(byte[] pixels, Homography? map = null)
            {
                for (int column = 0; column < PatternRailGeometry.Columns; column++)
                foreach (int y in new[] { PatternRailGeometry.DotY1ForColumn(column), PatternRailGeometry.DotY2ForColumn(column) })
                {
                    int index = Pixel(PatternRailGeometry.ColumnCenter(column) / (double)PatternRailGeometry.ReferenceWidth,
                        y / (double)PatternRailGeometry.ReferenceHeight, map);
                    Check(pixels[index] > 190 && pixels[index + 1] > 190 && pixels[index + 2] > 190,
                        test.Name + ": a drawn dot disagrees with its canonical detector coordinates.");
                }
            }
            void CheckRailUnchanged(byte[] before, byte[] after, Homography? map = null)
            {
                for (int column = 0; column < PatternRailGeometry.Columns; column++)
                foreach (double across in new[] { .12, .34, .5, .66, .88 })
                {
                    int index = Pixel((column + .5) / PatternRailGeometry.Columns, across, map);
                    Check(before.AsSpan(index, 4).SequenceEqual(after.AsSpan(index, 4)),
                        test.Name + ": animation or light modified the protected stationary dots/backing.");
                    protectedSamples++;
                }
            }
            int Pixel(double u, double v, Homography? map = null)
            {
                var point = (map ?? canonicalMap).Transform(new(u, v));
                int x = Math.Clamp((int)point.X, 0, test.Width - 1), y = Math.Clamp((int)point.Y, 0, test.Height - 1);
                return (y * test.Width + x) * 4;
            }
            async Task Save(string suffix)
            {
                string path = Path.Combine(directory, test.Name + "-" + suffix + ".png");
                await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
                images.Add(path);
            }
        }
        Check(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip),
            "Pitch verification changed the live camera, projector or board.");
        return new { passed = true, directory, images, assertions, protectedSamples, results,
            actualRenderedRailDetection = true, bottomToTopPitch = true, releaseKeepsPitch = true,
            staleAndHiddenInputRejected = true, drawerSwipeResetAndRotationEpochs = true,
            absoluteZoomAndPitchIndependent = true, sameFrameRailInputs = true,
            ordinaryModelHoldPreserved = true, protectedFromSkyAndSpotlights = true, liveHardwareUnchanged = true };

        void Check(bool valid, string message)
        {
            assertions++;
            if (!valid) throw new InvalidOperationException(message);
        }
        static int ChangedPixels(byte[] first, byte[] second)
        {
            int count = 0;
            for (int i = 0; i < first.Length; i += 4)
                if (Math.Max(Math.Abs(first[i] - second[i]), Math.Max(Math.Abs(first[i + 1] - second[i + 1]),
                    Math.Abs(first[i + 2] - second[i + 2]))) > 1) count++;
            return count;
        }
    }
}
#endif
