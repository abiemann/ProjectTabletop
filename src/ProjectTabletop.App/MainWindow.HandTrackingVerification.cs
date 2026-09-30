#if DEBUG
using System.Text.Json;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Isolated scene only: never sends synthetic gestures to the actual board.
    private static object VerifyHandTrackingInput()
    {
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
            var sourceTime = DateTimeOffset.UtcNow;
            var tip = new PixelPoint(.25, .35); // Inside Dragon Slots' menu target.
            scene.SetHandCursors([new(tip, DateTimeOffset.MinValue)], sourceTime);
            scene.ClearHandTips(resetInput);
            // Source frame predates the clear but is still fresh on completion.
            var selectingFrame = sourceTime.AddTicks(1);
            scene.SetHandCursors([new HandCursor(tip, DateTimeOffset.UtcNow.AddSeconds(1), 1) { TrackingId = 711 }], selectingFrame);
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
            var sourceTime = DateTimeOffset.UtcNow;
            scene.SetHandCursors([new(pointingTip, DateTimeOffset.MinValue)], sourceTime);
            scene.SetHandCursors([new(actualTip, DateTimeOffset.MinValue, 0, pointingTip, sourceTime)],
                DateTimeOffset.UtcNow);
            if (!scene.HoveredBoardButtons.SequenceEqual(["photo-copy"]))
                throw new InvalidOperationException("Closing a pinch moved the highlight away from Photo Copy.");
            var selectingFrame = DateTimeOffset.UtcNow;
            scene.SetHandCursors([new HandCursor(actualTip, selectingFrame.AddSeconds(1), 1, pointingTip, sourceTime)
                { TrackingId = 712 }], selectingFrame);
            if (scene.CurrentBoardScreen != BoardScreen.PhotoCopy)
                throw new InvalidOperationException("A curled fingertip prevented the pointed Photo Copy target from opening.");
            var selection = AssertHandSelectionDiagnostic(scene, BoardScreen.Menu, BoardScreen.PhotoCopy,
                "photo-copy", "Pinch", 712, selectingFrame);
            scene.SetHandCursors([], DateTimeOffset.UtcNow);
            scene.ClearHandTips(resetInput: false);
            foreach (var rejectedFrame in new[] { DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1) })
                scene.SetHandCursors([new HandCursor(new(.2, .88), rejectedFrame.AddSeconds(1), 2)
                    { TrackingId = 713 }], rejectedFrame);
            scene.SetHandCursors([new HandCursor(new(double.NaN, .88), DateTimeOffset.UtcNow.AddSeconds(1), 2)
                { TrackingId = 713 }], DateTimeOffset.UtcNow);
            if (!ReferenceEquals(selection, scene.GetLastHandBoardSelection()) || scene.CurrentBoardScreen != BoardScreen.PhotoCopy)
                throw new InvalidOperationException("Idle or rejected observations overwrote the successful pinch route after navigation.");
        }
        using (var scene = CreateMenu())
        {
            var sourceTime = DateTimeOffset.UtcNow;
            scene.SetHandCursors([new(new(.7, .33), DateTimeOffset.UtcNow.AddSeconds(1), 1,
                new(.5, .5), sourceTime)], DateTimeOffset.UtcNow);
            if (scene.CurrentBoardScreen != BoardScreen.Menu || scene.HoveredBoardButtons.Count != 0 ||
                scene.GetLastHandBoardSelection() is not null)
                throw new InvalidOperationException("An off-target pointing position clicked under the curled fingertip.");
        }
        return new { passed = true, freshQueuedPinchAccepted = true, resetRejectsOldInput = true,
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
            observedAt < frameTime || observedAt > DateTimeOffset.UtcNow)
            throw new InvalidOperationException("The retained hand selection lost its exact route, gesture, identity or source-frame timing.");
        return selection;
    }
}
#endif
