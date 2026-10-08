#if DEBUG
using System.Numerics;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.App.Projection.Football;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Private scenes exercise actual Win2D output and calibrated camera mapping
    // without starting the user's camera, changing profiles or opening a projector.
    private async Task<object> VerifyFootballAsync()
    {
        string directory = Path.Combine(_appDataDirectory, "FootballVerification", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(directory);
        var images = new List<string>();
        var device = CanvasDevice.GetSharedDevice();
        var now = MonotonicClock.UtcNow;
        int renderedFrames = 0;
        using var scene = MakeScene(16.0 / 9, out double inset);
        // The pitch is built off the render path; the board shows Loading until then.
        await scene.EnsureBoardArtworkResourcesAsync(device, BoardScreen.Football);
        using var target = new CanvasRenderTarget(device, 1600, 900, 96);
        Draw(scene, target);
        Check(scene.FootballState.Phase == FootballPhase.WaitingForPlayers, "Football started without a player.");
        var field = scene.FootballFieldBounds();
        var playerCamera = Camera(field.X + field.Width * .22, field.Y + field.Height * .5, inset);
        Check(scene.TryMapFootballCameraPoint(playerCamera, out var uv) && Math.Abs(uv.X - .22) < .0001 &&
            Math.Abs(uv.Y - .5) < .0001, "Field letterboxing and camera inverse disagreed.");
        Check(!scene.TryMapFootballCameraPoint(Camera(.5, .04, inset), out _), "Scoreboard accepted a playing point.");
        for (int frame = 0; frame < 155; frame++)
        {
            now += TimeSpan.FromMilliseconds(16);
            scene.SetFootballPlayerCameraPoint(0, playerCamera, now);
            Draw(scene, target);
        }
        Check(scene.FootballState.Phase == FootballPhase.Playing && scene.FootballState.Kickers.All(k => k.Present),
            "Fresh calibrated input did not start a human-versus-AI match.");
        await Save(target, "football-playing");
        Check(scene.GetHandAcquisitionContext(now) is { ContinuousSearchPolygon: null },
            "Black-bar play searched the pitch for hands.");
        scene.SetFootballFingerInput(0, true);
        var context = scene.GetHandAcquisitionContext(now);
        Check(context?.ContinuousSearchPolygon?.Length == 4, "The pitch did not allow finger acquisition away from buttons.");
        var hold = scene.GetHoldButtonContext(now);
        Check(hold is not null && hold.EnabledHoldIds.SetEquals(["football-exit", "football-reset", "football-mode"]),
            "Football controls did not expose caption-based hold references.");
        var beforeDrop = scene.FootballState.BallPosition;
        now += TimeSpan.FromMilliseconds(300);
        Draw(scene, target);
        Check(scene.FootballState.Phase == FootballPhase.WaitingForPlayers && !scene.FootballState.Kickers[0].Present,
            "A missing marker kept the match running.");
        var paused = scene.FootballState.BallPosition;
        now += TimeSpan.FromMilliseconds(40);
        scene.SetFootballPlayerCameraPoint(0, playerCamera, now.AddSeconds(-1));
        scene.SetFootballPlayerCameraPoint(0, playerCamera, now.AddSeconds(1));
        Draw(scene, target);
        Check(scene.FootballState.Phase == FootballPhase.WaitingForPlayers && scene.FootballState.BallPosition == paused,
            "Stale/future input restarted a paused game.");
        Check(scene.ActivateFootballButton("football-mode") && scene.FootballState.Mode == FootballMode.TwoHumans,
            "The projected two-player control failed.");
        for (int frame = 0; frame < 120; frame++)
        {
            now += TimeSpan.FromMilliseconds(16);
            scene.SetFootballPlayerCameraPoint(0, playerCamera, now);
            scene.SetFootballPlayerCameraPoint(1, Camera(field.X + field.Width * .78, field.Y + field.Height * .5, inset), now);
            Draw(scene, target);
        }
        Check(scene.FootballState.Phase == FootballPhase.Playing && scene.FootballState.Kickers.All(k => !k.IsAi && k.Present),
            "Two fresh markers did not start two-player play.");
        scene.SetFootballStyle(0, FootballKickerStyle.Pan);
        scene.SetFootballStyle(1, FootballKickerStyle.Boot);
        Draw(scene, target);
        await Save(target, "football-pan-and-boot");
        Check(scene.ActivateFootballButton("football-reset") && scene.FootballState.Score1 == 0 &&
            scene.FootballState.Score2 == 0 && scene.FootballState.Phase == FootballPhase.WaitingForPlayers,
            "Reset did not require fresh input for a new match.");
        now += TimeSpan.FromMilliseconds(16);
        scene.SetBoardFacingDegrees(90);
        Draw(scene, target);
        field = scene.FootballFieldBounds();
        var rotated = Camera(field.X + field.Width * .2, field.Y + field.Height * .65, inset, true);
        Check(scene.TryMapFootballCameraPoint(rotated, out uv) && Math.Abs(uv.X - .2) < .0001 && Math.Abs(uv.Y - .65) < .0001,
            "Quarter-turn board orientation changed football aiming.");
        scene.SetBlackOutput(true);
        now += TimeSpan.FromMilliseconds(16);
        scene.SetFootballPlayerCameraPoint(0, rotated, now);
        Check(!scene.TryMapFootballCameraPoint(rotated, out _), "Black output still accepted football camera input.");

        using var renderer = new FootballRenderer(device);
        await renderer.EnsurePitchAsync();
        using var raw = new CanvasRenderTarget(device, 1824, 1132, 96);
        var snapshot = new FootballGame().Snapshot with
        {
            Phase = FootballPhase.Playing,
            Kickers = new[]
            {
                new FootballKickerSnapshot(0, new(-.48f, .17f), Vector2.Zero, true, FootballKickerStyle.Car, false),
                new FootballKickerSnapshot(1, new(.5f, -.17f), Vector2.Zero, true, FootballKickerStyle.Glove, true)
            }
        };
        RenderRaw(snapshot with { BallPosition = new(-.5f, -.35f) });
        byte[] background = raw.GetPixelBytes();
        RenderRaw(snapshot with { BallHeight = .20f, BallRotation = Quaternion.CreateFromYawPitchRoll(.45f, .7f, .3f) });
        byte[] airborne = raw.GetPixelBytes();
        int shadowPixels = 0;
        for (int y = 650; y < 735; y++)
        for (int x = 960; x < 1070; x++)
        {
            int offset = (y * 1824 + x) * 4;
            if (background[offset + 1] - airborne[offset + 1] > 6) shadowPixels++;
        }
        Check(shadowPixels > 350, "Airborne ball lacked a visible, separated directional shadow on the turf.");
        await Save(raw, "football-airborne-shadow");
        RenderRaw(snapshot);
        await Save(raw, "football-car-and-glove");
        using (var thumbnail = new CanvasRenderTarget(device, 1200, 600, 96))
        {
            using (var drawing = thumbnail.CreateDrawingSession()) renderer.DrawThumbnail(drawing, new Rect(0, 0, 1200, 600));
            await Save(thumbnail, "football-thumbnail");
        }
        foreach (double aspect in new[] { 1.0, 9.0 / 16 })
        {
            using var fitted = MakeScene(aspect, out _);
            await fitted.EnsureBoardArtworkResourcesAsync(device, BoardScreen.Football);
            using var output = new CanvasRenderTarget(device, (float)(900 * aspect), 900, 96);
            Draw(fitted, output);
            var fittedField = fitted.FootballFieldBounds();
            Check(Math.Abs(fittedField.Width * aspect / fittedField.Height - FootballGame.Width) < .0001,
                "A changed board aspect stretched the pitch or ball.");
            await Save(output, aspect == 1 ? "football-square" : "football-portrait");
        }
        return new { passed = true, renderedFrames, shadowPixels, calibratedInput = true,
            staleAndFutureRejected = true, lossPausesPlay = true, twoPlayers = true,
            quarterTurnMapping = true, captionControls = true, continuousFingerAcquisition = true, directory, images };

        SceneCompositor MakeScene(double aspect, out double safetyInset)
        {
            var result = new SceneCompositor(blackjackClock: () => now, footballClock: () => now);
            result.SetDisplayAspect(aspect);
            result.SetBoardFacingDegrees(0);
            result.SetBoardSetup(true);
            Vector2[] corners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            Point2[] unit = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            Point2[] camera = [new(0, 0), new(1000, 0), new(1000, 1000), new(0, 1000)];
            safetyInset = result.SetDetectedBoardGrid(corners, Homography.FromFourPoints(camera, unit));
            result.SetBoardSetup(false);
            result.ShowFootball();
            return result;
        }
        static PixelPoint Camera(double x, double y, double padding, bool rotated = false)
        {
            if (rotated) (x, y) = (1 - y, x);
            return new(1000 * (padding / 2 + x * (1 - padding)), 1000 * (padding / 2 + y * (1 - padding)));
        }
        void Draw(SceneCompositor current, CanvasRenderTarget output)
        {
            using var drawing = output.CreateDrawingSession();
            current.Draw(drawing, (float)output.Size.Width, (float)output.Size.Height, preview: false, runningSlowly: false);
            renderedFrames++;
        }
        void RenderRaw(FootballSnapshot state)
        {
            using var drawing = raw.CreateDrawingSession();
            drawing.Clear(Windows.UI.Color.FromArgb(255, 13, 30, 25));
            renderer.Draw(drawing, new Rect(112, 66, 1600, 1000), state);
            renderedFrames++;
        }
        async Task Save(CanvasRenderTarget output, string name)
        {
            string path = Path.Combine(directory, name + ".png");
            await output.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(path);
        }
        static void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
        }
    }
}
#endif
