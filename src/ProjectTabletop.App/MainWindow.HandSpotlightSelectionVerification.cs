#if DEBUG
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // All synthetic observations stay in an isolated compositor and offscreen target.
    private async Task<object> VerifyHandSpotlightSelectionAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible, Volatile.Read(ref _boardSetupActive),
            _scene.CurrentBoardScreen, _scene.HasBoardMediaClip, _scene.BlackjackState.Revision);
        using var scene = new SceneCompositor();
        scene.SetDisplayAspect(1);
        scene.SetBoardSetup(true);
        var inset = scene.SetDetectedBoardGrid([new(.1f, .1f), new(.9f, .1f), new(.9f, .9f), new(.1f, .9f)],
            Homography.FromFourPoints([new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        scene.ShowHandTrackingTest();
        const int size = 800;
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 96);
        Draw(); // Warm the text and GPU cache before timed source observations.
        var left = Hand(.3, .6); var right = Hand(.7, .6);
        var leftCursor = Cursor(left, 71); var rightCursor = Cursor(right, 72);
        await Send((left, leftCursor), (right, rightCursor));
        var both = Draw();
        Require(scene.ActiveHandSpotlightCount == 2 && WhiteAt(both, new(.3, .6)) && WhiteAt(both, new(.7, .6)),
            "The fixture did not render two independent hand lights.");

        await Task.Delay(20);
        var pinchTime = DateTimeOffset.UtcNow;
        var pinched = leftCursor with { ExecuteEventId = 100, ExecuteUntil = pinchTime.AddSeconds(1) };
        Push([(left, pinched), (right, rightCursor)], pinchTime);
        var one = Draw();
        Require(scene.ActiveHandSpotlightCount == 1 && Suppressed(71) && !WhiteAt(one, new(.3, .6)) &&
            WhiteAt(one, new(.7, .6)) && PatchEquals(both, one, new(.7, .6)),
            "Executing one hand did not immediately extinguish only its own spotlight.");
        // Suppression outlasts both the execute pulse and observed release/rejoining.
        for (int frame = 0; frame < 11; frame++) await Send((left, pinched), (right, rightCursor));
        await Send((left, leftCursor), (right, rightCursor));
        await Send((left, leftCursor with { HasFourExtendedFingers = true, FingersTogether = true }), (right, rightCursor));
        Require(scene.ActiveHandSpotlightCount == 1 && !WhiteAt(Draw(), new(.3, .6)),
            "A held pinch, expired pulse, released pinch or rejoined fingers relit the hand without removal.");

        // A known illuminated hand may briefly disappear then return close to
        // the suppressed hand. Its stable identity must win over proximity.
        await Send((left, leftCursor));
        var nearbyRight = Hand(.35, .6);
        await Send((nearbyRight, Cursor(nearbyRight, 72)));
        Require(Suppressed(71) && !Suppressed(72) && scene.ActiveHandSpotlightCount == 1 && WhiteAt(Draw(), new(.35, .6)),
            "A briefly missing known hand stole another hand's suppression by returning nearby.");
        await Send((left, leftCursor), (right, rightCursor));

        await Send((right, rightCursor));
        await Send((right, rightCursor));
        leftCursor = Cursor(left, 171); // Tracking can assign a new ID after a short dark-image loss.
        var lastFresh = await Send((right, rightCursor), (left, leftCursor));
        Require(Suppressed(171) && scene.ActiveHandSpotlightCount == 1 && !WhiteAt(Draw(), new(.3, .6)),
            "A brief loss, reordered hands or replacement tracking ID relit the same physical hand.");
        foreach (var invalid in new[] { lastFresh, lastFresh.AddTicks(-1), DateTimeOffset.UtcNow.AddSeconds(-2), DateTimeOffset.UtcNow.AddSeconds(2) })
            Push([], invalid);
        await Send((left, leftCursor), (right, rightCursor));
        Require(Suppressed(171) && scene.ActiveHandSpotlightCount == 1, "Rejected source frames released suppression.");

        // Missing frames must be fresh and consecutive. A camera stall is not removal.
        await Task.Delay(420);
        await Send();
        Require(Suppressed(171), "A stalled camera counted as sustained hand removal.");
        await Send((left, leftCursor), (right, rightCursor));
        DateTimeOffset? firstMissing = null;
        for (int frame = 0; frame < 11; frame++)
        {
            var time = await Send();
            firstMissing ??= time;
            if (time - firstMissing.Value < TimeSpan.FromMilliseconds(850))
                Require(Suppressed(171), "The hand relit before 900 ms of confirmed removal.");
        }
        Require(scene.GetHandLightingDiagnostics().SuppressedHandIds.Length == 0 && scene.ActiveHandSpotlightCount == 0,
            "Consecutive fresh missing observations did not release suppression.");
        Push([(left, leftCursor), (right, rightCursor)], lastFresh);
        Require(scene.ActiveHandSpotlightCount == 0, "A pre-removal frame relit the hand after suppression cleared.");
        await Send((left, leftCursor), (right, rightCursor));
        Require(scene.ActiveHandSpotlightCount == 2 && WhiteAt(Draw(), new(.3, .6)), "A genuinely returned hand did not regain its light.");

        scene.ShowBoardMenu();
        Draw();
        var middle = BoardPoint(.28, .33);
        var selecting = Hand(middle.X, middle.Y + .1);
        var grouped = Cursor(selecting, 273) with { HasFourExtendedFingers = true, FingersTogether = true };
        var separated = grouped with { FingersTogether = false, IndexFingerSeparated = true };
        await Send((selecting, separated)); await Send((selecting, separated));
        Require(scene.CurrentBoardScreen == BoardScreen.Menu && !Suppressed(273) && scene.ActiveHandSpotlightCount == 1,
            "A separated pose with no accepted button action suppressed its light.");
        await Send((selecting, grouped)); await Send((selecting, grouped));
        Require(scene.ActiveHandSpotlightCount == 1 && WhiteAt(Draw(), middle), "Merely arming a button suppressed its light.");
        await Send((selecting, separated));
        Require(!Suppressed(273), "The first unconfirmed separation suppressed its light.");
        await Send((selecting, separated));
        Require(scene.CurrentBoardScreen == BoardScreen.HandTracking && Suppressed(273) &&
            scene.ActiveHandSpotlightCount == 0 && !WhiteAt(Draw(), middle),
            "An accepted four-finger selection did not extinguish its hand light.");

        scene.ShowPhotoCopy();
        Draw();
        await Task.Delay(1100);
        Require(scene.TryGetPhotoCopyCaptureContext(out var context), "The grey object fixture did not become ready.");
        const int fixtureSize = 1000;
        var pixels = new byte[fixtureSize * fixtureSize * 4];
        for (int y = 0; y < fixtureSize; y++)
        for (int x = 0; x < fixtureSize; x++)
        {
            int offset = (y * fixtureSize + x) * 4;
            byte value = x is >= 220 and < 320 && y is >= 530 and < 670 ? (byte)30 : (byte)100;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value; pixels[offset + 3] = 255;
        }
        var photoObject = PhotoObjectLocator.Locate(fixtureSize, fixtureSize, fixtureSize * 4, pixels,
            [.001, 0, 0, 0, .001, 0, 0, 0, 1], out var failure);
        Require(photoObject is not null && scene.SetPhotoCopyObject(photoObject, context.Revision), "Object light fixture failed: " + failure);
        await Send((right, rightCursor));
        var objectCenter = BoardPoint(photoObject!.Center.X / 1000, photoObject.Center.Y / 1000);
        Require(WhiteAt(Draw(), objectCenter) && scene.ActiveHandSpotlightCount == 1, "Independent Photo Copy lights were not present.");
        await Task.Delay(20);
        var shutterTime = DateTimeOffset.UtcNow;
        Push([(right, rightCursor with { ExecuteEventId = 200, ExecuteUntil = shutterTime.AddSeconds(1) })], shutterTime);
        Require(scene.ActiveHandSpotlightCount == 0 && !WhiteAt(Draw(), new(.7, .6)) && WhiteAt(Draw(), objectCenter),
            "Suppressing the shutter hand also removed the separate object spotlight.");

        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen, _scene.HasBoardMediaClip, _scene.BlackjackState.Revision),
            "The isolated spotlight-selection check changed live hardware or gameplay.");
        return new { passed = true, executingHandOnly = true, pulseReleaseAndRejoinStayDark = true,
            knownOtherHandKeepsLight = true, briefLossAndNewIdentityStayDark = true, invalidFramesCannotRelight = true,
            freshRemovalMilliseconds = 900, reentryRelights = true, onlyAcceptedFingerActionSuppresses = true,
            photoCopyObjectLightPreserved = true, liveHardwareUnchanged = true };

        async Task<DateTimeOffset> Send(params (HandDetection Hand, HandCursor Cursor)[] observations)
        {
            await Task.Delay(110);
            var time = DateTimeOffset.UtcNow; Push(observations, time); return time;
        }
        void Push((HandDetection Hand, HandCursor Cursor)[] observations, DateTimeOffset time)
        {
            scene.SetHandCursors(observations.Select(item => item.Cursor).ToArray(), time);
            scene.SetHandSpotlights(observations.Select(item => item.Hand).ToArray(), time);
        }
        bool Suppressed(long id) => scene.GetHandLightingDiagnostics().SuppressedHandIds.Contains(id);
        byte[] Draw()
        {
            using (var drawing = target.CreateDrawingSession()) scene.Draw(drawing, size, size, preview: false, runningSlowly: false);
            return target.GetPixelBytes();
        }
        PixelPoint BoardPoint(double u, double v) => new(.1 + .8 * (inset / 2 + u * (1 - inset)), .1 + .8 * (inset / 2 + v * (1 - inset)));
        static HandCursor Cursor(HandDetection hand, long id) => new(hand.IndexTip, DateTimeOffset.MinValue)
            { TrackingId = id, FingerTips = Array.AsReadOnly(new[] { hand.Landmarks[8], hand.Landmarks[12], hand.Landmarks[16], hand.Landmarks[20] }) };
        static HandDetection Hand(double x, double y)
        {
            PixelPoint[] local = [new(0, .09), new(-.03, .06), new(-.055, .035), new(-.07, .015), new(-.09, 0),
                new(-.035, .015), new(-.04, -.02), new(-.045, -.05), new(-.05, -.08),
                new(0, 0), new(0, -.04), new(0, -.07), new(0, -.1), new(.03, .015), new(.035, -.02), new(.04, -.045), new(.045, -.07),
                new(.055, .03), new(.065, .005), new(.075, -.01), new(.08, -.025)];
            return new(local.Select(point => new PixelPoint(x + point.X, y + point.Y)).ToArray(), .95, .5);
        }
        static bool WhiteAt(byte[] image, PixelPoint point)
        {
            int offset = ((int)Math.Round(point.Y * size) * size + (int)Math.Round(point.X * size)) * 4;
            return image[offset] >= 250 && image[offset + 1] >= 250 && image[offset + 2] >= 250 && image[offset + 3] == 255;
        }
        static bool PatchEquals(byte[] first, byte[] second, PixelPoint center)
        {
            for (int y = (int)(center.Y * size) - 5; y <= center.Y * size + 5; y++)
            for (int x = (int)(center.X * size) - 5; x <= center.X * size + 5; x++)
            for (int channel = 0; channel < 4; channel++)
                if (first[(y * size + x) * 4 + channel] != second[(y * size + x) * 4 + channel]) return false;
            return true;
        }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
#endif
