#if DEBUG
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
            }
            Draw();
            var state = scene.GalleryState;
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
        Check(shot && observed.Contains(DuckHuntGalleryMiddlePhase.Raising) &&
            observed.Contains(DuckHuntGalleryMiddlePhase.Entering) &&
            observed.Contains(DuckHuntGalleryMiddlePhase.TurningToFace) &&
            observed.Contains(DuckHuntGalleryMiddlePhase.WaitingForHit) &&
            observed.Contains(DuckHuntGalleryMiddlePhase.TurningAway) &&
            observed.Contains(DuckHuntGalleryMiddlePhase.Leaving) && scene.GalleryState.MiddleTargetIndex == 1,
            "The continuous preview did not include the full first target cycle and second arrival.");
        return new { Ok = true, Assertions = assertions, OpticalChecks = opticalChecks, Directory = directory,
            Width = width, Height = height, Fps = fps, Frames = frames, Transitions = transitions,
            TimingScope = "Native compositor frames at a synthetic 60 Hz clock; not live projector pacing." };

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
