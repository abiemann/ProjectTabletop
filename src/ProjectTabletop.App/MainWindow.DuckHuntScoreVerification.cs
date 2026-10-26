#if DEBUG
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Actual gallery rendering and shot events on a synthetic clock. No capture
    // device, app window, projection output or control pipe is constructed.
    private async Task<object> VerifyDuckHuntScoreAsync()
    {
        const int fps = 60, frames = 240;
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        string directory = Path.Combine(_appDataDirectory, "GalleryScore", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        int assertions = 0, opticalChecks = 0;
        var images = new List<string>();
        var results = new List<object>();
        foreach (var dimensions in new[]
        {
            (Name: "landscape", Width: 1920, Height: 1080),
            (Name: "square", Width: 1000, Height: 1000),
            (Name: "portrait", Width: 1080, Height: 1920),
            (Name: "4k", Width: 3840, Height: 2160)
        })
        {
            double aspect = dimensions.Width / (double)dimensions.Height;
            var now = MonotonicClock.UtcNow;
            using var scene = new SceneCompositor(blackjackClock: () => now, boardRevealClock: () => now);
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), dimensions.Width, dimensions.Height, 96);
            var board = (BoardSession)typeof(SceneCompositor).GetField("_boardSession", privateInstance)!.GetValue(scene)!;
            scene.SetDisplayAspect(aspect);
            scene.SetBoardSetup(true);
            scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
                Homography.FromFourPoints([new(0, 0), new(dimensions.Width, 0), new(dimensions.Width, dimensions.Height), new(0, dimensions.Height)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            scene.SetBoardSetup(false);
            now = MonotonicClock.UtcNow; // Calibration advances the real input timestamp.
            scene.ShowDuckHuntTargetPractice();
            await scene.EnsureBoardArtworkResourcesAsync(target.Device, BoardScreen.DuckHunt);
            now = MonotonicClock.UtcNow.AddSeconds(1);
            scene.SetDuckHuntAudioStatus(true, "Isolated score fixture");
            Draw();
            Check(board.DuckHuntDrawerOpen, "The ready drawer did not open automatically before PLAY.");
            now += TimeSpan.FromMilliseconds(1);
            Check(ActivateGalleryPlay(scene) && !board.DuckHuntDrawerOpen,
                "PLAY did not close the drawer before showing the score.");
            now += TimeSpan.FromSeconds(4);
            byte[] zero = Draw();
            CheckScore(0);
            var score = SceneCompositor.GalleryScoreBounds(aspect);
            Check(Math.Abs(score.X + score.Width / 2 - .5) < 1e-8,
                "The score is not centred on the lower wooden panel.");
            Check(score.Y >= 0 && score.Y + score.Height <= .96000001,
                "The taller cabinet pushed the score off the board.");
            for (int digit = 0; digit < 4; digit++)
                Check(WhitePixels(zero, new(score.X + digit * score.Width / 4, score.Y,
                    score.Width / 4, score.Height)) > 8, "A white zero is missing from the four-digit display.");
            CheckOptics(zero, "0000");
            await Save("0000");

            var centre = PaperSource(DuckHuntGalleryLayout.InnerRingSourceX, DuckHuntGalleryLayout.InnerRingSourceY);
            var annulus = PaperSource(DuckHuntGalleryLayout.OuterRingSourceX + 250,
                DuckHuntGalleryLayout.OuterRingSourceY);
            var outside = RingPoint(DuckHuntGalleryLayout.OuterRingSourceX,
                DuckHuntGalleryLayout.OuterRingSourceY, 400);
            var innerLine = RingPoint(DuckHuntGalleryLayout.InnerRingSourceX,
                DuckHuntGalleryLayout.InnerRingSourceY, DuckHuntGalleryLayout.InnerRingSourceRadius);
            var outerLine = RingPoint(DuckHuntGalleryLayout.OuterRingSourceX,
                DuckHuntGalleryLayout.OuterRingSourceY, DuckHuntGalleryLayout.OuterRingSourceRadius);
            Check(DuckHuntGalleryLayout.PaperScore(centre) == 100 && DuckHuntGalleryLayout.PaperScore(annulus) == 50 &&
                DuckHuntGalleryLayout.PaperScore(outside) == 0 && DuckHuntGalleryLayout.ContainsPaper(outside),
                "The native scoring probes do not cover the printed centre, annulus and outer paper.");
            var origin = now;
            byte[]? midway = null;
            var sampleFrames = new HashSet<int> { 0, 20, 25, 30, 35, 45, 60, 79, 80, 90, 100, 110, 130, 159, 160, 175, 200, 220, 239 };
            for (int frame = 0; frame < frames; frame++)
            {
                if (dimensions.Name != "landscape" && !sampleFrames.Contains(frame)) continue;
                now = origin + TimeSpan.FromSeconds(frame / (double)fps);
                (int From, int To, double Progress)[]? beforeRetarget = null;
                if (frame == 100)
                {
                    Draw();
                    beforeRetarget = Enumerable.Range(0, 4).Select(scene.GalleryScoreDigit).ToArray();
                    Check(beforeRetarget.Any(digit => digit.From != digit.To && digit.Progress is > 0 and < 1),
                        "The rapid-award probe did not reach an in-flight score flap.");
                }
                if (frame == 20) HitSkull(0, 100);
                if (frame == 80) HitPaper(1, annulus, 150);
                if (frame == 100) HitPaper(0, centre, 250);
                if (frame == 160) HitSkull(1, 350);
                byte[] pixels = Draw();
                for (int digit = 0; digit < 4; digit++)
                {
                    var flip = scene.GalleryScoreDigit(digit);
                    Check(flip.Item1 is >= 0 and <= 9 && flip.Item2 is >= 0 and <= 9 && flip.Item3 is >= 0 and <= 1,
                        "A score flap produced an invalid digit or progress.");
                    if (beforeRetarget is { } before && before[digit].From != before[digit].To &&
                        before[digit].Progress is > 0 and < 1)
                        Check(flip.Item1 == before[digit].From && flip.Item2 == before[digit].To &&
                            Math.Abs(flip.Item3 - before[digit].Progress) < 1e-10,
                            "A rapid award jumped an unfinished score flap instead of retargeting it continuously.");
                }
                if (frame == 30) midway = pixels;
                if (frame == 60)
                    Check(midway is not null && !RegionEqual(midway, pixels, score), "The score did not animate through distinct flap positions.");
                if (sampleFrames.Contains(frame)) CheckOptics(pixels, $"score preview {frame}");
                if (frame is 45 or 90 or 110) await Save($"flap-{frame:D3}");
                if (dimensions.Name == "landscape")
                    await target.SaveAsync(Path.Combine(directory, $"frame-{frame:D4}.jpg"), CanvasBitmapFileFormat.Jpeg);
            }
            byte[] settled = Draw();
            CheckScore(350);
            Check(!RegionEqual(zero, settled, score), "Awards changed the logical score without changing the drawn digits.");
            await Save("0350");
            now += TimeSpan.FromMilliseconds(1);
            HitPaper(1, centre, 350);
            Check(scene.GalleryState.ScoreAnimationSeconds == 1,
                "A repeated first-row hit restarted the settled score animation.");

            // Reopening the drawer hides the score rather than painting over
            // EXIT or BACK. Real awards must leave their caption pixels intact.
            now += TimeSpan.FromMilliseconds(1);
            Check(scene.ActivateDuckHuntAt(.1, .93), "The active score fixture could not reopen its drawer.");
            now += TimeSpan.FromMilliseconds(350);
            byte[] controls = Draw();
            var context = scene.GetHoldButtonContext(now) ?? throw new InvalidOperationException("No gallery caption reference.");
            var captions = new List<BoardRect>();
            foreach (string id in new[] { "duck-hunt-exit", "duck-hunt-back" })
            {
                int index = context.ButtonIds.ToList().IndexOf(id);
                Check(index >= 0 && context.EnabledHoldIds.Contains(id), $"Scoring disabled {id}.");
                var region = context.ExpectedScene.BoardTriggerRegions![index];
                captions.Add(new(region.X, region.Y, region.Width, region.Height));
            }
            now += TimeSpan.FromMilliseconds(1);
            HitPaper(3, innerLine, 450);
            now += TimeSpan.FromMilliseconds(180);
            byte[] awardingWithControls = Draw();
            foreach (var caption in captions)
                Check(RegionEqual(controls, awardingWithControls, caption), "The score animation painted over a stationary control caption.");
            await Save("drawer-award");
            now += TimeSpan.FromMilliseconds(1);
            Check(scene.ActivateDuckHuntAt(.1, .93), "The score fixture could not close its drawer.");
            now += TimeSpan.FromSeconds(1.1);
            Draw();
            CheckScore(450);
            now += TimeSpan.FromMilliseconds(1);
            HitPaper(2, outside, 450);
            now += TimeSpan.FromMilliseconds(1);
            HitPaper(2, centre, 450);
            Check(scene.GalleryState.HitTargets == 4 && scene.GalleryState.ScoreAnimationSeconds == 1,
                "A zero-point first hit did not consume its paper's scoring eligibility.");
            now += TimeSpan.FromMilliseconds(1);
            HitPaper(4, outerLine, 500);
            var lowerFinishedAt = now + DuckHuntGalleryGame.SuccessWaitDuration + DuckHuntGalleryGame.DepartDuration +
                DuckHuntGalleryGame.FallDuration + DuckHuntGalleryGame.ClosingDuration;
            Draw();
            // Advance the actual game without rendering until 500 would have
            // settled. A newly awarded 600 must then start its own flip; elapsed
            // time from the previous award cannot fast-forward the new one.
            now += TimeSpan.FromSeconds(1.2);
            HitSkull(2, 600);
            Draw();
            var sparseHundreds = scene.GalleryScoreDigit(1);
            var sparseTens = scene.GalleryScoreDigit(2);
            Check(sparseHundreds is { From: 5, To: 5 } && sparseTens is { From: 0, To: 0 },
                "A sparse-render gap retroactively advanced a newly awarded score instead of beginning its flip.");
            now += TimeSpan.FromMilliseconds(150);
            Draw();
            sparseHundreds = scene.GalleryScoreDigit(1);
            Check(sparseHundreds is { From: 5, To: 6, Progress: > 0 and < 1 },
                "The new score failed to animate after a gap in rendering.");
            for (int index = 3; index < 5; index++)
            {
                now += TimeSpan.FromMilliseconds(1);
                HitSkull(index, 500 + (index - 1) * 100);
            }
            var upperFinishedAt = now + DuckHuntGalleryGame.UpperShatterWaitDuration + DuckHuntGalleryGame.UpperLowerDuration;
            var middleStartedAt = lowerFinishedAt > upperFinishedAt ? lowerFinishedAt : upperFinishedAt;
            BoardAim[] middlePoints = [outside, annulus, centre, innerLine, outerLine];
            int[] middleScores = [800, 850, 950, 1050, 1100];
            for (int index = 0; index < middlePoints.Length; index++)
            {
                var facingAt = middleStartedAt + DuckHuntGalleryGame.MiddleRaiseDuration +
                    DuckHuntGalleryGame.MiddleTargetDuration * index + DuckHuntGalleryGame.MiddleEnterDuration + DuckHuntGalleryGame.MiddleTurnDuration;
                now = facingAt;
                Draw();
                var quad = DuckHuntGalleryLayout.MiddleTargetQuad(scene.GalleryState, aspect);
                var local = middlePoints[index];
                Check(scene.SubmitDuckHuntShot(quad.Map(local.U, local.V), now) && scene.GalleryState.Score == middleScores[index],
                    "A middle paper did not use the same first-hit printed-ring scoring as the lower row.");
                now += TimeSpan.FromMilliseconds(1);
                Check(scene.SubmitDuckHuntShot(quad.Map(centre.U, centre.V), now) && scene.GalleryState.Score == middleScores[index],
                    "A repeated middle target hit awarded points twice.");
                if (index == 1)
                {
                    now += TimeSpan.FromMilliseconds(140);
                    Draw();
                    scene.SetDuckHuntAudioStatus(false, "Isolated score pause");
                    byte[] paused = Draw();
                    var frozen = scene.GalleryState;
                    var frozenDigits = Enumerable.Range(0, 4).Select(scene.GalleryScoreDigit).ToArray();
                    await Save("pause-start");
                    now += TimeSpan.FromSeconds(2);
                    byte[] held = Draw();
                    var heldState = scene.GalleryState;
                    var heldDigits = Enumerable.Range(0, 4).Select(scene.GalleryScoreDigit).ToArray();
                    if (!ScoreVisualEqual(paused, held, score) || heldState != frozen || !frozenDigits.SequenceEqual(heldDigits))
                    {
                        await Save("pause-held-failure");
                        var pixels = Pixels(score);
                        int changed = 0, maximumDelta = 0;
                        for (int y = pixels.Top; y < pixels.Bottom; y++)
                        for (int x = pixels.Left; x < pixels.Right; x++)
                        {
                            int offset = (y * dimensions.Width + x) * 4;
                            bool differs = false;
                            for (int channel = 0; channel < 4; channel++)
                            {
                                int delta = Math.Abs(paused[offset + channel] - held[offset + channel]);
                                maximumDelta = Math.Max(maximumDelta, delta);
                                differs |= delta != 0;
                            }
                            if (differs) changed++;
                        }
                        string evidence = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            Frozen = frozen, Held = heldState, FrozenDigits = frozenDigits, HeldDigits = heldDigits,
                            ChangedScorePixels = changed, MaximumScoreChannelDelta = maximumDelta
                        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
                        await File.WriteAllTextAsync(Path.Combine(directory, dimensions.Name + "-pause-failure.json"), evidence);
                    }
                    Check(heldState.Paused && heldState == frozen, "The gallery state advanced while input was paused.");
                    Check(frozenDigits.SequenceEqual(heldDigits), "The rendered score reels advanced while input was paused.");
                    Check(ScoreVisualEqual(paused, held, score), "The paused score pixels changed despite a frozen game and reel state.");
                    scene.SetDuckHuntAudioStatus(true, "Isolated score resume");
                    Draw();
                    middleStartedAt += TimeSpan.FromSeconds(2);
                    facingAt += TimeSpan.FromSeconds(2);
                    Check(scene.GalleryState.ScoreAnimationSeconds == frozen.ScoreAnimationSeconds,
                        "Resuming skipped part of the score flip.");
                }
                now = facingAt + TimeSpan.FromSeconds(1.2);
                Draw();
                CheckScore(middleScores[index]);
            }
            now = middleStartedAt + DuckHuntGalleryGame.MiddleSequenceDuration + TimeSpan.FromSeconds(1);
            byte[] complete = Draw();
            Check(scene.GalleryState.PlayVisible, "The score fixture did not finish the middle challenge.");
            CheckScore(1100);
            now += TimeSpan.FromSeconds(2);
            Check(ScoreVisualEqual(complete, Draw(), score) && scene.GalleryState.Score == 1100,
                "The final score disappeared or continued flipping after the round.");
            CheckOptics(target.GetPixelBytes(), "final1100");
            await Save("1100-complete");
            now += TimeSpan.FromMilliseconds(1);
            Check(scene.ActivateDuckHuntAt(.1, .93), "The completed score fixture could not open its replay drawer.");
            now += TimeSpan.FromMilliseconds(350);
            Draw();
            now += TimeSpan.FromMilliseconds(1);
            Check(ActivateGalleryPlay(scene), "The score fixture could not replay through drawer PLAY.");
            byte[] replayed = Draw();
            CheckScore(0);
            Check(ScoreVisualEqual(zero, replayed, score), "Replay did not restore the same clean 0000 display.");
            results.Add(new { dimensions.Name, dimensions.Width, dimensions.Height, FinalScore = 1100,
                FirstHitOnly = true, OuterPaperConsumesEligibility = true, PrintedRingBoundaries = true });

            void HitSkull(int index, int expected)
            {
                var bounds = DuckHuntGalleryLayout.SkullBounds(index, aspect);
                Check(scene.SubmitDuckHuntShot(new(bounds.X + bounds.Width * .5, bounds.Y + bounds.Height * .4), now) &&
                    scene.GalleryState.Score == expected, "A crystal skull did not award exactly 100 points.");
            }
            void HitPaper(int index, BoardAim local, int expected)
            {
                var quad = DuckHuntGalleryLayout.TargetQuad(index, scene.GalleryState, aspect);
                Check(scene.SubmitDuckHuntShot(quad.Map(local.U, local.V), now) && scene.GalleryState.Score == expected,
                    "A lower target awarded the wrong first-hit ring value.");
            }
            void CheckScore(int expected)
            {
                Check(scene.GalleryState.Score == expected, "The gallery score is incorrect.");
                string digits = expected.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
                for (int digit = 0; digit < 4; digit++)
                {
                    var flip = scene.GalleryScoreDigit(digit);
                    Check(flip.Item2 == digits[digit] - '0' && (flip.Item1 == flip.Item2 || flip.Item3 == 1),
                        "The settled score display does not show the zero-padded total.");
                }
            }
            (int Left, int Top, int Right, int Bottom) Pixels(BoardRect region)
            {
                var map = (Homography)typeof(SceneCompositor).GetField("_boardSurfaceMap", privateInstance)!.GetValue(scene)!;
                var a = map.Transform(new(region.X, region.Y));
                var b = map.Transform(new(region.X + region.Width, region.Y + region.Height));
                return (Math.Clamp((int)Math.Ceiling(a.X * dimensions.Width), 0, dimensions.Width - 1),
                    Math.Clamp((int)Math.Ceiling(a.Y * dimensions.Height), 0, dimensions.Height - 1),
                    Math.Clamp((int)Math.Floor(b.X * dimensions.Width), 1, dimensions.Width),
                    Math.Clamp((int)Math.Floor(b.Y * dimensions.Height), 1, dimensions.Height));
            }
            bool RegionEqual(byte[] a, byte[] b, BoardRect region)
            {
                var p = Pixels(region);
                for (int y = p.Top; y < p.Bottom; y++)
                {
                    int offset = (y * dimensions.Width + p.Left) * 4;
                    if (!a.AsSpan(offset, (p.Right - p.Left) * 4).SequenceEqual(b.AsSpan(offset, (p.Right - p.Left) * 4))) return false;
                }
                return true;
            }
            bool ScoreVisualEqual(byte[] a, byte[] b, BoardRect region)
            {
                // The captured paused render changed only x=1023,y=954..971:
                // eighteen samples at the stationary zero's minified edge, each
                // by 1/255. Game state and all four reels were exactly identical.
                // Bound that GPU raster rounding; never mask an actual flap move.
                var p = Pixels(region);
                int changed = 0;
                int maximumChanged = Math.Max(64, (int)Math.Ceiling(64.0 * dimensions.Width * dimensions.Height / (1920 * 1080)));
                for (int y = p.Top; y < p.Bottom; y++)
                for (int x = p.Left; x < p.Right; x++)
                {
                    int offset = (y * dimensions.Width + x) * 4;
                    if (a.AsSpan(offset, 4).SequenceEqual(b.AsSpan(offset, 4))) continue;
                    if (++changed > maximumChanged) return false;
                    for (int channel = 0; channel < 4; channel++)
                        if (Math.Abs(a[offset + channel] - b[offset + channel]) > 1) return false;
                }
                return true;
            }
            int WhitePixels(byte[] bytes, BoardRect region)
            {
                var p = Pixels(region);
                int count = 0;
                for (int y = p.Top; y < p.Bottom; y++)
                for (int x = p.Left; x < p.Right; x++)
                {
                    int offset = (y * dimensions.Width + x) * 4;
                    if (bytes[offset] > 200 && bytes[offset + 1] > 200 && bytes[offset + 2] > 200) count++;
                }
                return count;
            }
            void CheckOptics(byte[] bytes, string stage)
            {
                var input = scene.GetDuckHuntInputContext() ?? throw new InvalidOperationException("No score fixture optical mapping.");
                var dot = new RedDotDetector().Detect(dimensions.Width, dimensions.Height, dimensions.Width * 4, bytes, input.CameraToBoard);
                Check(!dot.Detected && dot.CandidateCount == 0, $"{dimensions.Name} {stage} generated a false red dot.");
                opticalChecks++;
            }
            byte[] Draw()
            {
                using (var drawing = target.CreateDrawingSession())
                    scene.Draw(drawing, dimensions.Width, dimensions.Height, preview: false, runningSlowly: false);
                return target.GetPixelBytes();
            }
            async Task Save(string stage)
            {
                string path = Path.Combine(directory, dimensions.Name + "-" + stage + ".png");
                await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
                images.Add(path);
            }
        }
        return new { Ok = true, Assertions = assertions, OpticalChecks = opticalChecks, Directory = directory,
            Results = results, Images = images, Width = 1920, Height = 1080, Fps = fps, Frames = frames,
            TimingScope = "Real gallery events and compositor frames at a synthetic 60 Hz clock; not live projector pacing." };

        static BoardAim PaperSource(double x, double y) => new(x / 1177, .5 + (y - 668.5) * .88 / 1177);
        static BoardAim RingPoint(double x, double y, double radius) =>
            PaperSource(x + radius / Math.Sqrt(2), y + radius / Math.Sqrt(2));
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
