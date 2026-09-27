#if DEBUG
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
            var sourceTime = DateTimeOffset.UtcNow;
            var tip = new PixelPoint(.25, .35); // Inside Hand-Tracking's menu target.
            scene.SetHandCursors([new(tip, DateTimeOffset.MinValue)], sourceTime);
            scene.ClearHandTips(resetInput);
            // Source frame predates the clear but is still fresh on completion.
            scene.SetHandCursors([new(tip, DateTimeOffset.UtcNow.AddSeconds(1), 1)], sourceTime.AddTicks(1));
            var expected = resetInput ? BoardScreen.Menu : BoardScreen.HandTracking;
            if (scene.CurrentBoardScreen != expected)
                throw new InvalidOperationException(resetInput
                    ? "A camera reset accepted an old gesture frame."
                    : "A visual timeout rejected a fresh queued pinch.");
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
            scene.SetHandCursors([new(actualTip, DateTimeOffset.UtcNow.AddSeconds(1), 1, pointingTip, sourceTime)],
                DateTimeOffset.UtcNow);
            if (scene.CurrentBoardScreen != BoardScreen.PhotoCopy)
                throw new InvalidOperationException("A curled fingertip prevented the pointed Photo Copy target from opening.");
        }
        using (var scene = CreateMenu())
        {
            var sourceTime = DateTimeOffset.UtcNow;
            scene.SetHandCursors([new(new(.7, .33), DateTimeOffset.UtcNow.AddSeconds(1), 1,
                new(.5, .5), sourceTime)], DateTimeOffset.UtcNow);
            if (scene.CurrentBoardScreen != BoardScreen.Menu || scene.HoveredBoardButtons.Count != 0)
                throw new InvalidOperationException("An off-target pointing position clicked under the curled fingertip.");
        }
        return new { passed = true, freshQueuedPinchAccepted = true, resetRejectsOldInput = true,
            pointingTargetRetained = true, offTargetCurlRejected = true };

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
}
#endif
