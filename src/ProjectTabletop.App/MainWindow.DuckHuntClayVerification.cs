#if DEBUG
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
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
            // Calibration resets input with the host clock. Start the simulated
            // view afterward so its drawer request is not an obsolete event.
            now = MonotonicClock.UtcNow;
            scene.ShowDuckHuntClayPigeon();
            await scene.EnsureBoardArtworkResourcesAsync(target.Device, BoardScreen.DuckHunt);
            now = MonotonicClock.UtcNow.AddSeconds(1);
            scene.SetDuckHuntAudioStatus(true, "Isolated clay fixture");
            Draw();
            Check(scene.DuckHuntArtworkReady && scene.DuckHuntBoardReady && scene.DuckHuntInputReady,
                "Clay Pigeon did not publish its artwork and calibrated input.");
            Check(scene.ClayState is { Phase: DuckHuntClayPhase.Ready, Paused: false, PairsLaunched: 0 } &&
                board.DuckHuntDrawerOpen && board.Buttons.Select(button => button.Id).SequenceEqual(
                    ["duck-hunt-drawer-close", "duck-hunt-exit", "duck-hunt-back", "duck-hunt-play"]),
                $"Clay Pigeon did not wait with EXIT, BACK and PLAY in its expanded drawer: " +
                $"state={scene.ClayState}, open={board.DuckHuntDrawerOpen}, " +
                $"buttons={string.Join(',', board.Buttons.Select(button => button.Id))}.");
            now += TimeSpan.FromSeconds(30);
            Draw();
            Check(!scene.SubmitDuckHuntShot(new(.5, .5), now) &&
                scene.ClayState is { Phase: DuckHuntClayPhase.Ready, Shots: 0, PairsLaunched: 0 } &&
                board.ClayTargets.All(clay => !clay.Visible),
                "Waiting or shooting the scene started Clay Pigeon without PLAY.");
            CheckMessages();
            await Save("ready");
            CheckClearOptics("ready");
            byte[] idleFrame = target.GetPixelBytes();
            var idleState = scene.ClayState;
            long idleVisualFrame = scene.ClayFlockVisualFrame(now);
            now += TimeSpan.FromMilliseconds(1070);
            byte[] animatedIdle = Draw();
            int footerOffset = (int)(dimensions.Height * .80) * dimensions.Width * 4;
            Check(!idleFrame.AsSpan().SequenceEqual(animatedIdle) &&
                idleFrame.AsSpan(footerOffset).SequenceEqual(animatedIdle.AsSpan(footerOffset)),
                "Idle horizon animation froze, or changed the stationary footer captions.");
            Check(scene.ClayState == idleState && scene.ClayState is
                { Phase: DuckHuntClayPhase.Ready, PairsLaunched: 0, Shots: 0 } &&
                board.ClayTargets.All(clay => !clay.Visible) && scene.ClayFlockVisualFrame(now) != idleVisualFrame,
                "Decorative flock animation changed gameplay or failed to invalidate the idle raster.");
            await Save("ready-motion");
            CheckClearOptics("ready-motion");
            await CheckHorizonFlocks();
            Check(ActivateGalleryPlay(scene) && !board.DuckHuntDrawerOpen,
                "The regular drawer PLAY button did not begin Clay Pigeon and close its controls.");
            Draw();
            var roundStart = now;
            Check(scene.ClayState is { Phase: DuckHuntClayPhase.Playing, Paused: false, PairsLaunched: 0 },
                "PLAY failed to start a fresh Clay Pigeon round.");
            CheckMessages();
            double previousScale = double.PositiveInfinity;
            byte[]? previousFrame = null;
            using var flightLayer = new CanvasRenderTarget(target.Device, dimensions.Width, dimensions.Height, 96);
            foreach (double age in new[] { .25, .49, .75, 1.2, 2.1, 3.3 })
            {
                now = roundStart + DuckHuntClayGame.LaunchDelay + TimeSpan.FromSeconds(age);
                Draw();
                using (var drawing = flightLayer.CreateDrawingSession())
                {
                    drawing.Clear(Microsoft.UI.Colors.Transparent);
                    drawing.Transform = Matrix3x2.CreateScale(dimensions.Width / 1000f, dimensions.Height / 1000f);
                    foreach (var pose in board.ClayTargets) scene.DrawDuckHuntClayTarget(drawing, pose, aspect);
                }
                byte[] frame = flightLayer.GetPixelBytes();
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
            Check(!scene.SubmitDuckHuntShot(new(.99, .1), now), "A clear-sky miss incorrectly hit a clay.");
            Draw();
            Check(board.ClayTargets.All(clay => clay.Impact is null), "A missed shot created score feedback.");
            await Save("miss");
            now += TimeSpan.FromMilliseconds(20);
            Draw();
            var hitTarget = board.ClayTargets[2];
            Check(scene.SubmitDuckHuntShot(new(hitTarget.U, hitTarget.V), now), "The clay's visible centre was not hittable.");
            Check(scene.ClayState.Score == 1 && board.ClayTargets[2].Hit, "The hit did not shatter exactly one clay.");
            CheckScorePopup(board.ClayTargets[2]);
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
            CheckMessages();
            await Save("finished");
            CheckClearOptics("finished");
            Check(board.ActivateButton("duck-hunt-play-again", now), "PLAY AGAIN did not restart Clay Pigeon.");
            Draw();
            Check(scene.ClayState is { Phase: DuckHuntClayPhase.Playing, Score: 0, PairsLaunched: 0 },
                "The replay retained the previous score or launch count.");
            Check(board.ClayTargets.All(clay => clay.Impact is null), "Replay retained old floating score impacts.");
            CheckMessages();
            await Save("replay");
            results.Add(new { dimensions.Name, dimensions.Width, dimensions.Height, Targets = 20,
                PairDelaySeconds = .5, WaitsForPlay = true, IdleAndEndMessages = true,
                DecorativeHorizonMotion = true, StationaryFooter = true, ClearSceneCandidates = 0 });
            scene.ShowDuckHunt();
            Check(scene.ClayFlockVisualFrame(now) == scene.ClayFlockVisualFrame(now.AddSeconds(1)),
                "Clay flock animation kept invalidating a different board view.");

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
            async Task CheckHorizonFlocks()
            {
                using var overlay = new CanvasRenderTarget(target.Device, dimensions.Width, dimensions.Height, 96);
                var wings = (CanvasGeometry[])typeof(SceneCompositor).GetField("_clayBirdWings",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
                Check(wings.Length >= 4 && wings[0].ComputeBounds() != wings[wings.Length / 4].ComputeBounds() &&
                    wings[wings.Length / 4].ComputeBounds() != wings[wings.Length * 3 / 4].ComputeBounds(),
                    "The horizon birds have no distinct extended, raised and lowered wing silhouettes.");
                byte[]? first = null;
                double firstCenterX = 0, firstCenterY = 0;
                foreach (int elapsed in new[] { 0, 110, 1070 })
                {
                    using (var drawing = overlay.CreateDrawingSession())
                    {
                        drawing.Clear(Microsoft.UI.Colors.Transparent);
                        drawing.Transform = Matrix3x2.CreateScale(dimensions.Width / 1000f, dimensions.Height / 1000f);
                        scene.DrawClayHorizonFlocks(drawing, now.AddMilliseconds(elapsed), aspect);
                    }
                    byte[] pixels = overlay.GetPixelBytes();
                    int ink = 0, top = dimensions.Height, bottom = -1;
                    long totalAlpha = 0;
                    double sumX = 0, sumY = 0;
                    for (int y = 0; y < dimensions.Height; y++)
                    for (int x = 0; x < dimensions.Width; x++)
                    {
                        byte alpha = pixels[(y * dimensions.Width + x) * 4 + 3];
                        if (alpha <= 8) continue;
                        ink++; top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                        totalAlpha += alpha; sumX += (double)x * alpha; sumY += (double)y * alpha;
                    }
                    Check(ink > 10 && top >= dimensions.Height * .25 && bottom <= dimensions.Height * .55,
                        $"Horizon flock ink left the distant sky or reached the footer: {dimensions.Name}, {top}–{bottom}, {ink} pixels.");
                    if (first is null)
                    {
                        first = pixels;
                        firstCenterX = sumX / totalAlpha; firstCenterY = sumY / totalAlpha;
                    }
                    else Check(!first.AsSpan().SequenceEqual(pixels), "Horizon birds retained a frozen silhouette frame.");
                    if (elapsed == 1070)
                        Check(Math.Abs(sumX / totalAlpha - firstCenterX) + Math.Abs(sumY / totalAlpha - firstCenterY) > .1,
                            "Horizon flocks changed their wings but did not travel across the valley.");
                    if (elapsed != 110)
                    {
                        string path = Path.Combine(directory, $"{dimensions.Name}-horizon-flocks-{elapsed}.png");
                        await overlay.SaveAsync(path, CanvasBitmapFileFormat.Png);
                        images.Add(path);
                    }
                }
            }
            void CheckMessages()
            {
                using var overlay = new CanvasRenderTarget(target.Device, dimensions.Width, dimensions.Height, 96);
                var drawHud = (Action<CanvasDrawingSession, DuckHuntClaySnapshot, double>)typeof(SceneCompositor)
                    .GetMethod("DrawDuckHuntClay", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .CreateDelegate(typeof(Action<CanvasDrawingSession, DuckHuntClaySnapshot, double>), scene);
                using (var drawing = overlay.CreateDrawingSession())
                {
                    drawing.Clear(Microsoft.UI.Colors.Transparent);
                    drawing.Transform = Matrix3x2.CreateScale(dimensions.Width / 1000f, dimensions.Height / 1000f);
                    drawHud(drawing, scene.ClayState, aspect);
                }
                byte[] pixels = overlay.GetPixelBytes();
                int prompt = CountInk(.42, .49), heading = CountInk(.29, .36), result = CountInk(.36, .41);
                if (scene.ClayState.Phase == DuckHuntClayPhase.Playing)
                {
                    Check(prompt == 0 && heading == 0 && result == 0,
                        "Clay Pigeon left idle or end messages visible after PLAY.");
                    return;
                }
                Check(prompt > 50 && CaptionText("_clayPromptCaption") == "PRESS PLAY",
                    "The idle/end view did not render its PRESS PLAY message.");
                if (scene.ClayState.Phase == DuckHuntClayPhase.Finished)
                    Check(heading > 50 && result > 50 && CaptionText("_clayCompleteCaption") == "GAME OVER" &&
                        CaptionText("_clayResultCaption") == $"{scene.ClayState.Score} OF 20 CLAYS",
                        "The finished round lost GAME OVER or its final clay count.");
                else Check(heading == 0 && result == 0, "The initial view showed a completed-round result.");

                int CountInk(double top, double bottom)
                {
                    int count = 0;
                    for (int y = (int)(top * dimensions.Height); y < bottom * dimensions.Height; y++)
                    for (int x = 0; x < dimensions.Width; x++)
                        if (pixels[(y * dimensions.Width + x) * 4 + 3] > 16) count++;
                    return count;
                }
                string? CaptionText(string field)
                {
                    object caption = typeof(SceneCompositor).GetField(field,
                        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
                    return (string?)caption.GetType().GetField("_text",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(caption);
                }
            }
            void CheckScorePopup(DuckHuntClayTarget hit)
            {
                using var overlay = new CanvasRenderTarget(target.Device, dimensions.Width, dimensions.Height, 96);
                var initial = Render(hit with { HitElapsed = 0 });
                var rising = Render(hit with { HitElapsed = .45 });
                Check(initial.Gold > 20 && initial.Black > 10,
                    "The floating score lacks its golden-hour yellow face or black outline.");
                Check(Math.Abs(initial.CenterX / dimensions.Width - hit.Impact!.Value.U) < .012,
                    "The floating score is not centred on the shot impact.");
                Check(rising.CenterY < initial.CenterY - dimensions.Height * .01,
                    "The floating score did not rise from the impact.");
                Check(Render(hit with { Hit = false, Impact = null }).Ink == 0 &&
                    Render(hit with { HitElapsed = DuckHuntClayGame.HitDuration.TotalSeconds }).Ink == 0,
                    "The floating score was drawn without a hit or after its lifetime.");

                (int Ink, int Gold, int Black, double CenterX, double CenterY) Render(DuckHuntClayTarget pose)
                {
                    using (var drawing = overlay.CreateDrawingSession())
                    {
                        drawing.Clear(Microsoft.UI.Colors.Transparent);
                        drawing.Transform = Matrix3x2.CreateScale(dimensions.Width / 1000f, dimensions.Height / 1000f);
                        scene.DrawDuckHuntClayHitScore(drawing, pose, aspect);
                    }
                    var pixels = overlay.GetPixelBytes();
                    int ink = 0, gold = 0, black = 0;
                    double sumX = 0, sumY = 0;
                    for (int y = 0; y < dimensions.Height; y++)
                    for (int x = 0; x < dimensions.Width; x++)
                    {
                        int p = (y * dimensions.Width + x) * 4;
                        if (pixels[p + 3] < 200) continue;
                        ink++; sumX += x; sumY += y;
                        if (pixels[p] is >= 100 and <= 120 && pixels[p + 1] is >= 200 and <= 220 && pixels[p + 2] > 245) gold++;
                        if (pixels[p] < 30 && pixels[p + 1] < 30 && pixels[p + 2] < 30) black++;
                    }
                    return (ink, gold, black, ink == 0 ? 0 : sumX / ink, ink == 0 ? 0 : sumY / ink);
                }
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
