#if DEBUG
using System.Numerics;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.App.Projection.WaterGarden;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // A synthetic clock models slow first-use resource preparation without a
    // sleep, camera, visible window or dependency on the machine's render speed.
    private async Task<object> VerifyWaterGardenIntroTimingAsync()
    {
        var origin = DateTimeOffset.UtcNow;
        var now = origin;
        var preparationDelay = TimeSpan.FromMilliseconds(400);
        bool delayObserved = false, initialCameraWasWide = false;
        SceneCompositor? observedScene = null;
        using var scene = new SceneCompositor(waterClock: Clock, boardRevealClock: () => now);
        observedScene = scene;
        scene.SetDisplayAspect(1);
        scene.SetBoardFacingDegrees(0);
        scene.SetBoardSetup(true);
        Vector2[] corners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        Point2[] unit = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        scene.SetDetectedBoardGrid(corners, Homography.FromFourPoints(unit, unit));
        scene.SetBoardSetup(false);
        scene.ShowWaterGarden();
        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, 256, 256, 96);
        await scene.EnsureBoardArtworkResourcesAsync(device, BoardScreen.WaterGarden);

        Draw();
        // A buggy implementation that never samples a fresh clock during its
        // first render still observes the preparation delay here, then fails
        // the anchor check instead of depending on actual wall-clock timing.
        var afterPreparation = Clock();
        var simulation = Read<WaterGardenSimulation>(scene, "_waterSimulation");
        var started = Read<DateTimeOffset>(scene, "_waterIntroStartedAt");
        Require(delayObserved && initialCameraWasWide && started == origin + preparationDelay &&
                started == afterPreparation &&
                Read<DateTimeOffset>(scene, "_waterAdvancedAt") == started &&
                Read<float>(scene, "_waterEntranceProgress") == 0 &&
                !Read<bool>(scene, "_waterIntroPending") &&
                simulation.GetDiagnostics().SimulationSteps == 0,
            "The first water frame counted resource preparation as camera or simulation time.");

        Draw();
        Require(Read<DateTimeOffset>(scene, "_waterIntroStartedAt") == started &&
                Read<float>(scene, "_waterEntranceProgress") == 0 &&
                simulation.GetDiagnostics().SimulationSteps == 0,
            "A repeated first-frame draw restarted the entrance or advanced the simulation.");

        now = started + TimeSpan.FromMilliseconds(300);
        Draw();
        float progress = Read<float>(scene, "_waterEntranceProgress");
        Require(Math.Abs(progress - .1f) < 1e-6f &&
                Read<DateTimeOffset>(scene, "_waterIntroStartedAt") == started,
            $"The entrance did not progress from its fresh start: progress={progress:G9}.");
        long steps = simulation.GetDiagnostics().SimulationSteps;
        Draw();
        Require(Read<float>(scene, "_waterEntranceProgress") == progress &&
                Read<DateTimeOffset>(scene, "_waterIntroStartedAt") == started &&
                simulation.GetDiagnostics().SimulationSteps == steps,
            "A same-clock draw changed entrance progress or ran simulation work twice.");

        now = started + TimeSpan.FromSeconds(WaterGardenView.EntranceDurationSeconds);
        Draw();
        Require(Read<float>(scene, "_waterEntranceProgress") == 1 &&
                Read<DateTimeOffset>(scene, "_waterIntroStartedAt") == started,
            "The entrance did not settle after its full duration from the fresh start.");
        return new
        {
            passed = true,
            simulatedPreparationMilliseconds = preparationDelay.TotalMilliseconds,
            firstCameraProgress = 0,
            progressAfter300Milliseconds = progress,
            settledAfterSeconds = WaterGardenView.EntranceDurationSeconds,
            preparationExcludedFromEntranceAndSimulation = true,
            sameClockDrawDoesNotRestartOrAdvance = true,
            privateSceneOnly = true,
            cameraOrOutputCreated = false
        };

        DateTimeOffset Clock()
        {
            if (!delayObserved && observedScene is not null &&
                    Field(observedScene, "_waterSimulation").GetValue(observedScene) is WaterGardenSimulation water)
            {
                delayObserved = true;
                initialCameraWasWide = Read<float>(water, "_entranceProgress") == 0;
                now += preparationDelay;
            }
            return now;
        }
        void Draw()
        {
            using var drawing = target.CreateDrawingSession();
            scene.Draw(drawing, 256, 256, preview: false, runningSlowly: false);
        }
        static FieldInfo Field(object owner, string name) => owner.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing timing verification field: " + name);
        static T Read<T>(object owner, string name) => (T)(Field(owner, name).GetValue(owner)
            ?? throw new InvalidOperationException("Missing timing verification value: " + name));
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
