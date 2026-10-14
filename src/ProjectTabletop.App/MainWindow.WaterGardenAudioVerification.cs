#if DEBUG
using System.Numerics;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Media;
using ProjectTabletop.App.Projection;
using ProjectTabletop.App.Projection.WaterGarden;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Private offscreen scene and real fountain simulation; no MainWindow state,
    // hardware discovery, player endpoint, camera or visible output is touched.
    private async Task<object> VerifyWaterGardenAudioAsync()
    {
        var now = DateTimeOffset.UtcNow;
        using var scene = new SceneCompositor(waterClock: () => now, boardRevealClock: () => now);
        var changes = new List<bool>();
        var pending = new Queue<Action>();
        var dispatched = new List<bool>();
        scene.WaterGardenAudioChanged += () =>
        {
            changes.Add(scene.WaterGardenAudioActive);
            pending.Enqueue(() => dispatched.Add(scene.WaterGardenAudioActive));
        };

        scene.ShowWaterGarden();
        Require(!scene.WaterGardenAudioActive && changes.Count == 0, "An uncalibrated preview started audio.");
        RegisterBoard(reveal: false);
        scene.TickWaterGarden(now);
        Require(!scene.WaterGardenAudioActive && changes.Count == 0,
            "Registration started audio before the fountain was rendered.");
        now += TimeSpan.FromSeconds(10);
        scene.TickWaterGarden(now);
        Require(!scene.WaterGardenAudioActive && changes.Count == 0,
            "Elapsed wall time started audio before the fountain existed.");
        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, 256, 256, 96);
        await scene.EnsureBoardArtworkResourcesAsync(device, BoardScreen.WaterGarden);
        Require(!scene.WaterGardenAudioActive, "Loading artwork started fountain audio.");
        Draw();
        Require(!scene.WaterGardenAudioActive && Simulation().GetFountainDiagnostics().ParticleCount == 0,
            "The first dry fountain frame started audio.");
        StartRenderedFlow();
        Require(changes.SequenceEqual([true]), "The first rendered water did not start audio exactly once.");
        var beforePreview = scene.GetWaterGardenDiagnostics();
        using (var drawing = target.CreateDrawingSession())
            scene.DrawWaterGardenPreview(drawing, 256, 256);
        var afterPreview = scene.GetWaterGardenDiagnostics();
        Require(afterPreview.RenderedFrames == beforePreview.RenderedFrames &&
                afterPreview.PreviewFrameReuses == beforePreview.PreviewFrameReuses + 1 && changes.Count == 1,
            "A cached laptop preview changed the fountain audio request.");
        for (int i = 0; i < 20; i++)
        {
            now += TimeSpan.FromMilliseconds(16);
            scene.TickWaterGarden(now);
            _ = scene.GetWaterGardenDiagnostics();
        }
        Draw();
        Require(changes.Count == 1, "Repeated water updates restarted the audio request.");
        Require(scene.ActivateWaterGardenButton("water-drawer-open"), "Could not open the water drawer.");
        now += BoardSession.WaterGardenDrawerOpeningDuration;
        scene.TickWaterGarden(now);
        Require(scene.ActivateWaterGardenButton("water-garden-reset") && !scene.WaterGardenAudioActive &&
                changes.SequenceEqual([true, false]),
            "RESET left audio playing while the fountain was dry.");
        StartRenderedFlow();

        scene.SetBlackOutput(true);
        Require(!scene.WaterGardenAudioActive && changes[^1] == false, "Black output left fountain audio active.");
        scene.SetBlackOutput(false);
        Require(!scene.WaterGardenAudioActive, "Restoring the board started audio before new water appeared.");
        StartRenderedFlow();
        scene.ShowCalibrationTarget(0, false);
        Require(!scene.WaterGardenAudioActive && changes[^1] == false, "Calibration left fountain audio active.");
        scene.ShowCalibrationTarget(-1, false);
        Require(!scene.WaterGardenAudioActive, "Ending calibration started audio before new water appeared.");
        StartRenderedFlow();

        scene.ShowBoardMenu();
        Require(!scene.WaterGardenAudioActive && changes[^1] == false,
            "Navigation did not stop fountain audio before rendering the destination.");
        scene.ShowWaterGarden();
        Require(!scene.WaterGardenAudioActive, "Returning to Water Garden skipped its dry opening.");
        StartRenderedFlow();
        scene.ShowBoardMenu();
        while (pending.TryDequeue(out var callback)) callback();
        Require(dispatched.Count > 0 && dispatched.All(active => !active),
            "A delayed callback could restart fountain audio after navigation.");

        scene.ShowWaterGarden();
        StartRenderedFlow();
        scene.ClearBoardMediaClip();
        Require(!scene.WaterGardenAudioActive && changes[^1] == false, "Removing registration left fountain audio active.");
        RegisterBoard(reveal: true);
        scene.TickWaterGarden(now);
        Require(!scene.WaterGardenAudioActive, "Audio began before the calibration reveal finished.");
        now += TimeSpan.FromSeconds(5);
        scene.TickWaterGarden(now);
        Require(!scene.WaterGardenAudioActive, "Completing the reveal started audio before new water appeared.");
        StartRenderedFlow();
        scene.Dispose();
        Require(!scene.WaterGardenAudioActive && changes[^1] == false, "Disposal left fountain audio active.");

        using var player = new DisplaySoundPlayer("Water Garden muted verification", 0);
        player.ConfigureOutput("Unavailable display", null, true);
        player.EnsureLooping(WaterGardenFountainSound);
        Require(player.Status is { Enabled: false, ActiveClip: null, PlayRequestedAt: null },
            "An absent display endpoint fell back to another audio device.");
        player.ConfigureOutput("Unavailable display", null, false);
        player.EnsureLooping(WaterGardenFountainSound);
        Require(player.Status is { Enabled: false, ActiveClip: null, IsLooping: false }, "Disabled audio started a loop.");

        return new { passed = true, changes = changes.Count,
            visibleCalibratedBoardOnly = true, navigationStopsImmediately = true,
            waitsForRenderedWater = true, wallTimeAndArtworkDoNotStartAudio = true,
            resetWaitsForNewWater = true, repeatedUpdatesDoNotRestart = true,
            cachedPreviewDoesNotRestart = true,
            staleCallbacksReadCurrentState = true,
            calibrationAndRevealSilence = true, disposalStops = true,
            missingEndpointNeverFallsBack = true, disabledPlaybackSilent = true,
            hardwareUnchanged = true };

        void Draw(bool isProjectorFrame = false)
        {
            using var drawing = target.CreateDrawingSession();
            scene.Draw(drawing, 256, 256, preview: false, runningSlowly: false,
                isProjectorFrame: isProjectorFrame);
        }

        WaterGardenSimulation Simulation() =>
            (WaterGardenSimulation)(typeof(SceneCompositor).GetField("_waterSimulation",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene) ??
                throw new InvalidOperationException("Rendering did not create the fountain simulation."));

        void StartRenderedFlow()
        {
            int before = changes.Count;
            Draw();
            var simulation = Simulation();
            Require(!scene.WaterGardenAudioActive && simulation.GetFountainDiagnostics().ParticleCount == 0,
                "A recreated/reset fountain skipped its silent dry state.");
            // Advance real source physics without shading: audio must wait for
            // the next completed frame, even after actual particles exist.
            for (int i = 0; i < 9; i++) simulation.Advance(.1);
            Require(simulation.GetFountainDiagnostics().ParticleCount == 0 && !scene.WaterGardenAudioActive,
                "The source's initial dry second started fountain audio.");
            for (int i = 0; i < 20 && simulation.GetFountainDiagnostics().ParticleCount == 0; i++)
                simulation.Advance(.1);
            Require(simulation.GetFountainDiagnostics().ParticleCount > 0 && !scene.WaterGardenAudioActive,
                "Audio started before newly emitted water was drawn, or the source never emitted water.");
            now += TimeSpan.FromMilliseconds(20);
            Draw(isProjectorFrame: true);
            Require(scene.WaterGardenAudioActive && changes.Count == before + 1 && changes[^1],
                "The first rendered water did not start audio exactly once.");
        }

        void RegisterBoard(bool reveal)
        {
            scene.SetBoardSetup(true);
            Vector2[] corners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            Point2[] unit = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            var map = Homography.FromFourPoints(unit, unit);
            if (reveal)
            {
                scene.ShowBoardCalibrationSpot(SceneCompositor.BoardCalibrationSpotCount - 1);
                scene.CompleteBoardSetup(corners, map);
            }
            else
            {
                scene.SetDetectedBoardGrid(corners, map);
                scene.SetBoardSetup(false);
            }
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
