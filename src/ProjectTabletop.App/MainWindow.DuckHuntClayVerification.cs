#if DEBUG
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Isolated GPU targets and simulated timestamps only. This never opens a
    // camera, microphone, projection window, or another application instance.
    private async Task<object> VerifyDuckHuntClayAsync()
    {
        string directory = Path.Combine(_appDataDirectory, "DuckHuntClayVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var images = new List<string>();
        var results = new List<object>();
        var opticalFailures = new List<string>();
        int assertions = 0;
        foreach (var dimensions in new[]
        {
            (Name: "4k", Width: 3840, Height: 2160),
            (Name: "wide", Width: 2560, Height: 1080),
            (Name: "square", Width: 1200, Height: 1200),
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
            scene.ShowDuckHuntClayPigeon();
            await scene.EnsureBoardArtworkResourcesAsync(target.Device, BoardScreen.DuckHunt);
            now = MonotonicClock.UtcNow.AddSeconds(1);
            scene.SetDuckHuntAudioStatus(true, "Isolated clay fixture");
            Draw();
            var roundStart = now;
            Check(scene.DuckHuntArtworkReady && scene.DuckHuntBoardReady && scene.DuckHuntInputReady,
                "Clay Pigeon did not publish its artwork and calibrated input.");
            Check(scene.ClayState is { Phase: DuckHuntClayPhase.Playing, Paused: false, PairsLaunched: 0 },
                "Clay Pigeon did not automatically begin once ready.");
            Check(board.Buttons.Select(button => button.Id).SequenceEqual(["duck-hunt-drawer-open"]),
                "Clay Pigeon exposed controls before the drawer was opened.");
            await Save("ready");
            CheckClearOptics("ready");
            double previousScale = double.PositiveInfinity;
            byte[]? previousFrame = null;
            foreach (double age in new[] { .25, .49, .75, 1.2, 2.1, 3.3 })
            {
                now = roundStart + DuckHuntClayGame.LaunchDelay + TimeSpan.FromSeconds(age);
                byte[] frame = Draw();
                var clay = board.ClayTargets[0];
                Check(clay.Visible && clay.Scale < previousScale && clay.U < .5 && clay.V < 1.1,
                    "The first clay did not rise, move sideways, and shrink continuously.");
                Check(board.ClayTargets[1].Visible == (age >= .5),
                    "The second clay did not preserve the half-second stagger.");
                if (previousFrame is not null)
                    Check(!previousFrame.AsSpan().SequenceEqual(frame), "The rendered clay trajectory became a stale cached frame.");
                previousFrame = frame;
                previousScale = clay.Scale;
                await Save("flight-" + (int)Math.Round(age * 1000));
                CheckClearOptics("flight-" + (int)Math.Round(age * 1000));
                CheckOpticalAim(new(clay.U, clay.V), "flight-" + (int)Math.Round(age * 1000));
            }
            // Use the next pair near the foreground centre for clear, useful
            // shatter captures after the first pair's full trajectory sequence.
            now = roundStart + DuckHuntClayGame.PairInterval + DuckHuntClayGame.LaunchDelay + TimeSpan.FromSeconds(1.1);
            Draw();
            var hitTarget = board.ClayTargets[2];
            Check(scene.SubmitDuckHuntShot(new(hitTarget.U, hitTarget.V), now), "The clay's visible centre was not hittable.");
            Check(scene.ClayState.Score == 1 && board.ClayTargets[2].Hit, "The hit did not shatter exactly one clay.");
            var hitAt = now;
            foreach (double elapsed in new[] { 0, .07, .2, .45, .8, 1.22 })
            {
                now = hitAt + TimeSpan.FromSeconds(elapsed);
                Draw();
                Check(board.ClayTargets[2] is { Hit: true, Visible: true }, "The ceramic fragments ended early.");
                await Save("shatter-" + (int)Math.Round(elapsed * 1000));
                CheckClearOptics("shatter-" + (int)Math.Round(elapsed * 1000));
            }
            await CheckSpriteAndShatter(hitTarget);

            scene.SetDuckHuntAudioStatus(false, "Isolated unavailable microphone");
            Draw();
            var paused = board.ClayTargets[3];
            now += TimeSpan.FromSeconds(8);
            Draw();
            Check(scene.ClayState.Paused && board.ClayTargets[3] == paused,
                "Loss of input allowed clays to continue flying.");
            scene.SetDuckHuntAudioStatus(true, "Isolated clay fixture");
            Draw();
            now += TimeSpan.FromSeconds(90);
            Draw();
            Check(scene.ClayState is { Phase: DuckHuntClayPhase.Finished, Score: 1, PairsLaunched: 10 },
                "The clay round did not stop after exactly ten pairs.");
            now += TimeSpan.FromMilliseconds(350);
            Draw();
            Check(board.DuckHuntDrawerOpen && board.Buttons.Select(button => button.Id).SequenceEqual(
                ["duck-hunt-drawer-close", "duck-hunt-exit", "duck-hunt-back", "duck-hunt-play-again"]),
                "The final clay drawer is missing its replay and navigation captions.");
            await Save("finished");
            Check(board.ActivateButton("duck-hunt-play-again", now), "PLAY AGAIN did not restart Clay Pigeon.");
            Draw();
            Check(scene.ClayState is { Phase: DuckHuntClayPhase.Playing, Score: 0, PairsLaunched: 0 },
                "The replay retained the previous score or launch count.");
            results.Add(new { dimensions.Name, dimensions.Width, dimensions.Height, Targets = 20,
                PairDelaySeconds = .5, AutomaticStart = true, ClearSceneCandidates = 0 });

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
                var input = scene.GetDuckHuntInputContext() ?? throw new InvalidOperationException("Clay Pigeon has no optical mapping.");
                var detection = new RedDotDetector().Detect(dimensions.Width, dimensions.Height, dimensions.Width * 4,
                    target.GetPixelBytes(), input.CameraToBoard);
                string evidence = Path.Combine(directory, dimensions.Name + "-" + stage + "-optics.json");
                File.WriteAllText(evidence, JsonSerializer.Serialize(new
                {
                    Stage = stage, dimensions.Width, dimensions.Height, Detection = detection,
                    input.CameraToBoard, Image = Path.Combine(directory, dimensions.Name + "-" + stage + ".png"),
                    CandidateMeaning = "CandidateCount includes only optically viable candidates after all colour, size, shape, and contrast filters. More than one makes the frame ambiguous."
                }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
                assertions++;
                if (detection.Detected || detection.CandidateCount != 0)
                    opticalFailures.Add($"{dimensions.Name} {stage}: {detection.CandidateCount} viable false red-dot candidates " +
                        $"(detected={detection.Detected}, ambiguous={detection.Ambiguous}). Evidence: {evidence}");
            }
            void CheckOpticalAim(BoardAim aim, string stage)
            {
                var input = scene.GetDuckHuntInputContext()!;
                var surface = (Homography)typeof(SceneCompositor).GetField("_boardSurfaceMap",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
                var projected = surface.Transform(new Point2(aim.U, aim.V));
                double cx = projected.X * dimensions.Width, cy = projected.Y * dimensions.Height;
                byte[] pixels = target.GetPixelBytes();
                // Simulate a non-clipped camera exposure plus the red laser's
                // observed blue-biased response. This is an optical regression,
                // not a substitute for the user's real projector/camera test.
                for (int p = 0; p < pixels.Length; p += 4)
                {
                    pixels[p] = (byte)(pixels[p] * .8);
                    pixels[p + 1] = (byte)(pixels[p + 1] * .8);
                    pixels[p + 2] = (byte)(pixels[p + 2] * .8);
                }
                for (int y = Math.Max(0, (int)cy - 6); y <= Math.Min(dimensions.Height - 1, (int)cy + 6); y++)
                for (int x = Math.Max(0, (int)cx - 6); x <= Math.Min(dimensions.Width - 1, (int)cx + 6); x++)
                {
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > 20) continue;
                    int p = (y * dimensions.Width + x) * 4;
                    pixels[p] = (byte)Math.Min(255, pixels[p] + 180);
                    pixels[p + 2] = 255;
                }
                var detected = new RedDotDetector().Detect(dimensions.Width, dimensions.Height, dimensions.Width * 4,
                    pixels, input.CameraToBoard);
                Check(detected.Detected && Math.Abs(detected.BoardX!.Value - aim.U) < .003 &&
                    Math.Abs(detected.BoardY!.Value - aim.V) < .003,
                    $"The additive laser on {dimensions.Name} {stage} did not map to its clay centre.");
            }
            async Task CheckSpriteAndShatter(DuckHuntClayTarget source)
            {
                using var sprite = new CanvasRenderTarget(target.Device, dimensions.Width, dimensions.Height, 96);
                foreach (double bank in new[] { -.42, 0, .42 })
                {
                    var pose = source with { U = .5, V = .45, Scale = 1, FaceRatio = .55,
                        BankAngle = bank, Visible = true, Hit = false };
                    using (var drawing = sprite.CreateDrawingSession())
                    {
                        drawing.Clear(Microsoft.UI.Colors.Transparent);
                        drawing.Transform = Matrix3x2.CreateScale(dimensions.Width / 1000f, dimensions.Height / 1000f);
                        scene.DrawDuckHuntClayTarget(drawing, pose, aspect);
                    }
                    byte[] pixels = sprite.GetPixelBytes();
                    int sampledInk = 0, missedInk = 0, sampledHits = 0, emptyHits = 0;
                    for (int y = (int)(dimensions.Height * .30); y < dimensions.Height * .60; y += 2)
                    for (int x = (int)(dimensions.Width * .35); x < dimensions.Width * .65; x += 2)
                    {
                        byte alpha = pixels[(y * dimensions.Width + x) * 4 + 3];
                        bool hit = DuckHuntClayLayout.ContainsClay(new((x + .5) / dimensions.Width, (y + .5) / dimensions.Height), pose, aspect);
                        if (alpha > 128) { sampledInk++; if (!hit) missedInk++; }
                        if (hit) { sampledHits++; if (alpha < 16) emptyHits++; }
                    }
                    Check(sampledInk > 50 && missedInk <= sampledInk * .15 && sampledHits > 50 && emptyHits <= sampledHits * .15,
                        $"Clay alpha and hit ellipse diverge at {bank}: {missedInk}/{sampledInk} missed, {emptyHits}/{sampledHits} empty hits.");
                    string path = Path.Combine(directory, $"{dimensions.Name}-clay-silhouette-{(int)Math.Round(bank * 100)}.png");
                    await sprite.SaveAsync(path, CanvasBitmapFileFormat.Png);
                    images.Add(path);
                }
            }
        }
        File.WriteAllText(Path.Combine(directory, "verification-summary.json"), JsonSerializer.Serialize(new
        {
            Assertions = assertions, Results = results, Images = images, OpticalFailures = opticalFailures
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Check(opticalFailures.Count == 0, "Clay optics failed after all rendering/gameplay checks: " + string.Join("\n", opticalFailures));
        return new { Ok = true, Assertions = assertions, Results = results, Images = images, Directory = directory };

        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
