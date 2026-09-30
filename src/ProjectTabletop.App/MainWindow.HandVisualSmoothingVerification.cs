#if DEBUG
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Rendering copies must never become gesture input or change the identity
    // used to turn off a hand's light. All observations here stay offscreen.
    private async Task<object> VerifyHandVisualSmoothingAsync()
    {
        const int size = 800;
        using var scene = new SceneCompositor();
        scene.SetDisplayAspect(1);
        scene.SetBoardSetup(true);
        var inset = scene.SetDetectedBoardGrid([new(.1f, .1f), new(.9f, .1f), new(.9f, .9f), new(.1f, .9f)],
            Homography.FromFourPoints([new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        scene.ShowHandTrackingTest();
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 96);
        Draw();
        var raw = FourTips(new(.3, .6), 401);
        var visual = FourTips(new(.6, .6), 401);
        var time = DateTimeOffset.UtcNow;
        scene.SetHandCursors([raw], time, visualCursors: [visual]);
        var pixels = Draw();
        Require(GoldNear(pixels, visual.FingerTips[1]) > 20 && GoldNear(pixels, raw.FingerTips[1]) == 0,
            "The projector did not draw the supplied visual fingertip instead of its raw input position.");
        scene.SetHandCursors([], DateTimeOffset.UtcNow, visualCursors: [visual]);
        Require(GoldNear(Draw(), visual.FingerTips[1]) == 0,
            "A display copy rendered a missing hand.");

        foreach (var show in new Action[] { scene.ShowBoardMenu, scene.ShowPhotoCopy, scene.ShowBlackjack, scene.ShowHandTrackingTest })
        {
            show();
            scene.ClearHandTips();
            var baseline = Draw();
            scene.SetHandCursors([raw], DateTimeOffset.UtcNow, visualCursors: [visual]);
            var shown = Draw();
            Require(NewGoldNear(baseline, shown, visual.FingerTips[1]) > 20,
                "The shared visual fingertip failed to render on " + scene.CurrentBoardScreen + ".");
        }

        scene.ShowBoardMenu();
        scene.ClearHandTips();
        var inputPoint = BoardPoint(.28, .33);
        var wrongVisualPoint = BoardPoint(.72, .33);
        var pointing = new HandCursor(inputPoint, DateTimeOffset.MinValue) { TrackingId = 402 };
        var displayed = pointing with { Position = wrongVisualPoint };
        scene.SetHandCursors([pointing], DateTimeOffset.UtcNow, visualCursors: [displayed]);
        Require(scene.HoveredBoardButtons.SequenceEqual(["slots"]),
            "Visual damping changed button aiming to the displayed Photo Copy position.");
        await Task.Delay(5);
        time = DateTimeOffset.UtcNow;
        scene.SetHandCursors([pointing with { ExecuteEventId = 101, ExecuteUntil = time.AddSeconds(1) }],
            time, visualCursors: [displayed]);
        Require(scene.CurrentBoardScreen == BoardScreen.Slots,
            "A filtered display position changed the pinch's selected board.");

        scene.ClearHandTips();
        var hand = Hand(.4, .6);
        Observe(hand);
        var first = scene.GetHandLightingDiagnostics().Lights.Single();
        await Task.Delay(40);
        hand = Hand(.403, .602);
        Observe(hand);
        var lighting = scene.GetHandLightingDiagnostics();
        var filteredLight = lighting.Lights.Single();
        var rawLight = lighting.RawLights.Single();
        double rawMotion = Distance(first.Center, rawLight.Center);
        double renderedMotion = Distance(first.Center, filteredLight.Center);
        Require(renderedMotion > 0 && renderedMotion < rawMotion * .85,
            "The compositor did not damp a small spotlight-center change.");
        Require(filteredLight.Radius >= rawLight.Radius + Distance(filteredLight.Center, rawLight.Center) - 1e-9,
            "The damped spotlight no longer covers the current measured hand circle.");
        Require(lighting.LightLifetimes.Single().TrackingId == 411,
            "Smoothed cursor geometry broke raw hand-to-light identity matching.");
        await Task.Delay(5);
        time = DateTimeOffset.UtcNow;
        var executing = new HandCursor(hand.IndexTip, time.AddSeconds(1), 102) { TrackingId = 411 };
        scene.SetHandCursors([executing], time, visualCursors: [executing with { Position = new(.7, .7) }]);
        scene.SetHandSpotlights([hand], time);
        Require(scene.ActiveHandSpotlightCount == 0 &&
            scene.GetHandLightingDiagnostics().SuppressedHandIds.Contains(411),
            "An execute with a smoothed marker failed to extinguish its own hand light.");

        return new { passed = true, projectorUsesVisualTips = true, sharedAcrossBoards = true, noMissingHandGhosts = true,
            inputUsesRawPositions = true, spotlightDamped = true, currentFingerCoverage = true,
            suppressionUsesRawIdentity = true, spotlightMotionRatio = renderedMotion / rawMotion };

        void Observe(HandDetection observation)
        {
            var sourceTime = DateTimeOffset.UtcNow;
            var cursor = new HandCursor(observation.IndexTip, DateTimeOffset.MinValue) { TrackingId = 411 };
            scene.SetHandCursors([cursor], sourceTime,
                visualCursors: [cursor with { Position = new(.7, .7) }]);
            scene.SetHandSpotlights([observation], sourceTime);
        }
        byte[] Draw()
        {
            using (var drawing = target.CreateDrawingSession()) scene.Draw(drawing, size, size, false, false);
            return target.GetPixelBytes();
        }
        PixelPoint BoardPoint(double u, double v) => new(
            .1 + .8 * (inset / 2 + u * (1 - inset)), .1 + .8 * (inset / 2 + v * (1 - inset)));
        static HandCursor FourTips(PixelPoint center, long id) => new(center, DateTimeOffset.MinValue)
        {
            TrackingId = id, HasFourExtendedFingers = true,
            FingerTips = [center, new(center.X + .03, center.Y), new(center.X + .06, center.Y), new(center.X + .09, center.Y)]
        };
        static int GoldNear(byte[] data, PixelPoint point)
        {
            int count = 0;
            for (int y = (int)(point.Y * size) - 14; y <= (int)(point.Y * size) + 14; y++)
            for (int x = (int)(point.X * size) - 14; x <= (int)(point.X * size) + 14; x++)
            {
                int offset = (y * size + x) * 4;
                if (data[offset + 2] > 200 && data[offset + 1] is > 160 and < 235 && data[offset] < 160) count++;
            }
            return count;
        }
        static int NewGoldNear(byte[] before, byte[] after, PixelPoint point)
        {
            int count = 0;
            for (int y = (int)(point.Y * size) - 14; y <= (int)(point.Y * size) + 14; y++)
            for (int x = (int)(point.X * size) - 14; x <= (int)(point.X * size) + 14; x++)
            {
                int i = (y * size + x) * 4;
                if (after[i + 2] > 200 && after[i + 1] is > 160 and < 235 && after[i] < 160 &&
                    Math.Abs(before[i] - after[i]) + Math.Abs(before[i + 1] - after[i + 1]) +
                    Math.Abs(before[i + 2] - after[i + 2]) > 30) count++;
            }
            return count;
        }
        static HandDetection Hand(double x, double y)
        {
            PixelPoint[] points = [new(0, .09), new(-.03, .06), new(-.055, .035), new(-.07, .015), new(-.09, 0),
                new(-.035, .015), new(-.04, -.02), new(-.045, -.05), new(-.05, -.08),
                new(0, 0), new(0, -.04), new(0, -.07), new(0, -.1),
                new(.03, .015), new(.035, -.02), new(.04, -.045), new(.045, -.07),
                new(.055, .03), new(.065, .005), new(.075, -.01), new(.08, -.025)];
            return new(points.Select(p => new PixelPoint(x + p.X, y + p.Y)).ToArray(), .95, .5);
        }
        static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
#endif
