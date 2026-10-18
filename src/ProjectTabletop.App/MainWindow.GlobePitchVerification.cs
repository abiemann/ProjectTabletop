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
            Check(scene.GetGlobePitchRailGeometry() is null, test.Name + ": the closed drawer exposed a pitch rail.");
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
            byte[] clear = Draw();
            CheckDotCenters(clear);
            await Save("drawer-open");
            var detector = new PatternRailDetector();
            var initial = Observe(clear);
            Check(initial is { Reliable: true, Position: null, DamageCount: 0 },
                test.Name + ": the actual rendered dots did not register as intact: " + initial.Reason);

            now += TimeSpan.FromSeconds(20);
            byte[] movingSky = Draw();
            Check(ChangedPixels(clear, movingSky) > 1000, "The animated sky fixture did not change its background.");
            CheckRailUnchanged(clear, movingSky);
            var skyObservation = Observe(movingSky);
            Check(skyObservation is { Reliable: true, Position: null, DamageCount: 0 } &&
                    ReferenceEquals(rail, scene.GetGlobePitchRailGeometry()),
                "Sky/Earth animation generated pitch input or reset optical registration.");

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

            byte[] beforeLight = Draw();
            using (var drawing = target.CreateDrawingSession())
            using (var clip = (CanvasGeometry?)lightClipMethod.Invoke(scene, [drawing, output]))
            {
                Check(clip is not null, "The Globe rail has no shared spotlight protection.");
                using (drawing.CreateLayer(1, clip!)) drawing.FillRectangle(output, Colors.White);
            }
            byte[] lit = target.GetPixelBytes();
            CheckRailUnchanged(beforeLight, lit);
            var litObservation = Observe(lit);
            Check(litObservation.Position is null && litObservation.Reliable,
                "A shared search/hand spotlight corrupted the pitch rail.");
            await Save("spotlight-protected");
            Draw();

            if (test.Name == "landscape")
            {
                Check(scene.ActivateGlobeButton("globe-zoom-in"), "The unchanged Zoom + action failed.");
                now += TimeSpan.FromMilliseconds(500);
                Draw();
                var hold = scene.GetHoldButtonContext(now) ?? throw new InvalidOperationException("No Globe zoom caption context.");
                Check(hold.EnabledHoldIds.Contains("globe-zoom-in") && hold.EnabledHoldIds.Contains("globe-zoom-out"),
                    "The pitch rail removed the existing zoom hold buttons.");
                double zoom = scene.GlobeState.TargetZoom;
                scene.ObserveHoldButtons(hold, [], now, ["globe-zoom-in"]);
                int zoomActivations = 0;
                for (int frame = 0; frame <= 10; frame++)
                {
                    now += TimeSpan.FromMilliseconds(100);
                    if (frame == 4)
                        Check(scene.SetGlobePitchRailPosition(.4f, now, rail.Revision), "Pitch input failed during a zoom hold.");
                    Draw();
                    Check(scene.GetHoldButtonContext(now)?.Revision == hold.Revision,
                        "Changing pitch reset stationary caption references during a zoom hold.");
                    zoomActivations += scene.ObserveHoldButtons(hold, ["globe-zoom-in"], now, []).Count;
                }
                Check(zoomActivations == 1 && scene.GlobeState.TargetZoom > zoom,
                    $"Pitch input interrupted the normal zoom hold: activations={zoomActivations}, " +
                    $"zoomBefore={zoom}, zoomAfter={scene.GlobeState.TargetZoom}.");
                // The action rejects camera frames at/before its timestamp,
                // but the rail itself has not moved or changed appearance.
                var afterZoomRail = scene.GetGlobePitchRailGeometry();
                Check(ReferenceEquals(rail, afterZoomRail),
                    $"Zoom reset the stationary rail: originalEpoch={rail.Revision}, " +
                    $"afterZoomEpoch={afterZoomRail?.Revision}, time={now:O}.");
                Check(scene.GetGlobePitchRailGeometry(now) is null,
                    "A camera frame at the zoom action's timestamp bypassed its fresh-input barrier.");
                now += TimeSpan.FromMilliseconds(1);
                Check(ReferenceEquals(rail, scene.GetGlobePitchRailGeometry(now)),
                    "A fresh post-zoom frame lost the unchanged rail's optical registration.");

                Check(session.SetButtonsVisible(false, now), "Could not hide Globe controls for the input-barrier test.");
                Check(scene.GetGlobePitchRailGeometry() is null &&
                        !scene.SetGlobePitchRailPosition(0, now.AddTicks(-1), rail.Revision),
                    "A hiding rail accepted an in-flight contact.");
                now += TimeSpan.FromMilliseconds(301);
                Draw();
                Check(scene.GetGlobePitchRailGeometry() is null, "A hidden rail still exposed input geometry.");
                Check(session.SetButtonsVisible(true, now), "Could not restore Globe controls.");
                now += TimeSpan.FromMilliseconds(150);
                Draw();
                Check(scene.GetGlobePitchRailGeometry() is null, "Fading-in dots accepted contact before settling.");
                now += TimeSpan.FromMilliseconds(151);
                Draw();
                var restored = scene.GetGlobePitchRailGeometry() ?? throw new InvalidOperationException("Restored dots lacked geometry.");
                Check(restored.Revision != rail.Revision &&
                        !scene.SetGlobePitchRailPosition(0, now, rail.Revision),
                    "Restored dots reused or accepted the hidden rail epoch.");
                now += TimeSpan.FromMilliseconds(100);
                Check(scene.SetGlobePitchRailPosition(0, now, restored.Revision), "Fresh restored dots did not accept input.");
                Check(scene.ActivateGlobeButton("globe-drawer-close") && scene.GetGlobePitchRailGeometry() is null &&
                        !scene.SetGlobePitchRailPosition(1, now, restored.Revision),
                    "Closing the drawer left pitch active.");
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
                railBounds = bounds, contacts });

            byte[] Draw()
            {
                using (var drawing = target.CreateDrawingSession())
                    scene.Draw(drawing, test.Width, test.Height, preview: false, runningSlowly: false);
                return target.GetPixelBytes();
            }
            PatternRailObservation Observe(byte[] pixels) =>
                detector.Observe(pixels, test.Width, test.Height, test.Width * 4, rail, now);
            byte[] Cover(int first, int count)
            {
                Draw();
                double left = first / (double)PatternRailGeometry.Columns;
                double right = (first + count) / (double)PatternRailGeometry.Columns;
                Vector2[] polygon = new[] { new Point2(left, 0), new Point2(right, 0), new Point2(right, 1), new Point2(left, 1) }
                    .Select(point => canonicalMap.Transform(point)).Select(point => new Vector2((float)point.X, (float)point.Y)).ToArray();
                using (var drawing = target.CreateDrawingSession())
                using (var shape = CanvasGeometry.CreatePolygon(target.Device, polygon))
                    drawing.FillGeometry(shape, Windows.UI.Color.FromArgb(255, 176, 143, 119));
                return target.GetPixelBytes();
            }
            void CheckDotCenters(byte[] pixels)
            {
                for (int column = 0; column < PatternRailGeometry.Columns; column++)
                foreach (int y in new[] { PatternRailGeometry.DotY1ForColumn(column), PatternRailGeometry.DotY2ForColumn(column) })
                {
                    int index = Pixel(PatternRailGeometry.ColumnCenter(column) / (double)PatternRailGeometry.ReferenceWidth,
                        y / (double)PatternRailGeometry.ReferenceHeight);
                    Check(pixels[index] > 190 && pixels[index + 1] > 190 && pixels[index + 2] > 190,
                        test.Name + ": a drawn dot disagrees with its canonical detector coordinates.");
                }
            }
            void CheckRailUnchanged(byte[] before, byte[] after)
            {
                for (int column = 0; column < PatternRailGeometry.Columns; column++)
                foreach (double across in new[] { .12, .34, .5, .66, .88 })
                {
                    int index = Pixel((column + .5) / PatternRailGeometry.Columns, across);
                    Check(before.AsSpan(index, 4).SequenceEqual(after.AsSpan(index, 4)),
                        test.Name + ": animation or light modified the protected stationary dots/backing.");
                    protectedSamples++;
                }
            }
            int Pixel(double u, double v)
            {
                var point = canonicalMap.Transform(new(u, v));
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
            ordinaryZoomHoldPreserved = true, protectedFromSkyAndSpotlights = true, liveHardwareUnchanged = true };

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
