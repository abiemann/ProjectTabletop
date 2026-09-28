#if DEBUG
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Camera;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Use the real button renderer and foreground comparison, independently of
    // the live camera and projector. A stationary occlusion must illuminate the
    // controls without manufacturing a hand or executing a Photo Copy action.
    private async Task<object> VerifyPhotoCopyAcquisitionAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip);
        const int size = 1000;
        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        using var scene = new SceneCompositor(blackjackClock: () => now);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 96);
        scene.SetDisplayAspect(1);
        scene.SetBoardSetup(true);
        double inset = scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f),
                new(.965f, .965f), new(.035f, .965f)],
            Homography.FromFourPoints([new(0, 0), new(size, 0), new(size, size), new(0, size)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        scene.ShowPhotoCopy();
        Draw();
        Require(scene.GetHandAcquisitionContext(now) is { ObserveMotion: false },
            "Photo Copy acquisition did not settle after navigation.");
        now += TimeSpan.FromMilliseconds(600);
        var before = Ready();
        Require(before.ExpectedScene is { Width: size, Height: size } &&
                before.ExpectedScene.Bgra.Length == size * size * 4 &&
                before.ExpectedScene.BoardSearchRegions is { Count: 3 } &&
                before.RestrictAcquisitionToSearchRegions && before.AllowsSearchIllumination &&
                before.ContinuousSearchPolygon is { Length: 4 },
            "Photo Copy needs its own bounded button reference, three search masks and a focused object field.");
        AssertSearchCenters(before);
        AssertEmpty(before, Draw(), "The empty Photo Copy controls produced foreground candidates.");

        await Task.Delay(1100);
        Require(scene.TryGetPhotoCopyCaptureContext(out var capture), "The object fixture did not settle.");
        scene.SetPhotoCopyStatus("Different object status and a long changing hint.", capture.Revision);
        var statusFrame = Draw();
        AssertStableReference(before, Ready(), "Status text reset the control reference.");
        AssertEmpty(before, statusFrame, "A changing status bar became a hand candidate.");

        // Light a real segmented object in the capture field. This light and the
        // title/status are outside the control comparison, even as they redraw.
        var objectFrame = new byte[size * size * 4];
        Fill(objectFrame, 0, 0, size, size, 110, 110, 110);
        var topLeft = CameraPoint(.30, .35);
        var bottomRight = CameraPoint(.49, .52);
        Fill(objectFrame, (int)topLeft.X, (int)topLeft.Y, (int)(bottomRight.X - topLeft.X),
            (int)(bottomRight.Y - topLeft.Y), 20, 35, 185);
        var objectTarget = PhotoObjectLocator.Locate(size, size, size * 4, objectFrame,
            capture.CameraToBoard, out string? objectFailure);
        Require(objectTarget is not null && scene.SetPhotoCopyObject(objectTarget, capture.Revision),
            "The object-light fixture failed: " + objectFailure);
        var objectLightFrame = Draw();
        AssertStableReference(before, Ready(), "The object spotlight reset the control reference.");
        AssertEmpty(before, objectLightFrame, "The object spotlight became a hand candidate.");

        const int photoWidth = 30, photoHeight = 50;
        var photoPixels = new byte[photoWidth * photoHeight * 4];
        for (int offset = 0; offset < photoPixels.Length; offset += 4)
        {
            photoPixels[offset] = 35; photoPixels[offset + 1] = 70;
            photoPixels[offset + 2] = 210; photoPixels[offset + 3] = 255;
        }
        var photograph = new PhotoHandCutout(photoWidth, photoHeight, photoPixels,
            new(photoWidth / 2d, photoHeight / 2d), new(0, -1));
        Require(scene.SetPhotoCopyCapture(photograph, capture.Revision, objectTarget),
            "The Swirl fixture could not retain its photograph.");
        Draw();
        var changing = scene.GetHandAcquisitionContext(now)!;
        Require(changing.Revision != before.Revision && !changing.ObserveMotion,
            "Changing Swirl/Copy to Clear/Save did not invalidate the previous control image.");
        now += TimeSpan.FromMilliseconds(600);
        var ready = Ready();
        Require(scene.CurrentBoardButtons.Select(button => button.Label).SequenceEqual(["Exit", "Clear", "Save"]),
            "The retained photograph did not expose the requested result controls.");
        Require(ready.ContinuousSearchPolygon is null && ready.RestrictAcquisitionToSearchRegions,
            "Photo Copy continued scanning the object field after Swirl changed the controls to Clear/Save.");
        Require(!ReferenceEquals(before.ExpectedScene, ready.ExpectedScene) &&
                !before.ExpectedScene!.Bgra.SequenceEqual(ready.ExpectedScene!.Bgra),
            "Clear/Save reused the old Swirl/Copy text template.");
        AssertSearchCenters(ready);
        await Task.Delay(1250);
        var swirlFrame = Draw();
        Require(scene.PhotoCopyCount > 0, "The animated Swirl fixture did not start.");
        AssertStableReference(ready, Ready(), "Swirl animation reset the button reference.");
        AssertEmpty(ready, swirlFrame, "A Swirl stamp outside the button interior became a hand candidate.");
        Require(scene.TryGetPhotoCopyMemoryImage(out var memory) && scene.BeginPhotoCopyMemorySave(memory) &&
                scene.CompletePhotoCopyMemorySave(memory), "The saved-status fixture was unavailable.");
        var savedFrame = Draw();
        AssertStableReference(ready, Ready(), "Image Saved feedback reset the button reference.");
        AssertEmpty(ready, savedFrame, "Image Saved feedback became a hand candidate.");

        foreach (var button in scene.CurrentBoardButtons.ToArray())
        {
            ready = Ready();
            var center = CameraPoint(button.Bounds.X + button.Bounds.Width / 2,
                button.Bounds.Y + button.Bounds.Height / 2);
            var empty = Draw();
            var occupied = (byte[])empty.Clone();
            // Four still fingers cover the lettering on their very first frame.
            // No previously empty webcam frame or temporal motion is supplied.
            for (int finger = 0; finger < 4; finger++)
                Fill(occupied, (int)center.X - 44 + finger * 23, (int)center.Y - 30,
                    21, 85, 95, 145, 195);
            var detector = new HandAcquisitionPresenceTracker();
            var firstArrival = CreateHandAcquisitionQuery(new CameraFrame(size, size, size * 4, occupied, now),
                ready, detector, now);
            Require(firstArrival.LightingHints.Count == 0 && firstArrival.SearchRegions.Count == 0,
                $"A single unconfirmed {button.Label} obstruction triggered model inference or illumination.");
            now += TimeSpan.FromMilliseconds(125);
            var nativeQuery = CreateHandAcquisitionQuery(new CameraFrame(size, size, size * 4, occupied, now),
                ready, detector, now);
            var presence = nativeQuery.Presence!;
            Require(presence.Hints.Count > 0 && presence.Hints.Any(hint =>
                    Math.Abs(hint.Center.X - center.X) < 65 && Math.Abs(hint.Center.Y - center.Y) < 65 &&
                    hint.ControlCoverage is double coverage && double.IsFinite(coverage) && coverage >= .07),
                $"Stationary fingers over {button.Label} produced no useful crop after two fresh frames: {presence.Reason}.");
            Require(nativeQuery.SearchRegions.Count is > 0 and <= 2 &&
                    nativeQuery.SearchRegions.Contains(presence.Hints[0].SearchBounds) && ready.IlluminatedHint is null,
                $"Qualified {button.Label} fingers did not receive native inference before fallback illumination.");
            scene.CompleteHandAcquisition(ready, presence.Hints, [], now);
            var illuminated = scene.GetHandAcquisitionContext(now)!;
            Require(illuminated.IlluminatedHint is not null &&
                    CountWhite(Draw(), center) > CountWhite(empty, center) + 1600,
                $"Foreground over {button.Label} did not illuminate the actual button and its text.");
            Require(scene.ActiveHandSpotlightCount == 0 && scene.HoveredBoardButtons.Count == 0 &&
                    scene.CurrentBoardScreen == BoardScreen.PhotoCopy &&
                    scene.CurrentBoardButtons.Select(item => item.Label).SequenceEqual(["Exit", "Clear", "Save"]) &&
                    scene.TryGetPhotoCopyMemoryImage(out var unchanged) && ReferenceEquals(unchanged.Cutout, photograph) &&
                    !scene.TryTakePhotoCopyCaptureRequest(now, out _) &&
                    !scene.TryTakePhotoCopyMemorySaveRequest(now, out _),
                "Acquisition assistance created a gesture, cleared the photo, saved it or navigated away.");
            AssertStableReference(ready, illuminated, "A search light contaminated the unlit button reference.");
            scene.CompleteHandAcquisition(illuminated, presence.Hints, [], now, illuminatedPresence: false);
            now += TimeSpan.FromMilliseconds(1000);
        }

        ready = Ready();
        var handCenter = CameraPoint(.5, .8875);
        var hint = new HandAcquisitionHint(new(handCenter.X - 100, handCenter.Y - 100, 200, 200),
            handCenter, 75, now, .08, ControlCoverage: .10, ControlTriggerCoverage: .10);
        scene.CompleteHandAcquisition(ready, [hint], [], now.AddSeconds(-1));
        Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint is null, "A stale camera frame relit the controls.");
        scene.CompleteHandAcquisition(ready, [hint], [], now.AddSeconds(1));
        Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint is null, "A future camera frame relit the controls.");
        scene.CompleteHandAcquisition(ready, [hint], [], now);
        var hand = new HandDetection(Enumerable.Repeat(handCenter, 21).ToArray(), .99, .5);
        scene.CompleteHandAcquisition(ready, [hint], [hand], now);
        Require(scene.GetHandAcquisitionContext(now)?.IlluminatedHint is null,
            "A confirmed hand did not take over from preliminary button illumination.");
        now += TimeSpan.FromMilliseconds(600);
        ready = Ready();
        scene.ClearHandTips();
        scene.CompleteHandAcquisition(ready, [hint with { ObservedAt = now }], [], now);
        Require(scene.GetHandAcquisitionContext(now) is { ObserveMotion: false, IlluminatedHint: null },
            "A result from before input reset restored its obsolete search light.");
        now += TimeSpan.FromMilliseconds(600);
        ready = Ready();
        await Task.Delay(5);
        var sourceTime = DateTimeOffset.UtcNow;
        scene.SetHandCursors([new(CameraPoint(.5, .5), sourceTime.AddSeconds(1), 99001)
            { TrackingId = 991 }], sourceTime);
        Require(scene.GetHandLightingDiagnostics().SuppressedHandIds.Contains(991),
            "The execute-suppression fixture did not suppress its hand.");
        scene.CompleteHandAcquisition(ready, [hint with { ObservedAt = now }], [], now);
        Require(scene.GetHandAcquisitionContext(now) is { ObserveMotion: false, IlluminatedHint: null },
            "A button disturbance relit an executed hand before it was removed.");
        scene.ClearHandTips();
        scene.GetHandAcquisitionContext(now);
        now += TimeSpan.FromMilliseconds(600);
        ready = Ready();
        scene.ShowBoardMenu();
        scene.CompleteHandAcquisition(ready, [hint with { ObservedAt = now }], [], now);
        Require(scene.GetHandAcquisitionContext(now) is { ObserveMotion: false, IlluminatedHint: null },
            "Photo Copy assistance survived Exit instead of starting a fresh menu reference.");
        scene.ShowPhotoCopy();
        Draw();
        scene.CompleteHandAcquisition(ready, [hint with { ObservedAt = now }], [], now);
        Require(scene.GetHandAcquisitionContext(now) is { ObserveMotion: false, IlluminatedHint: null },
            "A previous visit's result illuminated the new Photo Copy session.");
        scene.SetBoardSetup(true);
        Require(scene.GetHandAcquisitionContext(now) is null, "Calibration did not cancel control assistance.");
        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip),
            "The isolated verification changed the live camera, projector or board.");
        return new { passed = true, stationaryTwoFreshFramesOverExitClearSave = true,
            realRenderedButtonReference = true, boundedButtonMasks = true, motionFallbackDisabled = true,
            focusedObjectFieldStopsAfterSwirl = true,
            textChangesRefreshReference = true, swirlStatusObjectAndSearchLightsDoNotRetrigger = true,
            acquisitionCannotSelect = true, confirmedHandHandover = true,
            staleResetNavigationAndExecuteSuppression = true, liveHardwareUnchanged = true };

        SceneCompositor.HandAcquisitionContext Ready()
        {
            var context = scene.GetHandAcquisitionContext(now);
            Require(context is { ObserveMotion: true, ExpectedScene: not null },
                "Photo Copy control acquisition did not become ready.");
            return context!;
        }
        void AssertSearchCenters(SceneCompositor.HandAcquisitionContext context)
        {
            Require(context.StationarySearchCenters is { Length: 3 }, "Three control search centers are required.");
            for (int index = 0; index < 3; index++)
            {
                var bounds = scene.CurrentBoardButtons[index].Bounds;
                var expected = CameraPoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                var actual = context.StationarySearchCenters![index];
                Require(Math.Abs(expected.X - actual.X) < .1 && Math.Abs(expected.Y - actual.Y) < .1,
                    "A stationary search crop missed its actual Photo Copy button center.");
            }
        }
        void AssertEmpty(SceneCompositor.HandAcquisitionContext context, byte[] pixels, string message)
        {
            var detector = new HandAcquisitionPresenceTracker();
            var result = detector.Update(size, size, size * 4, pixels, context.SearchPolygon,
                context.ExpectedScene, now, now);
            Require(result.BaselineReady && result.Hints.Count == 0, message + " " + result.Reason);
        }
        static void AssertStableReference(SceneCompositor.HandAcquisitionContext first,
            SceneCompositor.HandAcquisitionContext second, string message) =>
            Require(first.Revision == second.Revision && ReferenceEquals(first.ExpectedScene, second.ExpectedScene), message);
        PixelPoint CameraPoint(double u, double v) => new(
            size * (.035 + .93 * (inset / 2 + u * (1 - inset))),
            size * (.035 + .93 * (inset / 2 + v * (1 - inset))));
        byte[] Draw()
        {
            using (var drawing = target.CreateDrawingSession())
                scene.Draw(drawing, size, size, preview: false, runningSlowly: false);
            return target.GetPixelBytes();
        }
        static void Fill(byte[] pixels, int left, int top, int width, int height, byte b, byte g, byte r)
        {
            for (int y = Math.Max(0, top); y < Math.Min(size, top + height); y++)
            for (int x = Math.Max(0, left); x < Math.Min(size, left + width); x++)
            {
                int offset = (y * size + x) * 4;
                pixels[offset] = b; pixels[offset + 1] = g; pixels[offset + 2] = r; pixels[offset + 3] = 255;
            }
        }
        static int CountWhite(byte[] pixels, PixelPoint center)
        {
            int count = 0;
            for (int y = (int)center.Y - 25; y <= (int)center.Y + 25; y++)
            for (int x = (int)center.X - 35; x <= (int)center.X + 35; x++)
            {
                int offset = (y * size + x) * 4;
                if (pixels[offset] > 245 && pixels[offset + 1] > 245 && pixels[offset + 2] > 245) count++;
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
