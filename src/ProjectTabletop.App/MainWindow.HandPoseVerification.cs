#if DEBUG
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Synthetic observations stay inside an isolated compositor, never the
    // camera tracker, live navigation session, or recording timeline.
    private async Task<object> VerifyHandPoseFeedbackAsync()
    {
        const int size = 1200;
        using var scene = new SceneCompositor();
        scene.SetDisplayAspect(1);
        scene.SetBoardSetup(true);
        var inset = scene.SetDetectedBoardGrid([new(.1f, .1f), new(.9f, .1f), new(.9f, .9f), new(.1f, .9f)],
            Homography.FromFourPoints([new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        scene.ShowHandTrackingTest();
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 96);
        var tip = BoardPoint(.5, .65); // Below the tester's navigation controls.
        var waiting = Draw(); // Warm font/surface creation before timed observations.
        Expect("Waiting for a hand");

        Send(spread: false);
        var pointing = Draw();
        Expect("Pinch: red circle for one second");
        Send(spread: true);
        var spread = Draw();
        Expect("Spread out hand");
        Require(CaptionDifference(pointing, spread) > 25 && CaptionDifference(waiting, spread) > 25,
            "The spread caption changed in status but not in the rendered tester panel.");

        string directory = Path.Combine(_appDataDirectory, "HandPoseSnapshots", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string spreadPath = Path.Combine(directory, "spread-out-hand.png");
        await target.SaveAsync(spreadPath, CanvasBitmapFileFormat.Png);

        Send(spread: false, pinch: true);
        var pinch = Draw();
        Expect("PINCH DETECTED");
        Require(CaptionDifference(spread, pinch) > 25 && RedRingPixels(pinch) > 20,
            "The pinch fixture did not render its distinct caption and red ring.");

        Send(spread: true, pinch: true);
        var spreadWithPulse = Draw();
        Expect("Spread out hand");
        Require(CaptionDifference(spread, spreadWithPulse) == 0 && RedRingPixels(spreadWithPulse) > 20,
            "A remembered pinch pulse overrode the current spread pose or lost its independent red ring.");

        Send(spread: false);
        Expect("Pinch: red circle for one second");
        Require(CaptionDifference(pointing, Draw()) == 0,
            "Closing the spread pose did not immediately restore the ordinary hand caption.");
        scene.SetHandCursors([], DateTimeOffset.UtcNow);
        Expect("Waiting for a hand");
        Require(CaptionDifference(waiting, Draw()) == 0, "An empty hand observation retained a spread caption.");

        Send(spread: true);
        await Task.Delay(390);
        Expect("Waiting for a hand");
        Require(CaptionDifference(waiting, Draw()) == 0,
            "The projected pose outlived its 350 ms source-frame lifetime.");

        Send(spread: true);
        scene.ClearHandTips(resetInput: false);
        Expect("Waiting for a hand");
        Require(CaptionDifference(waiting, Draw()) == 0, "A visual clear retained the spread caption.");

        Send(spread: true);
        await Task.Delay(2);
        var unprocessedBeforeReset = DateTimeOffset.UtcNow;
        await Task.Delay(2);
        scene.ClearHandTips(resetInput: true);
        scene.SetHandCursors([new(tip, DateTimeOffset.MinValue) { IsSpreadOut = true }], unprocessedBeforeReset);
        Expect("Waiting for a hand");
        Require(CaptionDifference(waiting, Draw()) == 0,
            "A fresh but pre-reset observation restored the spread caption.");

        // Invalid source times cannot introduce a new pose, even when their
        // landmark coordinates would otherwise be valid on the board.
        var lastAccepted = Send(spread: false);
        foreach (var rejected in new[] { lastAccepted, lastAccepted.AddTicks(-1),
                     DateTimeOffset.UtcNow.AddSeconds(1), DateTimeOffset.UtcNow.AddSeconds(-1) })
        {
            scene.SetHandCursors([new(tip, DateTimeOffset.MinValue) { IsSpreadOut = true }], rejected);
            Require(scene.HandTrackingTestStatus != "Spread out hand", "A rejected source frame introduced a spread pose.");
        }

        scene.ShowBoardMenu();
        var menu = Draw();
        scene.SetHandCursors([new(BoardPoint(.25, .33), DateTimeOffset.MinValue) { IsSpreadOut = true }],
            DateTimeOffset.UtcNow);
        Require(scene.CurrentBoardScreen == BoardScreen.Menu && scene.HandTrackingTestStatus == string.Empty &&
            scene.HoveredBoardButtons.SequenceEqual(["slots"]),
            "A spread hand executed a menu command or exposed tester status on the menu.");
        Require(CaptionDifference(menu, Draw()) == 0, "Spread-hand feedback appeared above the menu buttons.");

        scene.ClearHandTips(resetInput: false);
        scene.ShowPhotoCopy();
        var photo = Draw();
        Send(spread: true);
        Require(scene.CurrentBoardScreen == BoardScreen.PhotoCopy && scene.HandTrackingTestStatus == string.Empty &&
            CaptionDifference(photo, Draw()) == 0, "Spread feedback replaced the Photo Copy title or issued a command.");

        return new { passed = true, captionsRendered = true, currentPosePrecedesPinchPulse = true,
            pinchRingPreserved = true, emptyAndClosedClear = true, sourceExpiryMilliseconds = 350,
            resetRejectsOldFrames = true, rejectedSourceTimes = true, poseDoesNotExecute = true,
            testerOnlyCaption = true, directory, images = new[] { new { name = "spread-out-hand", path = spreadPath } } };

        DateTimeOffset Send(bool spread, bool pinch = false)
        {
            var time = DateTimeOffset.UtcNow;
            scene.SetHandCursors([new(tip, pinch ? time.AddSeconds(1) : DateTimeOffset.MinValue)
                { IsSpreadOut = spread }], time);
            return time;
        }

        byte[] Draw()
        {
            using (var drawing = target.CreateDrawingSession())
                scene.Draw(drawing, size, size, preview: false, runningSlowly: false);
            return target.GetPixelBytes();
        }

        PixelPoint BoardPoint(double u, double v) => new(.1 + .8 * (inset / 2 + u * (1 - inset)),
            .1 + .8 * (inset / 2 + v * (1 - inset)));

        int CaptionDifference(byte[] before, byte[] after)
        {
            var start = BoardPoint(.408, .109);
            var end = BoardPoint(.925, .151);
            int changed = 0;
            for (int y = (int)(start.Y * size); y <= (int)(end.Y * size); y++)
            for (int x = (int)(start.X * size); x <= (int)(end.X * size); x++)
            {
                int index = (y * size + x) * 4;
                if (Math.Abs(before[index] - after[index]) + Math.Abs(before[index + 1] - after[index + 1]) +
                    Math.Abs(before[index + 2] - after[index + 2]) > 12) changed++;
            }
            return changed;
        }

        int RedRingPixels(byte[] pixels)
        {
            int count = 0, centerX = (int)(tip.X * size), centerY = (int)(tip.Y * size);
            for (int y = centerY - 30; y <= centerY + 30; y++)
            for (int x = centerX - 30; x <= centerX + 30; x++)
            {
                int index = (y * size + x) * 4;
                if (pixels[index + 2] > 160 && pixels[index + 1] < 80 && pixels[index] < 80) count++;
            }
            return count;
        }

        void Expect(string expected) => Require(scene.HandTrackingTestStatus == expected,
            $"Expected tester status '{expected}', received '{scene.HandTrackingTestStatus}'.");
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
