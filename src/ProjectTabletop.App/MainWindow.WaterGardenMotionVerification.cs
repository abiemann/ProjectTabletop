#if DEBUG
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.App.Projection.WaterGarden;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // PNG frames retain the actual renderer and ambient water appearance. The
    // frame clock is synthetic, so encoding/storage speed cannot alter the wake.
    private async Task<object> CaptureWaterGardenMotionAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen,
            _scene.HasBoardMediaClip, _scene.GetBoardFacingDegrees());
        const int width = 1200, height = 900, fps = 24, frameCount = 192;
        const int strokeStart = 24, strokeEnd = 84;
        var origin = MonotonicClock.UtcNow.AddMinutes(1);
        var now = origin;
        string directory = Path.Combine(_appDataDirectory, "WaterGardenMotion", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string metadataPath = Path.Combine(directory, "capture.json");
        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, width, height, 96);
        using var scene = new SceneCompositor(blackjackClock: () => now, waterClock: () => now);
        scene.SetDisplayAspect(width / (double)height);
        scene.SetBoardFacingDegrees(0);
        scene.SetBoardSetup(true);
        Vector2[] corners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        Point2[] unit = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        Point2[] camera = [new(0, 0), new(1000, 0), new(1000, 1000), new(0, 1000)];
        double inset = scene.SetDetectedBoardGrid(corners, Homography.FromFourPoints(camera, unit));
        scene.SetBoardSetup(false);
        scene.ShowWaterGarden();
        await scene.EnsureBoardArtworkResourcesAsync(device, BoardScreen.WaterGarden);
        var samples = new List<object>();
        var checkpoints = new List<object>();
        int acceptedSamples = 0;
        double drawMilliseconds = 0, pngMilliseconds = 0;
        var totalTimer = Stopwatch.StartNew();
        for (int frame = 0; frame < frameCount; frame++)
        {
            now = origin + TimeSpan.FromSeconds(frame / (double)fps);
            if (frame >= strokeStart && frame < strokeEnd && frame % 2 == 0)
            {
                // One central surface-plane figure-eight at 12 observations/second.
                // Perspective projection keeps it inside the visible water and
                // away from the raised rim and near/far rock silhouettes. At this
                // speed consecutive samples exercise the compositor's real
                // short-stroke interpolation, without its jump/loss shortcut.
                // Passing close to the middle duck sends waves under the flock;
                // the final quiet interval records their buoyant settling.
                double phase = 2 * Math.PI * (frame - strokeStart) / (strokeEnd - strokeStart);
                var surface = new Vector2((float)(.5 + .25 * Math.Sin(phase)),
                    (float)(.5 + .23 * Math.Sin(2 * phase)));
                var board = WaterGardenView.SurfaceToScreen(surface);
                double u = board.X, v = board.Y;
                var raw = new PixelPoint(1000 * (inset / 2 + u * (1 - inset)),
                    1000 * (inset / 2 + v * (1 - inset)));
                if (!scene.SetWaterStickTip(raw, now))
                    throw new InvalidOperationException($"The motion capture rejected fresh mapped eye sample at frame {frame}.");
                acceptedSamples++;
                samples.Add(new { frame, seconds = frame / (double)fps, boardU = u, boardV = v,
                    surfaceU = surface.X, surfaceV = surface.Y,
                    cameraX = raw.X, cameraY = raw.Y });
            }
            else if (frame == strokeEnd)
            {
                scene.SetWaterStickTip(null, now);
                if (scene.GetWaterGardenDiagnostics().TipVisible)
                    throw new InvalidOperationException("The motion capture's lost-tip interval kept its pointer active.");
            }
            var timer = Stopwatch.StartNew();
            using (var drawing = target.CreateDrawingSession())
                scene.Draw(drawing, width, height, preview: false, runningSlowly: false);
            drawMilliseconds += timer.Elapsed.TotalMilliseconds;
            if (frame == 0 || frame == strokeStart || frame == 60 || frame == strokeEnd - 1 || frame == frameCount - 1)
            {
                var water = typeof(SceneCompositor).GetField("_waterSimulation", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(scene) as WaterGardenSimulation ?? throw new InvalidOperationException("The motion capture lost its GPU water simulation.");
                checkpoints.Add(new { frame, seconds = frame / (double)fps,
                    diagnostics = scene.GetWaterGardenDiagnostics(), ducks = water.GetDuckStates() });
            }
            timer.Restart();
            await target.SaveAsync(Path.Combine(directory, $"frame-{frame:D4}.png"), CanvasBitmapFileFormat.Png);
            pngMilliseconds += timer.Elapsed.TotalMilliseconds;
        }
        bool hardwareUnchanged = ReferenceEquals(liveOutput, _output) &&
            liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen,
                _scene.HasBoardMediaClip, _scene.GetBoardFacingDegrees());
        if (!hardwareUnchanged || acceptedSamples != 30 || scene.CurrentBoardScreen != BoardScreen.WaterGarden)
            throw new InvalidOperationException("Motion capture changed live hardware state or failed to capture all 30 eye-tip observations.");
        var result = new
        {
            passed = true, directory, metadataPath, framePattern = "frame-%04d.png", firstFrame = 0,
            width, height, fps, frameCount, durationSeconds = frameCount / (double)fps,
            ambientEnabled = true, inputRateHz = 12, acceptedSamples,
            quietSeconds = 1.0, strokeSeconds = 2.5, decaySeconds = 4.5,
            inputPath = "surface u=.5+.25*sin(phase), v=.5+.23*sin(2*phase), phase=0..2*pi; projected through the garden camera",
            samples, checkpoints, drawMilliseconds, pngMilliseconds,
            elapsedMilliseconds = totalTimer.Elapsed.TotalMilliseconds,
            timingScope = "GPU draw submission and PNG save/readback measured separately; synthetic 24 fps clock; not projector frame pacing.",
            liveHardwareUnchanged = hardwareUnchanged
        };
        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result;
    }
}
#endif
