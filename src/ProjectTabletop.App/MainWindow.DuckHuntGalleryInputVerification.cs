#if DEBUG
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Exercise the real click-photo gate without opening any capture device.
    private async Task<object> VerifyDuckHuntInputAsync()
    {
        const int width = 1280, height = 960;
        const double displayAspect = width / (double)height;
        int assertions = 0;
        var uiNow = MonotonicClock.UtcNow;
        using var scene = new SceneCompositor(blackjackClock: () => uiNow);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        scene.SetDisplayAspect(displayAspect);
        scene.SetBoardSetup(true);
        scene.SetDetectedBoardGrid([new(.06f, .05f), new(.96f, .09f), new(.91f, .95f), new(.09f, .90f)],
            Homography.FromFourPoints([new(0, 0), new(width, 0), new(width, height), new(0, height)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        // This deliberately skewed calibration has a different physical aspect
        // from the window. Use the same paper plane that the compositor renders.
        double aspect = (double)typeof(SceneCompositor).GetMethod("PaintBoardAspect",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scene, null)!;
        scene.ShowDuckHuntTargetPractice();
        await scene.EnsureBoardArtworkResourcesAsync(target.Device, BoardScreen.DuckHunt);
        scene.SetDuckHuntAudioStatus(true, "Isolated microphone fixture");
        Draw();
        var board = (BoardSession)typeof(SceneCompositor).GetField("_boardSession",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
        var game = (DuckHuntGalleryGame)typeof(BoardSession).GetField("_gallery",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(board)!;
        var playBounds = DuckHuntGalleryLayout.PlayBounds;
        var play = new BoardAim(playBounds.X + playBounds.Width / 2, playBounds.Y + playBounds.Height / 2);
        var context = Context();
        Check(scene.DuckHuntBoardReady && scene.GalleryState is
            { Phase: DuckHuntGalleryPhase.Ready, Paused: false }, "Gallery input cannot wait for PLAY.");
        scene.PublishDuckHuntDot(play, MonotonicClock.UtcNow, context.Epoch);
        Draw();
        Check(scene.GalleryState.PlayVisible && scene.GalleryPunctures.Count == 0,
            "Aim alone started the gallery or marked the wall.");
        Check(!scene.ActivateDuckHuntAt(play.U, play.V) &&
            board.Buttons.All(button => !button.Bounds.Contains(play.U, play.V)),
            "PLAY is reachable through ordinary button input.");
        Check(!Snapshot(null, context.Epoch) && scene.GalleryState.PlayVisible,
            "A no-dot click activated PLAY.");
        Check(Snapshot(new(.05, .20), context.Epoch) && scene.GalleryState.PlayVisible,
            "An off-target click activated PLAY.");
        Check(!Snapshot(play, context.Epoch - 1), "An old input epoch activated PLAY.");
        var stale = MonotonicClock.UtcNow.AddSeconds(-1);
        Check(!scene.SubmitDuckHuntSnapshot(play, stale, stale, stale, context.Epoch),
            "An expired click photo activated PLAY.");

        scene.SetDuckHuntAudioStatus(false, "Microphone unavailable");
        Check(!Snapshot(play, context.Epoch), "PLAY accepted a click without a microphone.");
        scene.SetDuckHuntAudioStatus(true, "Isolated microphone fixture");
        scene.SetDuckHuntLearningActive(true);
        Check(scene.GetDuckHuntInputContext() is null && !Snapshot(play, context.Epoch),
            "Learning allowed a gallery shot.");
        scene.SetDuckHuntLearningActive(false);
        Draw();
        var learned = Context();
        Check(learned.Epoch != context.Epoch && !Snapshot(play, context.Epoch),
            "Learning reused previous click work.");
        context = learned;
        Check(Snapshot(play, context.Epoch) && scene.GalleryState is
            { Phase: DuckHuntGalleryPhase.Opening, Cycle: 1, HitTargets: 0 },
            "A fresh click photo on PLAY did not open the gallery.");
        Check(!board.DuckHuntDrawerOpen, "Starting the gallery left its drawer open.");
        var roundContext = Context();
        Check(roundContext.Epoch != context.Epoch && !Snapshot(play, context.Epoch),
            "PLAY did not invalidate earlier click work.");

        // Backdate only the deterministic game fixture. Camera/audio timestamps
        // still pass through the production gate using the real host clock.
        var began = MonotonicClock.UtcNow.AddSeconds(-10);
        game.Reset(began);
        game.SetReady(true, began);
        Check(game.Shoot(play, began, aspect), "Could not prepare the paper-target fixture.");
        Draw();
        context = Context();
        Check(scene.GalleryState.Phase == DuckHuntGalleryPhase.WaitingForHits,
            "The stationary paper fixture is not ready.");
        var paperQuad = DuckHuntGalleryLayout.TargetQuad(0, scene.GalleryState, aspect);
        double paperU = DuckHuntGalleryLayout.PaperCenterU - .24 * DuckHuntGalleryLayout.PaperRadiusU;
        double paperV = DuckHuntGalleryLayout.PaperCenterV + .18 * DuckHuntGalleryLayout.PaperRadiusV;
        var paper = paperQuad.Map(paperU, paperV);
        Check(!Snapshot(null, context.Epoch) && scene.GalleryPunctures.Count == 0,
            "A no-dot click punctured a paper target.");
        Check(Snapshot(paperQuad.Map(.02, .02), context.Epoch) && scene.GalleryPunctures.Count == 0 &&
            scene.GalleryState.HitTargets == 0, "The rectangular sprite corner counted as round paper.");
        var holder = paperQuad.Map(DuckHuntGalleryLayout.PaperCenterU,
            (1 + DuckHuntGalleryLayout.PaperCenterV + DuckHuntGalleryLayout.PaperRadiusV) / 2);
        Check(Snapshot(holder, context.Epoch) && scene.GalleryPunctures.Count == 0 &&
            scene.GalleryState.HitTargets == 0, "The metal holder counted as paper.");
        Check(Snapshot(paper, context.Epoch) && scene.GalleryState.HitTargets == 1 &&
            scene.GalleryPunctures.Count == 1, "A valid click photo did not puncture the paper.");
        var puncture = scene.GalleryPunctures[0];
        Check(puncture.TargetIndex == 0 && Math.Abs(puncture.U - paperU) < 1e-6 &&
            Math.Abs(puncture.V - paperV) < 1e-6, "The hole is not attached to the photographed paper coordinates.");
        Check(Snapshot(paper, context.Epoch) && scene.GalleryState.HitTargets == 1 &&
            scene.GalleryPunctures.Count == 2, "A repeated target hit changed the distinct-target count.");
        Check(!Snapshot(null, context.Epoch) && scene.GalleryPunctures.Count == 2,
            "A missing dot altered the existing holes.");

        scene.SetDuckHuntAudioStatus(false, "Microphone unavailable");
        Draw();
        Check(scene.GalleryState.Paused && !Snapshot(paper, context.Epoch) && scene.GalleryPunctures.Count == 2,
            "Microphone loss failed to pause or preserve the gallery.");
        scene.SetDuckHuntAudioStatus(true, "Isolated microphone fixture");
        Draw();
        Check(!scene.GalleryState.Paused && scene.GalleryPunctures.Count == 2,
            "Microphone recovery reset the target row.");
        uiNow = MonotonicClock.UtcNow;
        scene.ShowDuckHunt();
        Check(!scene.DuckHuntBoardReady && scene.GetDuckHuntInputContext() is null &&
            !Snapshot(paper, context.Epoch), "Returning to the welcome screen retained shooting input.");
        uiNow = MonotonicClock.UtcNow;
        scene.ShowDuckHuntTargetPractice();
        Draw();
        var reopened = Context();
        Check(reopened.Epoch != context.Epoch && !Snapshot(paper, context.Epoch) &&
            scene.GalleryState is { Phase: DuckHuntGalleryPhase.Ready, Cycle: 0 } &&
            scene.GalleryPunctures.Count == 0, "Reopening retained a previous cycle or shot.");
        scene.SetBoardSetup(true);
        Check(!scene.DuckHuntBoardReady && !Snapshot(play, reopened.Epoch),
            "Calibration allowed a gallery shot.");
        return new { Ok = true, Assertions = assertions, RealClockSnapshotGate = true };

        SceneCompositor.DuckHuntInputContext Context() => scene.GetDuckHuntInputContext() ??
            throw new InvalidOperationException("The gallery has no calibrated shot context.");
        bool Snapshot(BoardAim? aim, long epoch)
        {
            var click = MonotonicClock.UtcNow;
            var frame = LaterThan(click);
            uiNow = frame;
            return scene.SubmitDuckHuntSnapshot(aim, frame, click, LaterThan(frame), epoch);
        }
        void Draw()
        {
            uiNow = MonotonicClock.UtcNow;
            using var drawing = target.CreateDrawingSession();
            scene.Draw(drawing, width, height, false, false);
        }
        static DateTimeOffset LaterThan(DateTimeOffset previous)
        {
            DateTimeOffset next;
            do { next = MonotonicClock.UtcNow; } while (next <= previous);
            return next;
        }
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
