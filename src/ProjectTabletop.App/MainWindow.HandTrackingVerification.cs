#if DEBUG
using System.Text.Json;
using ProjectTabletop.App.Camera;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Queue checks keep workers disabled; synthetic gestures use isolated scenes.
    private object VerifyHandTrackingInput()
    {
        VerifyHandInputQueueReset();
        foreach (bool resetInput in new[] { false, true })
        {
            using var scene = new SceneCompositor();
            scene.SetBoardSetup(true);
            scene.SetDetectedBoardGrid([new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                Homography.FromFourPoints([new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            scene.SetBoardSetup(false);
            if (scene.GetLastHandBoardSelection() is not null)
                throw new InvalidOperationException("An idle scene invented a successful hand selection.");
            var sourceTime = MonotonicClock.UtcNow;
            var tip = new PixelPoint(.25, .35); // Inside Dragon Slots' menu target.
            scene.SetHandCursors([new(tip, DateTimeOffset.MinValue)], sourceTime);
            scene.ClearHandTips(resetInput);
            // Source frame predates the clear but is still fresh on completion.
            var selectingFrame = sourceTime.AddTicks(1);
            scene.SetHandCursors([new HandCursor(tip, MonotonicClock.UtcNow.AddSeconds(1), 1) { TrackingId = 711 }], selectingFrame);
            var expected = resetInput ? BoardScreen.Menu : BoardScreen.Slots;
            if (scene.CurrentBoardScreen != expected)
                throw new InvalidOperationException(resetInput
                    ? "A camera reset accepted an old gesture frame."
                    : "A visual timeout rejected a fresh queued pinch.");
            if (resetInput)
            {
                if (scene.GetLastHandBoardSelection() is not null)
                    throw new InvalidOperationException("A rejected pre-reset pinch fabricated selection diagnostics.");
            }
            else
                AssertHandSelectionDiagnostic(scene, BoardScreen.Menu, BoardScreen.Slots,
                    "slots", "Pinch", 711, selectingFrame);
        }
        foreach (var actualTip in new[] { new PixelPoint(.7, .5), new PixelPoint(1.03, .5) })
        {
            using var scene = CreateMenu();
            var pointingTip = new PixelPoint(.7, .33); // Photo Copy.
            var sourceTime = MonotonicClock.UtcNow;
            scene.SetHandCursors([new(pointingTip, DateTimeOffset.MinValue)], sourceTime);
            scene.SetHandCursors([new(actualTip, DateTimeOffset.MinValue, 0, pointingTip, sourceTime)],
                MonotonicClock.UtcNow);
            if (!scene.HoveredBoardButtons.SequenceEqual(["photo-copy"]))
                throw new InvalidOperationException("Closing a pinch moved the highlight away from Photo Copy.");
            var selectingFrame = MonotonicClock.UtcNow;
            scene.SetHandCursors([new HandCursor(actualTip, selectingFrame.AddSeconds(1), 1, pointingTip, sourceTime)
                { TrackingId = 712 }], selectingFrame);
            if (scene.CurrentBoardScreen != BoardScreen.PhotoCopy)
                throw new InvalidOperationException("A curled fingertip prevented the pointed Photo Copy target from opening.");
            var selection = AssertHandSelectionDiagnostic(scene, BoardScreen.Menu, BoardScreen.PhotoCopy,
                "photo-copy", "Pinch", 712, selectingFrame);
            scene.SetHandCursors([], MonotonicClock.UtcNow);
            scene.ClearHandTips(resetInput: false);
            foreach (var rejectedFrame in new[] { MonotonicClock.UtcNow.AddSeconds(-1), MonotonicClock.UtcNow.AddSeconds(1) })
                scene.SetHandCursors([new HandCursor(new(.2, .88), rejectedFrame.AddSeconds(1), 2)
                    { TrackingId = 713 }], rejectedFrame);
            scene.SetHandCursors([new HandCursor(new(double.NaN, .88), MonotonicClock.UtcNow.AddSeconds(1), 2)
                { TrackingId = 713 }], MonotonicClock.UtcNow);
            if (!ReferenceEquals(selection, scene.GetLastHandBoardSelection()) || scene.CurrentBoardScreen != BoardScreen.PhotoCopy)
                throw new InvalidOperationException("Idle or rejected observations overwrote the successful pinch route after navigation.");
        }
        using (var scene = CreateMenu())
        {
            var sourceTime = MonotonicClock.UtcNow;
            scene.SetHandCursors([new(new(.7, .33), MonotonicClock.UtcNow.AddSeconds(1), 1,
                new(.5, .5), sourceTime)], MonotonicClock.UtcNow);
            if (scene.CurrentBoardScreen != BoardScreen.Menu || scene.HoveredBoardButtons.Count != 0 ||
                scene.GetLastHandBoardSelection() is not null)
                throw new InvalidOperationException("An off-target pointing position clicked under the curled fingertip.");
        }
        return new { passed = true, freshQueuedPinchAccepted = true, resetRejectsOldInput = true,
            handAndPaintQueuesRejectPreResetFrames = true, queueResetBoundaryPreservesFreshFrames = true,
            pointingTargetRetained = true, offTargetCurlRejected = true,
            exactPinchSelectionRouteRetained = true, rejectedObservationsNeverFabricateOrOverwriteSelection = true };

        static SceneCompositor CreateMenu()
        {
            var scene = new SceneCompositor();
            scene.SetBoardSetup(true);
            scene.SetDetectedBoardGrid([new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                Homography.FromFourPoints([new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            scene.SetBoardSetup(false);
            return scene;
        }
    }

    private void VerifyHandInputQueueReset()
    {
        lock (_handGate)
        {
            var originalCutoff = _handInputNotBefore;
            bool originalEnabled = _handTrackingEnabled;
            var originalHandTask = _handDetectionTask;
            var originalPaintTask = _paintDetectionTask;
            var originalHandTick = _lastHandDetectionTick;
            var originalPaintTick = _lastPaintDetectionTick;
            bool originalHandDetecting = _handDetecting;
            int originalPaintDetecting = _paintDetecting;
            try
            {
                // Disable inference even if a regression removes the timestamp check.
                // An old frame is otherwise fresh, and receives the current generation
                // if admitted, matching a delivery callback that crossed a reset.
                _handTrackingEnabled = false;
                var now = MonotonicClock.UtcNow;
                _handInputNotBefore = now;
                var old = new CameraFrame(1, 1, 4, [0, 0, 0, 255], now.AddTicks(-1));
                foreach (var frame in new[] { old, old with { Timestamp = now }, old with { Timestamp = now.AddTicks(1) } })
                {
                    var expected = frame == old ? InputFrameQueueResult.PredatesReset : InputFrameQueueResult.Skipped;
                    if (QueueHandDetection(frame, System.Diagnostics.Stopwatch.GetTimestamp()) != expected ||
                        QueuePaintDetection(frame, System.Diagnostics.Stopwatch.GetTimestamp()) != expected)
                        throw new InvalidOperationException("A hand or Paint queue admitted pre-reset pixels or rejected the reset boundary.");
                }
                if (!ReferenceEquals(originalHandTask, _handDetectionTask) ||
                    !ReferenceEquals(originalPaintTask, _paintDetectionTask) ||
                    originalHandTick != _lastHandDetectionTick || originalPaintTick != _lastPaintDetectionTick ||
                    originalHandDetecting != _handDetecting || originalPaintDetecting == 0 && _paintDetecting != 0)
                    throw new InvalidOperationException("A rejected hand or Paint frame changed inference bookkeeping.");
            }
            finally
            {
                _handInputNotBefore = originalCutoff;
                _handTrackingEnabled = originalEnabled;
            }
        }
    }

    private static object AssertHandSelectionDiagnostic(SceneCompositor scene, BoardScreen previous, BoardScreen current,
        string buttonId, string gesture, long trackingId, DateTimeOffset frameTime)
    {
        var selection = scene.GetLastHandBoardSelection() ??
            throw new InvalidOperationException("A successful hand selection did not retain its diagnostic route.");
        var recorded = JsonSerializer.SerializeToElement(selection);
        var observedAt = recorded.GetProperty("observedAt").GetDateTimeOffset();
        if (recorded.GetProperty("previous").GetString() != previous.ToString() ||
            recorded.GetProperty("current").GetString() != current.ToString() ||
            recorded.GetProperty("ButtonId").GetString() != buttonId ||
            recorded.GetProperty("gesture").GetString() != gesture ||
            recorded.GetProperty("TrackingId").GetInt64() != trackingId ||
            recorded.GetProperty("frameTime").GetDateTimeOffset() != frameTime ||
            observedAt < frameTime || observedAt > MonotonicClock.UtcNow)
            throw new InvalidOperationException("The retained hand selection lost its exact route, gesture, identity or source-frame timing.");
        return selection;
    }
}
#endif
