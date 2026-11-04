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
    // A continuous native-rendered preview with a synthetic clock. No app,
    // microphone, camera, output window or control pipe is started here.
    private async Task<object> VerifyDuckHuntMiddleAsync()
    {
        const int width = 1280, height = 720, fps = 60, frames = 600;
        const double aspect = width / (double)height;
        int assertions = 0, opticalChecks = 0;
        string directory = Path.Combine(_appDataDirectory, "GalleryMiddle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var now = MonotonicClock.UtcNow;
        using var scene = new SceneCompositor(blackjackClock: () => now, boardRevealClock: () => now);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        scene.SetDisplayAspect(aspect);
        scene.SetBoardSetup(true);
        scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
            Homography.FromFourPoints([new(0, 0), new(width, 0), new(width, height), new(0, height)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        now = MonotonicClock.UtcNow; // Calibration advances the real input timestamp.
        scene.ShowDuckHuntTargetPractice();
        await scene.EnsureBoardArtworkResourcesAsync(target.Device, BoardScreen.DuckHunt);
        now = MonotonicClock.UtcNow.AddSeconds(1);
        scene.SetDuckHuntAudioStatus(true, "Isolated middle-stage fixture");
        Draw();
        Check(ActivateGalleryPlay(scene), "The drawer PLAY button did not start the gallery preview.");
        now += TimeSpan.FromSeconds(4);
        Draw();
        Check(scene.GalleryState is { Phase: DuckHuntGalleryPhase.WaitingForHits,
            UpperPhase: DuckHuntGalleryUpperPhase.WaitingForHits }, "The preview rows did not finish opening.");

        var origin = now;
        bool shot = false;
        DateTimeOffset? firstHitAt = null;
        bool hitTurnedAway = false;
        var transitions = new List<object>();
        var observed = new HashSet<DuckHuntGalleryMiddlePhase>();
        var previous = DuckHuntGalleryMiddlePhase.Closed;
        for (int frame = 0; frame < frames; frame++)
        {
            now = origin + TimeSpan.FromSeconds(frame / (double)fps);
            Draw();
            if (frame == 30)
            {
                Check(!scene.SubmitDuckHuntShot(DuckHuntGalleryLayout.ScenePoint(.5, .19, aspect), now) &&
                    scene.GalleryState is { UpperPhase: DuckHuntGalleryUpperPhase.Lowering,
                        Phase: DuckHuntGalleryPhase.WaitingForHits, HitSkulls: 0 },
                    "An upper-background miss failed to close just that curtain.");
            }
            if (frame == 60)
            {
                Check(!scene.SubmitDuckHuntShot(DuckHuntGalleryLayout.ScenePoint(.5, .78, aspect), now) &&
                    scene.GalleryState is { Phase: DuckHuntGalleryPhase.Departing, HitTargets: 0 },
                    "A belt miss failed to start lower cleanup.");
            }
            if (!shot && scene.GalleryState.MiddlePhase == DuckHuntGalleryMiddlePhase.WaitingForHit)
            {
                var quad = DuckHuntGalleryLayout.MiddleTargetQuad(scene.GalleryState, aspect);
                var aim = quad.Map(DuckHuntGalleryLayout.PaperCenterU - .24 * DuckHuntGalleryLayout.PaperRadiusU,
                    DuckHuntGalleryLayout.PaperCenterV + .18 * DuckHuntGalleryLayout.PaperRadiusV);
                Check(scene.SubmitDuckHuntShot(aim, now) && scene.GalleryPunctures.Count == 1 &&
                    scene.GalleryPunctures[0].TargetIndex == DuckHuntGalleryGame.MiddlePunctureOffset,
                    "The preview shot did not puncture its moving-stage paper.");
                shot = true;
                firstHitAt = now;
            }
            Draw();
            var state = scene.GalleryState;
            if (firstHitAt is { } hitAt && state.MiddleTargetIndex == 0)
            {
                if (now < hitAt + DuckHuntGalleryGame.MiddleHitLingerDuration)
                    Check(state.MiddlePhase == DuckHuntGalleryMiddlePhase.WaitingForHit,
                        "The preview target turned away before its half-second hit linger.");
                else if (!hitTurnedAway)
                {
                    Check(state.MiddlePhase == DuckHuntGalleryMiddlePhase.TurningAway &&
                        now - hitAt - DuckHuntGalleryGame.MiddleHitLingerDuration < TimeSpan.FromSeconds(1d / fps),
                        "The preview target did not begin turning at its half-second hit deadline.");
                    hitTurnedAway = true;
                }
            }
            observed.Add(state.MiddlePhase);
            if (state.MiddlePhase != previous)
            {
                transitions.Add(new { Frame = frame, Seconds = frame / (double)fps,
                    Phase = state.MiddlePhase.ToString(), state.MiddleTargetIndex });
                previous = state.MiddlePhase;
            }
            if (frame % 30 == 0)
            {
                var input = scene.GetDuckHuntInputContext() ?? throw new InvalidOperationException("No gallery optical mapping.");
                var dot = new RedDotDetector().Detect(width, height, width * 4, target.GetPixelBytes(), input.CameraToBoard);
                Check(!dot.Detected && dot.CandidateCount == 0, $"Preview frame {frame} generated a false red-dot candidate.");
                opticalChecks++;
            }
            await target.SaveAsync(Path.Combine(directory, $"frame-{frame:D4}.jpg"), CanvasBitmapFileFormat.Jpeg);
        }
        Check(shot && hitTurnedAway && observed.Contains(DuckHuntGalleryMiddlePhase.Raising) &&
            observed.Contains(DuckHuntGalleryMiddlePhase.Entering) &&
            observed.Contains(DuckHuntGalleryMiddlePhase.TurningToFace) &&
            observed.Contains(DuckHuntGalleryMiddlePhase.WaitingForHit) &&
            observed.Contains(DuckHuntGalleryMiddlePhase.TurningAway) &&
            observed.Contains(DuckHuntGalleryMiddlePhase.Leaving) &&
            scene.GalleryState is { MiddleTargetIndex: 1, MiddlePhase: DuckHuntGalleryMiddlePhase.WaitingForHit },
            "The continuous preview did not shorten the hit target while preserving the unhit second target's facing window.");
        await CheckCountdown();
        return new { Ok = true, Assertions = assertions, OpticalChecks = opticalChecks, Directory = directory,
            Width = width, Height = height, Fps = fps, Frames = frames, Transitions = transitions,
            TimingScope = "Native compositor frames at a synthetic 60 Hz clock; not live projector pacing." };

        async Task CheckCountdown()
        {
            foreach (var size in new[] { (Name: "4k", Width: 3840, Height: 2160),
                (Name: "landscape", Width: 1920, Height: 1080), (Name: "square", Width: 1000, Height: 1000),
                (Name: "portrait", Width: 1080, Height: 1920) })
            {
                double boardAspect = size.Width / (double)size.Height;
                var clock = MonotonicClock.UtcNow;
                using var gallery = new SceneCompositor(blackjackClock: () => clock, boardRevealClock: () => clock);
                using var boardImage = new CanvasRenderTarget(target.Device, size.Width, size.Height, 96);
                using var ringImage = new CanvasRenderTarget(target.Device, size.Width, size.Height, 96);
                gallery.SetDisplayAspect(boardAspect);
                gallery.SetBoardSetup(true);
                gallery.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
                    Homography.FromFourPoints([new(0, 0), new(size.Width, 0), new(size.Width, size.Height), new(0, size.Height)],
                        [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
                gallery.SetBoardSetup(false);
                clock = MonotonicClock.UtcNow;
                gallery.ShowDuckHuntTargetPractice();
                await gallery.EnsureBoardArtworkResourcesAsync(boardImage.Device, BoardScreen.DuckHunt);
                clock = MonotonicClock.UtcNow.AddSeconds(1);
                gallery.SetDuckHuntAudioStatus(true, "Isolated countdown fixture");
                RenderBoard();
                Check(ActivateGalleryPlay(gallery), "Countdown fixture could not begin the gallery.");
                clock += TimeSpan.FromSeconds(4);
                RenderBoard();
                Check(!gallery.SubmitDuckHuntShot(DuckHuntGalleryLayout.ScenePoint(.5, .19, boardAspect), clock),
                    "Countdown fixture upper-background miss scored.");
                clock += TimeSpan.FromMilliseconds(1);
                Check(!gallery.SubmitDuckHuntShot(DuckHuntGalleryLayout.ScenePoint(.5, .78, boardAspect), clock),
                    "Countdown fixture conveyor miss scored.");
                var faceAt = clock + DuckHuntGalleryGame.DepartDuration + DuckHuntGalleryGame.FallDuration +
                    DuckHuntGalleryGame.ClosingDuration + DuckHuntGalleryGame.MiddleRaiseDuration +
                    DuckHuntGalleryGame.MiddleEnterDuration + DuckHuntGalleryGame.MiddleTurnDuration;
                clock = faceAt;
                RenderBoard();
                Check(gallery.GalleryState.MiddlePhase == DuckHuntGalleryMiddlePhase.WaitingForHit,
                    "Countdown fixture did not reach the first facing target.");
                var fullState = gallery.GalleryState;
                byte[] full = RingPixels(fullState);
                int fullGold = Gold(full);
                Check(fullGold > 20, "The facing target has no visible countdown ring.");
                CheckRingBounds(full);
                await SaveCountdown("full");
                foreach (var phase in new[] { DuckHuntGalleryMiddlePhase.Closed, DuckHuntGalleryMiddlePhase.Raising,
                    DuckHuntGalleryMiddlePhase.Entering, DuckHuntGalleryMiddlePhase.TurningToFace,
                    DuckHuntGalleryMiddlePhase.TurningAway, DuckHuntGalleryMiddlePhase.Leaving,
                    DuckHuntGalleryMiddlePhase.Lowering, DuckHuntGalleryMiddlePhase.Complete })
                    Check(Ink(RingPixels(fullState with { MiddlePhase = phase })) == 0,
                        $"The countdown ring appeared during {phase}.");
                clock = faceAt + TimeSpan.FromSeconds(1.5);
                RenderBoard();
                byte[] half = RingPixels(gallery.GalleryState);
                Check(Gold(half) < fullGold * .72 && Gold(half) > fullGold * .28,
                    "Half of the countdown arc did not disappear at 1.5 seconds.");
                await SaveCountdown("half");
                gallery.SetDuckHuntAudioStatus(false, "Isolated countdown pause");
                RenderBoard();
                var paused = gallery.GalleryState;
                byte[] pausedRing = RingPixels(paused);
                clock += TimeSpan.FromSeconds(2);
                RenderBoard();
                Check(paused.MiddleMotionProgress == gallery.GalleryState.MiddleMotionProgress &&
                    pausedRing.AsSpan().SequenceEqual(RingPixels(gallery.GalleryState)),
                    "The countdown ring advanced while input was paused.");
                gallery.SetDuckHuntAudioStatus(true, "Isolated countdown resume");
                RenderBoard();
                Check(pausedRing.AsSpan().SequenceEqual(RingPixels(gallery.GalleryState)),
                    "Resuming jumped the countdown ring forward.");
                faceAt += TimeSpan.FromSeconds(2);
                clock = faceAt + TimeSpan.FromSeconds(2.9);
                RenderBoard();
                int nearGold = Gold(RingPixels(gallery.GalleryState));
                Check(nearGold > 0 && nearGold < fullGold * .15,
                    "The countdown ring did not run nearly empty before its deadline.");
                await SaveCountdown("nearly-empty");
                var quad = DuckHuntGalleryLayout.MiddleTargetQuad(gallery.GalleryState, boardAspect);
                var aim = quad.Map(DuckHuntGalleryLayout.PaperCenterU, DuckHuntGalleryLayout.PaperCenterV);
                Check(gallery.SubmitDuckHuntShot(aim, clock), "Countdown fixture could not hit the facing target.");
                RenderBoard();
                Check(Ink(RingPixels(gallery.GalleryState)) == 0 &&
                    gallery.GalleryState.MiddlePhase == DuckHuntGalleryMiddlePhase.WaitingForHit,
                    "The countdown ring remained after a hit or changed the target's linger.");
                await SaveCountdown("hit");

                void RenderBoard()
                {
                    using var drawing = boardImage.CreateDrawingSession();
                    gallery.Draw(drawing, size.Width, size.Height, preview: false, runningSlowly: false);
                }
                byte[] RingPixels(DuckHuntGallerySnapshot state)
                {
                    using (var drawing = ringImage.CreateDrawingSession())
                    {
                        drawing.Clear(Microsoft.UI.Colors.Transparent);
                        drawing.Transform = Matrix3x2.CreateScale(size.Width / 1000f, size.Height / 1000f);
                        gallery.DrawGalleryMiddleCountdown(drawing, state,
                            DuckHuntGalleryLayout.MiddleTargetQuad(fullState, boardAspect));
                    }
                    return ringImage.GetPixelBytes();
                }
                void CheckRingBounds(byte[] pixels)
                {
                    var bounds = DuckHuntGalleryLayout.MiddleTargetQuad(fullState, boardAspect).Bounds;
                    double du = 1.5 / (size.Width * bounds.Width), dv = 1.5 / (size.Height * bounds.Height);
                    double edgeTolerance = Math.Max(du / DuckHuntGalleryLayout.PaperRadiusU,
                        dv / DuckHuntGalleryLayout.PaperRadiusV);
                    bool inPaper = true, outsideScoring = true, clearClips = true;
                    for (int y = 0; y < size.Height; y++)
                    for (int x = 0; x < size.Width; x++)
                    {
                        if (pixels[(y * size.Width + x) * 4 + 3] <= 200) continue;
                        var local = new BoardAim(((x + .5) / size.Width - bounds.X) / bounds.Width,
                            ((y + .5) / size.Height - bounds.Y) / bounds.Height);
                        double dx = (local.U - DuckHuntGalleryLayout.PaperCenterU) / DuckHuntGalleryLayout.PaperRadiusU;
                        double dy = (local.V - DuckHuntGalleryLayout.PaperCenterV) / DuckHuntGalleryLayout.PaperRadiusV;
                        inPaper &= Math.Sqrt(dx * dx + dy * dy) <= 1 + edgeTolerance;
                        outsideScoring &= DuckHuntGalleryLayout.PaperScore(local) == 0;
                        foreach (var clip in DuckHuntGalleryLayout.PaperClipBounds)
                            if (local.U > clip.X + du && local.U < clip.X + clip.Width - du &&
                                local.V > clip.Y + dv && local.V < clip.Y + clip.Height - dv)
                                clearClips = false;
                    }
                    Check(inPaper && outsideScoring && clearClips,
                        $"{size.Name} countdown extends outside the paper, into its scoring rings, or across a copper clip.");
                }
                async Task SaveCountdown(string stage)
                {
                    string stem = Path.Combine(directory, $"{size.Name}-countdown-{stage}");
                    await boardImage.SaveAsync(stem + ".png", CanvasBitmapFileFormat.Png);
                    var input = gallery.GetDuckHuntInputContext() ?? throw new InvalidOperationException("No countdown optical mapping.");
                    var dot = new RedDotDetector().Detect(size.Width, size.Height, size.Width * 4,
                        boardImage.GetPixelBytes(), input.CameraToBoard);
                    Check(!dot.Detected && dot.CandidateCount == 0, $"{size.Name} {stage} countdown generated a false red dot.");
                    opticalChecks++;
                    var map = (Homography)typeof(SceneCompositor).GetField("_boardSurfaceMap",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gallery)!;
                    var bounds = DuckHuntGalleryLayout.MiddleTargetQuad(fullState, boardAspect).Bounds;
                    var a = map.Transform(new(bounds.X, bounds.Y));
                    var b = map.Transform(new(bounds.X + bounds.Width, bounds.Y + bounds.Height));
                    int x = Math.Clamp((int)(a.X * size.Width) - 8, 0, size.Width - 1);
                    int y = Math.Clamp((int)(a.Y * size.Height) - 8, 0, size.Height - 1);
                    int cropWidth = Math.Min(size.Width - x, (int)Math.Ceiling((b.X - a.X) * size.Width) + 16);
                    int cropHeight = Math.Min(size.Height - y, (int)Math.Ceiling((b.Y - a.Y) * size.Height) + 16);
                    using var closeup = new CanvasRenderTarget(target.Device, cropWidth, cropHeight, 96);
                    using (var drawing = closeup.CreateDrawingSession())
                        drawing.DrawImage(boardImage, new Windows.Foundation.Rect(0, 0, cropWidth, cropHeight),
                            new Windows.Foundation.Rect(x, y, cropWidth, cropHeight));
                    await closeup.SaveAsync(stem + "-closeup.png", CanvasBitmapFileFormat.Png);
                }
            }

            static int Ink(byte[] pixels)
            {
                int count = 0;
                for (int p = 3; p < pixels.Length; p += 4) if (pixels[p] > 16) count++;
                return count;
            }
            static int Gold(byte[] pixels)
            {
                int count = 0;
                for (int p = 0; p < pixels.Length; p += 4)
                    // Include anti-aliased gold at small projector sizes while
                    // excluding the darker copper-coloured track (R = 64).
                    if (pixels[p + 3] > 100 && pixels[p] > 40 &&
                        pixels[p + 1] > 100 && pixels[p + 2] > 145) count++;
                return count;
            }
        }

        void Draw()
        {
            using var drawing = target.CreateDrawingSession();
            scene.Draw(drawing, width, height, preview: false, runningSlowly: false);
        }
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
