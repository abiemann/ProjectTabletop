#if DEBUG
using System.Numerics;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Isolated native drawing and simulated input. No camera, microphone, window
    // or running session is acquired or altered by this verification.
    private async Task<object> VerifyDuckHuntFlightAsync()
    {
        string directory = Path.Combine(_appDataDirectory, "DuckHuntFlightVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<string>();
        var results = new List<object>();
        int assertions = 0;
        foreach (var dimensions in new[]
        {
            (Name: "landscape", Width: 1920, Height: 1080),
            (Name: "square", Width: 1000, Height: 1000),
            (Name: "portrait", Width: 1080, Height: 1920)
        })
        {
            var now = MonotonicClock.UtcNow;
            double aspect = dimensions.Width / (double)dimensions.Height;
            using var scene = new SceneCompositor(blackjackClock: () => now, boardRevealClock: () => now);
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), dimensions.Width, dimensions.Height, 96);
            var board = (BoardSession)typeof(SceneCompositor).GetField("_boardSession",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
            scene.SetDisplayAspect(aspect);
            scene.SetBoardSetup(true);
            scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
                Homography.FromFourPoints([new(0, 0), new(dimensions.Width, 0), new(dimensions.Width, dimensions.Height), new(0, dimensions.Height)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            scene.SetBoardSetup(false);
            scene.ShowDuckHuntHunt();
            await scene.EnsureBoardArtworkResourcesAsync(target.Device, BoardScreen.DuckHunt);
            now = MonotonicClock.UtcNow.AddSeconds(1);
            scene.SetDuckHuntAudioStatus(true, "Isolated microphone fixture");
            Draw();
            Check(scene.DuckHuntArtworkReady && scene.DuckHuntBoardReady && scene.DuckHuntInputReady,
                "The HUNT flight artwork or calibrated shot input is unavailable.");
            Check(scene.HuntFlightState is { Phase: DuckHuntFlightPhase.Ready, Paused: false, Score: 0, BirdsLaunched: 0 },
                "HUNT did not wait for the shot-only START target.");
            Check(!scene.ActivateDuckHuntAt(.5, .5) &&
                !board.Buttons.Any(button => button.Bounds.Contains(.5, .5)),
                "An ordinary click or hand button can start HUNT.");
            var holds = scene.GetHoldButtonContext(now);
            Check(holds is not null && holds.ButtonIds.All(id => id != "duck-hunt-start"),
                "START was included among the hand-held captions.");
            CheckClearOptics("ready");
            CheckOpticalAim(new(.5, .5), "START");
            await Save("start");
            Check(!scene.SubmitDuckHuntShot(null, now) && scene.HuntFlightState.StartVisible,
                "A click without a red dot started the round.");
            now += TimeSpan.FromMilliseconds(1);
            Check(scene.SubmitDuckHuntShot(new(.5, .5), now), "A valid shot on START did not begin HUNT.");
            Draw();
            Check(!scene.HuntFlightState.StartVisible && scene.HuntFlightState.Phase == DuckHuntFlightPhase.Launching,
                "START did not disappear after the opening shot.");
            now += DuckHuntFlightGame.LaunchDelay + TimeSpan.FromMilliseconds(300);
            Draw();
            for (int birdIndex = 1; birdIndex <= DuckHuntFlightGame.BirdsPerRound; birdIndex++)
            {
                var flight = scene.HuntFlightState;
                Check(flight is { Phase: DuckHuntFlightPhase.Flying, Visible: true } && flight.BirdsLaunched == birdIndex,
                    "The next bird was not launched exactly once.");
                CheckClearOptics("flight " + birdIndex);
                var bounds = DuckHuntFlightLayout.SpriteBounds(flight, aspect);
                var aim = new BoardAim(bounds.X + bounds.Width * DuckHuntFlightLayout.BodyCenterU,
                    bounds.Y + bounds.Height * DuckHuntFlightLayout.BodyCenterV);
                Check(DuckHuntFlightLayout.ContainsBird(aim, flight, aspect), "The rendered body centre is not hittable.");
                if (birdIndex == 1)
                {
                    CheckOpticalAim(aim, "flying body");
                    await Save("flying");
                    await CheckWingAnimation(flight);
                }
                Check(scene.SubmitDuckHuntShot(aim, now), "A calibrated shot on the flying bird did not score.");
                byte[] hitVisible = Draw();
                var frozen = scene.HuntFlightState;
                Check(frozen.Phase == DuckHuntFlightPhase.Hit && frozen.Score == birdIndex && frozen.Visible,
                    "The hit frame did not freeze visibly and award one point.");
                if (birdIndex == 1) await Save("hit-visible");
                now += TimeSpan.FromMilliseconds(100);
                byte[] blink = Draw();
                Check(scene.HuntFlightState is { Phase: DuckHuntFlightPhase.Hit, Visible: false } &&
                    scene.HuntFlightState.U == frozen.U && scene.HuntFlightState.V == frozen.V &&
                    scene.HuntFlightState.FlapPhase == frozen.FlapPhase && !hitVisible.AsSpan().SequenceEqual(blink),
                    "The struck bird moved or failed to blink off.");
                if (birdIndex == 1) await Save("hit-hidden");
                now += TimeSpan.FromMilliseconds(100);
                byte[] relit = Draw();
                Check(scene.HuntFlightState.Visible && hitVisible.AsSpan().SequenceEqual(relit),
                    "The blink did not return to the exact frozen frame.");
                now += DuckHuntFlightGame.HitDuration + DuckHuntFlightGame.BetweenBirdsDuration -
                    TimeSpan.FromMilliseconds(200) + TimeSpan.FromMilliseconds(300);
                Draw();
            }
            Check(scene.HuntFlightState is { Phase: DuckHuntFlightPhase.Finished, Score: 10, BirdsLaunched: 10, StartVisible: true },
                "The ten-bird round did not finish with its score and START target.");
            CheckClearOptics("finished");
            await Save("finished");
            now += TimeSpan.FromMilliseconds(1);
            Check(scene.SubmitDuckHuntShot(new(.5, .5), now), "The final START target cannot begin the next round.");
            Draw();
            Check(scene.HuntFlightState is { Phase: DuckHuntFlightPhase.Launching, Score: 0, BirdsLaunched: 0 },
                "The new round retained the previous score or bird count.");
            now += TimeSpan.FromSeconds(90);
            Draw();
            Check(scene.HuntFlightState is { Phase: DuckHuntFlightPhase.Finished, Score: 0, BirdsLaunched: 10 },
                "Escaped birds awarded points or caused more than ten launches.");
            await Save("all-escaped");
            now += TimeSpan.FromMilliseconds(1);
            Check(scene.ActivateDuckHuntAt(.10, .93), "The Hunt up-arrow did not open after the round.");
            now += TimeSpan.FromMilliseconds(300);
            Draw();
            Check(board.Buttons.Select(button => button.Id).SequenceEqual(
                    ["duck-hunt-drawer-close", "duck-hunt-exit", "duck-hunt-back"]),
                "HUNT changed its EXIT/BACK drawer controls.");
            await Save("result-controls");
            Check(board.ActivateButton("duck-hunt-back", now) && scene.CurrentDuckHuntView == DuckHuntView.Menu,
                "BACK could not leave the finished Hunt round.");
            results.Add(new { dimensions.Name, dimensions.Width, dimensions.Height, Score = 10,
                BirdCount = 10, ShotOnlyStart = true, FrozenBlink = true, ClearSceneCandidates = 0 });

            byte[] Draw()
            {
                using (var drawing = target.CreateDrawingSession())
                    scene.Draw(drawing, dimensions.Width, dimensions.Height, preview: false, runningSlowly: false);
                return target.GetPixelBytes();
            }
            async Task Save(string name)
            {
                string path = Path.Combine(directory, dimensions.Name + "-" + name + ".png");
                await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
                images.Add(path);
            }
            void CheckClearOptics(string stage)
            {
                var input = scene.GetDuckHuntInputContext() ?? throw new InvalidOperationException("HUNT has no optical mapping.");
                var detection = new RedDotDetector().Detect(dimensions.Width, dimensions.Height, dimensions.Width * 4,
                    target.GetPixelBytes(), input.CameraToBoard);
                Check(!detection.Detected && detection.CandidateCount == 0,
                    $"{dimensions.Name} {stage} artwork created {detection.CandidateCount} false red-dot candidates.");
            }
            void CheckOpticalAim(BoardAim aim, string stage)
            {
                var input = scene.GetDuckHuntInputContext() ?? throw new InvalidOperationException("HUNT has no optical mapping.");
                var surface = (Homography)typeof(SceneCompositor).GetField("_boardSurfaceMap",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
                var projected = surface.Transform(new Point2(aim.U, aim.V));
                double cx = projected.X * dimensions.Width, cy = projected.Y * dimensions.Height;
                byte[] pixels = target.GetPixelBytes();
                for (int y = Math.Max(0, (int)cy - 6); y <= Math.Min(dimensions.Height - 1, (int)cy + 6); y++)
                for (int x = Math.Max(0, (int)cx - 6); x <= Math.Min(dimensions.Width - 1, (int)cx + 6); x++)
                {
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > 20) continue;
                    int p = (y * dimensions.Width + x) * 4;
                    // Add red and blue-biased camera laser light to the projected
                    // scene. Retain green, so this also probes bright button glare.
                    pixels[p] = (byte)Math.Min(255, pixels[p] + 180);
                    pixels[p + 2] = 255;
                }
                var detection = new RedDotDetector().Detect(dimensions.Width, dimensions.Height, dimensions.Width * 4,
                    pixels, input.CameraToBoard);
                Check(detection.Detected && Math.Abs(detection.BoardX!.Value - aim.U) < .003 &&
                    Math.Abs(detection.BoardY!.Value - aim.V) < .003,
                    $"The additive laser on {stage} did not map back to its board position.");
            }
            async Task CheckWingAnimation(DuckHuntFlightSnapshot snapshot)
            {
                using var sprite = new CanvasRenderTarget(target.Device, dimensions.Width, dimensions.Height, 96);
                byte[]? first = null;
                foreach (int direction in new[] { -1, 1 })
                foreach (double phase in new[] { 0d, Math.PI / 2, Math.PI, 3 * Math.PI / 2 })
                {
                    var pose = snapshot with { U = .5, V = .48, Direction = direction, FlapPhase = phase };
                    using (var drawing = sprite.CreateDrawingSession())
                    {
                        drawing.Clear(Microsoft.UI.Colors.Transparent);
                        drawing.Transform = Matrix3x2.CreateScale(dimensions.Width / 1000f, dimensions.Height / 1000f);
                        scene.DrawDuckHuntFlyingMallard(drawing, pose, aspect);
                    }
                    byte[] pixels = sprite.GetPixelBytes();
                    int opaque = 0;
                    for (int p = 3; p < pixels.Length; p += 4) if (pixels[p] > 128) opaque++;
                    Check(opaque > 200, "The flight artwork is blank or too small to see.");
                    int sampledInk = 0, missedInk = 0, sampledHits = 0, emptyHits = 0;
                    var bounds = DuckHuntFlightLayout.SpriteBounds(pose, aspect);
                    int x0 = Math.Max(0, (int)((bounds.X - bounds.Width * .6) * dimensions.Width));
                    int x1 = Math.Min(dimensions.Width, (int)((bounds.X + bounds.Width * 1.6) * dimensions.Width));
                    int y0 = Math.Max(0, (int)((bounds.Y - bounds.Height * .2) * dimensions.Height));
                    int y1 = Math.Min(dimensions.Height, (int)((bounds.Y + bounds.Height * 1.5) * dimensions.Height));
                    for (int y = y0; y < y1; y += 3)
                    for (int x = x0; x < x1; x += 3)
                    {
                        byte alpha = pixels[(y * dimensions.Width + x) * 4 + 3];
                        bool hit = DuckHuntFlightLayout.ContainsBird(
                            new((x + .5) / dimensions.Width, (y + .5) / dimensions.Height), pose, aspect);
                        if (alpha > 128) { sampledInk++; if (!hit) missedInk++; }
                        if (hit) { sampledHits++; if (alpha < 16) emptyHits++; }
                    }
                    Check(sampledInk > 20 && missedInk <= sampledInk * .10 &&
                        sampledHits > 20 && emptyHits <= sampledHits * .06,
                        $"Flight hit silhouettes diverge from alpha pixels: {missedInk}/{sampledInk} missed, {emptyHits}/{sampledHits} empty hits.");
                    if (first is not null) Check(!first.AsSpan().SequenceEqual(pixels), "The wing phase/facing did not change its drawing.");
                    first = pixels;
                    string path = Path.Combine(directory, $"{dimensions.Name}-flight-{direction}-{(int)Math.Round(phase * 100)}.png");
                    await sprite.SaveAsync(path, CanvasBitmapFileFormat.Png);
                    images.Add(path);
                }
            }
        }
        return new { Ok = true, Assertions = assertions, Results = results, Images = images, Directory = directory };

        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
