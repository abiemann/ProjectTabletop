#if DEBUG
using System.Numerics;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Media;
using ProjectTabletop.App.Projection;
using ProjectTabletop.App.Projection.WaterGarden;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Synthetic observations and a private offscreen scene; no camera, visible
    // output, MainWindow constructor or enabled sound endpoint is touched.
    private async Task<object> VerifyWaterGardenSwishAudioAsync()
    {
        var now = DateTimeOffset.UtcNow;
        using var scene = new SceneCompositor(waterClock: () => now, boardRevealClock: () => now);
        var changes = new List<bool>();
        var pending = new Queue<Func<bool>>();
        int published = 0;
        scene.WaterGardenAudioChanged += () =>
        {
            published++;
            bool active = scene.WaterGardenSwishAudioActive;
            if (changes.Count == 0 || changes[^1] != active) changes.Add(active);
            pending.Enqueue(() => scene.WaterGardenSwishAudioActive);
        };
        scene.SetDisplayAspect(1);
        scene.SetBoardFacingDegrees(0);
        scene.ShowWaterGarden();
        Require(!scene.SetWaterStickTip(new(.5, .5), now) && !scene.WaterGardenSwishAudioActive,
            "Uncalibrated water accepted a swish.");
        double inset = RegisterBoard(reveal: false);
        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, 256, 256, 96);
        await scene.EnsureBoardArtworkResourcesAsync(device, BoardScreen.WaterGarden);
        Draw();
        now += TimeSpan.FromMilliseconds(20);
        Require(!scene.SetWaterStickTip(CameraPoint(.5, .5), now) && !scene.WaterGardenSwishAudioActive,
            "The moving entrance view accepted a swish.");
        now += TimeSpan.FromSeconds(WaterGardenView.EntranceDurationSeconds + .1);
        Contact();
        Require(changes.SequenceEqual([true]), "Accepted water contact did not start exactly one swish.");
        int started = published;
        long ripples = scene.GetWaterGardenDiagnostics().InputCount;
        for (int frame = 0; frame < 20; frame++) Contact();
        Require(published == started && scene.GetWaterGardenDiagnostics().InputCount == ripples,
            "A resting marker restarted audio or pumped extra ripples.");
        Require(!scene.WaterGardenAudioActive,
            "Marker contact started fountain audio before newly emitted water was rendered.");
        var lastContact = now;
        scene.SetWaterStickTip(null, now.AddMilliseconds(-1));
        Require(scene.WaterGardenSwishAudioActive, "An out-of-order missing frame canceled current contact.");
        scene.SetWaterStickTip(null, now);
        Stopped("Explicit contact loss");
        Require(!scene.SetWaterStickTip(CameraPoint(.5, .5), lastContact) && !scene.WaterGardenSwishAudioActive,
            "A duplicate observation restarted a lost swish.");

        Contact();
        int beforeExpiry = changes.Count;
        var expiredFrame = now;
        now += TimeSpan.FromMilliseconds(251);
        Require(!scene.WaterGardenSwishAudioActive, "A delayed callback could start expired marker audio.");
        scene.TickWaterGarden(now);
        Require(changes.Count == beforeExpiry + 1 && !changes[^1], "Expiry did not publish a swish stop.");
        Require(!scene.SetWaterStickTip(CameraPoint(.5, .5), expiredFrame) && !scene.WaterGardenSwishAudioActive,
            "A stale camera frame revived a swish.");
        Require(!scene.SetWaterStickTip(CameraPoint(.5, .5), now.AddSeconds(1)) &&
                !scene.SetWaterStickTip(new(double.NaN, 0), now) && !scene.WaterGardenSwishAudioActive,
            "Future or invalid camera input started a swish.");

        Reject(.04, .22, "Sand outside the water silhouette");
        Reject(.75, .90, "The control row");
        var side = WaterGardenView.Project(new(0, .5f, -WaterGardenView.WaterDepth * .5f), 1);
        Reject(side.X, side.Y, "The front vertical water cutaway");
        var isRock = typeof(SceneCompositor).GetMethod("IsWaterGardenRock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<double, double, bool>>(scene);
        Vector2? rock = null;
        for (int y = 15; y < 80 && rock is null; y++)
        for (int x = 5; x < 95; x++)
            if (isRock(x / 100.0, y / 100.0) && isRock(x / 100.0 - .002, y / 100.0) &&
                isRock(x / 100.0 + .002, y / 100.0) && WaterGardenView.ContainsWater(new(x / 100f, y / 100f)))
            { rock = new(x / 100f, y / 100f); break; }
        Require(rock is not null, "The fixture could not locate an opaque rock over the water.");
        Reject(rock!.Value.X, rock.Value.Y, "An opaque rock");

        Contact();
        scene.SetBlackOutput(true);
        Stopped("Black output");
        scene.SetBlackOutput(false);
        Contact();
        scene.ShowCalibrationTarget(0, false);
        Stopped("Calibration");
        scene.ShowCalibrationTarget(-1, false);
        Contact();
        scene.ShowBoardMenu();
        Stopped("Navigation before another draw");
        Require(pending.All(callback => !callback()), "Delayed callbacks revived contact after navigation.");
        pending.Clear();
        scene.ShowWaterGarden();
        SettleEntrance();
        Contact();
        scene.SetWaterStickTip(null, now);
        Require(scene.ActivateWaterGardenButton("water-drawer-open"), "Could not open the reset controls.");
        now += BoardSession.WaterGardenDrawerOpeningDuration;
        scene.TickWaterGarden(now);
        var beforeReset = now;
        Require(scene.ActivateWaterGardenButton("water-garden-reset") && !scene.WaterGardenSwishAudioActive &&
                !scene.SetWaterStickTip(CameraPoint(.5, .5), beforeReset),
            "RESET retained contact or accepted pre-reset work.");
        Contact();
        scene.ClearBoardMediaClip();
        Stopped("Lost registration / camera outage");
        inset = RegisterBoard(reveal: true);
        now += TimeSpan.FromMilliseconds(20);
        Require(!scene.SetWaterStickTip(CameraPoint(.5, .5), now) && !scene.WaterGardenSwishAudioActive,
            "Calibration reveal accepted a swish.");
        now += TimeSpan.FromSeconds(5);
        scene.TickWaterGarden(now);
        Contact();
        scene.Dispose();
        Stopped("Disposal");

        using var player = new DisplaySoundPlayer("Water Garden swish muted verification", 0);
        player.ConfigureOutput("Unavailable display", null, true);
        player.EnsureLooping(WaterGardenSwishSound);
        Require(player.Status is { Enabled: false, ActiveClip: null, PlayRequestedAt: null },
            "An absent display endpoint started or redirected swish playback.");
        player.ConfigureOutput("Unavailable display", null, false);
        player.EnsureLooping(WaterGardenSwishSound);
        Require(player.Status is { Enabled: false, ActiveClip: null, IsLooping: false },
            "Disabled display audio started a swish loop.");
        return new { passed = true, transitions = changes.Count, stationaryContactContinuous = true,
            sameWaterHitTesting = true, sourceOrderingPreserved = true, staleContactStops = true,
            navigationAndCalibrationStop = true, resetRejectsOldFrames = true,
            delayedCallbacksReadCurrentState = true, fountainRenderedStartUnchanged = true,
            missingEndpointNeverFallsBack = true, hardwareUnchanged = true };

        void Contact()
        {
            now += TimeSpan.FromMilliseconds(20);
            Require(scene.SetWaterStickTip(CameraPoint(.5, .5), now) && scene.WaterGardenSwishAudioActive,
                "A fresh marker over open water did not request swish playback.");
        }
        void Stopped(string cause) => Require(!scene.WaterGardenSwishAudioActive && changes.Count > 0 && !changes[^1],
            cause + " left the swish request active or omitted its stop event.");
        void Reject(double u, double v, string cause)
        {
            Contact();
            now += TimeSpan.FromMilliseconds(20);
            Require(!scene.SetWaterStickTip(CameraPoint(u, v), now), cause + " accepted water input.");
            Stopped(cause);
        }
        PixelPoint CameraPoint(double u, double v) =>
            new(inset / 2 + u * (1 - inset), inset / 2 + v * (1 - inset));
        void Draw()
        {
            using var drawing = target.CreateDrawingSession();
            scene.Draw(drawing, 256, 256, preview: false, runningSlowly: false);
        }
        void SettleEntrance()
        {
            Draw();
            now += TimeSpan.FromSeconds(WaterGardenView.EntranceDurationSeconds + .1);
        }
        double RegisterBoard(bool reveal)
        {
            scene.SetBoardSetup(true);
            Vector2[] corners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            Point2[] unit = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            var map = Homography.FromFourPoints(unit, unit);
            if (reveal)
            {
                scene.ShowBoardCalibrationSpot(SceneCompositor.BoardCalibrationSpotCount - 1);
                return scene.CompleteBoardSetup(corners, map);
            }
            double safetyInset = scene.SetDetectedBoardGrid(corners, map);
            scene.SetBoardSetup(false);
            return safetyInset;
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
