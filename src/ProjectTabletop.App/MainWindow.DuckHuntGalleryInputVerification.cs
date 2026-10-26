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
        uiNow = MonotonicClock.UtcNow; // Calibration advances the real input timestamp.
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
        var playBounds = BoardSession.DuckHuntPlayBounds;
        var play = new BoardAim(playBounds.X + playBounds.Width / 2, playBounds.Y + playBounds.Height / 2);
        var context = Context();
        Check(scene.DuckHuntBoardReady && scene.GalleryState is
            { Phase: DuckHuntGalleryPhase.Ready, Paused: false }, "Gallery input cannot wait for PLAY.");
        scene.PublishDuckHuntDot(play, MonotonicClock.UtcNow, context.Epoch);
        Draw();
        Check(scene.GalleryState.PlayVisible && scene.GalleryPunctures.Count == 0,
            "Aim alone started the gallery or marked the wall.");
        Check(board.DuckHuntDrawerOpen && board.Buttons.Any(button => button.Id == "duck-hunt-play" && button.Hold == BoardButtonHold.Once),
            "The initial drawer does not expose standard long-press PLAY.");
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
        Check(Snapshot(play, context.Epoch) && scene.GalleryState.PlayVisible && scene.GalleryState.Cycle == 0,
            "A fresh laser-and-click photo incorrectly activated the drawer PLAY button.");
        Check(Snapshot(new(.5, .46), context.Epoch) && scene.GalleryState.PlayVisible && scene.GalleryState.Cycle == 0,
            "A fresh laser-and-click photo activated the removed central PLAY target.");
        // Settle the injected UI clock without a wall-clock delay. The game is
        // separately backdated below before real camera-frame boundary checks.
        uiNow = MonotonicClock.UtcNow.AddMilliseconds(350);
        using (var drawing = target.CreateDrawingSession()) scene.Draw(drawing, width, height, false, false);
        Check(ActivateGalleryPlay(scene) && scene.GalleryState is
            { Phase: DuckHuntGalleryPhase.Opening, UpperPhase: DuckHuntGalleryUpperPhase.Raising,
                Cycle: 1, HitTargets: 0, SkullHitMask: 0 },
            "The regular drawer PLAY button did not start both shelves together.");
        Check(!board.DuckHuntDrawerOpen, "Starting the gallery left its drawer open.");
        var roundContext = Context();
        Check(roundContext.Epoch != context.Epoch && !Snapshot(play, context.Epoch),
            "PLAY did not invalidate earlier click work.");

        // Backdate only the deterministic game fixture. Camera/audio timestamps
        // still pass through the production gate using the real host clock.
        var began = MonotonicClock.UtcNow.AddSeconds(-10);
        game.Reset(began);
        game.SetReady(true, began);
        Check(game.Start(began), "Could not prepare the paper-target fixture.");
        Draw();
        context = Context();
        Check(scene.GalleryState is { Phase: DuckHuntGalleryPhase.WaitingForHits,
            UpperPhase: DuckHuntGalleryUpperPhase.WaitingForHits },
            "The two stationary target rows are not ready.");
        var skullBounds = DuckHuntGalleryLayout.SkullBounds(0, aspect);
        var skull = new BoardAim(skullBounds.X + skullBounds.Width / 2,
            skullBounds.Y + skullBounds.Height / 2);
        long roundRevision = board.DuckHuntRoundRevision;
        Check(!Snapshot(null, context.Epoch) && scene.GalleryState.SkullHitMask == 0,
            "A no-dot click shattered a skull.");
        Check(Snapshot(skull, context.Epoch) && scene.GalleryState.SkullHitMask == 1 &&
            scene.GalleryState.HitTargets == 0 && scene.GalleryPunctures.Count == 0,
            "A skull photo hit did not remain independent of the paper row.");
        Check(board.DuckHuntRoundRevision == roundRevision && Context().Epoch == context.Epoch,
            "A skull hit reset the round or invalidated its camera context.");
        var paperQuad = DuckHuntGalleryLayout.TargetQuad(0, scene.GalleryState, aspect);
        double paperU = DuckHuntGalleryLayout.PaperCenterU - .24 * DuckHuntGalleryLayout.PaperRadiusU;
        double paperV = DuckHuntGalleryLayout.PaperCenterV + .18 * DuckHuntGalleryLayout.PaperRadiusV;
        var paper = paperQuad.Map(paperU, paperV);
        Check(!Snapshot(null, context.Epoch) && scene.GalleryPunctures.Count == 0,
            "A no-dot click punctured a paper target.");
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
        var nextSkull = DuckHuntGalleryLayout.SkullBounds(1, aspect);
        Check(Snapshot(new(nextSkull.X + nextSkull.Width / 2, nextSkull.Y + nextSkull.Height / 2), context.Epoch) &&
            scene.GalleryState is { SkullHitMask: 3, HitTargets: 1 } && scene.GalleryPunctures.Count == 2,
            "Alternating back to the skull row interrupted the paper targets.");

        scene.SetDuckHuntAudioStatus(false, "Microphone unavailable");
        Draw();
        Check(scene.GalleryState.Paused && !Snapshot(paper, context.Epoch) && scene.GalleryPunctures.Count == 2,
            "Microphone loss failed to pause or preserve the gallery.");
        scene.SetDuckHuntAudioStatus(true, "Isolated microphone fixture");
        Draw();
        Check(!scene.GalleryState.Paused && scene.GalleryPunctures.Count == 2 && scene.GalleryState.SkullHitMask == 3,
            "Microphone recovery reset either target row.");

        // A real background hit now dismisses its row. Check those silhouette
        // misses after the ordinary hits so they cannot hide target coverage.
        Check(Snapshot(new(skullBounds.X, skullBounds.Y), context.Epoch) && scene.GalleryState is
            { UpperPhase: DuckHuntGalleryUpperPhase.Lowering, SkullHitMask: 3,
                Phase: DuckHuntGalleryPhase.WaitingForHits },
            "A transparent skull corner did not close only the upper row.");
        Check(Snapshot(skull, context.Epoch) && scene.GalleryState.SkullHitMask == 3,
            "A closing or destroyed skull counted as another target.");
        Check(!Snapshot(null, context.Epoch) && scene.GalleryState.Phase == DuckHuntGalleryPhase.WaitingForHits,
            "A no-dot click closed the paper row.");
        Check(Snapshot(paperQuad.Map(.02, .02), context.Epoch) && scene.GalleryPunctures.Count == 2 &&
            scene.GalleryState is { Phase: DuckHuntGalleryPhase.Departing, HitTargets: 1 },
            "A transparent paper corner counted as paper or failed to start belt cleanup.");
        Check(Snapshot(paper, context.Epoch) && scene.GalleryPunctures.Count == 2,
            "A departing paper accepted a new puncture.");
        Check(board.DuckHuntRoundRevision == roundRevision && Context().Epoch == context.Epoch,
            "A missed shot reset the round or invalidated the camera context.");

        // The renderer may already have begun the quarter-turn when a fresh
        // camera result arrives. Its photo timestamp, not render time, decides
        // whether the three-second facing window was still open.
        var boundaryClick = MonotonicClock.UtcNow;
        var boundaryFrame = LaterThan(boundaryClick);
        var faceDeadline = boundaryFrame + TimeSpan.FromTicks(1);
        var lowerMissAt = faceDeadline - DuckHuntGalleryGame.DepartDuration - DuckHuntGalleryGame.FallDuration -
            DuckHuntGalleryGame.ClosingDuration - DuckHuntGalleryGame.MiddleRaiseDuration -
            DuckHuntGalleryGame.MiddleEnterDuration - DuckHuntGalleryGame.MiddleTurnDuration - DuckHuntGalleryGame.MiddleFaceDuration;
        var boundaryBegan = lowerMissAt - TimeSpan.FromSeconds(4);
        game.Reset(boundaryBegan);
        game.SetReady(true, boundaryBegan);
        Check(game.Start(boundaryBegan), "Could not prepare the middle photo-boundary fixture.");
        game.Advance(lowerMissAt - TimeSpan.FromMilliseconds(1));
        game.Shoot(DuckHuntGalleryLayout.ScenePoint(.5, .19, aspect), lowerMissAt - TimeSpan.FromMilliseconds(1), aspect);
        game.Shoot(DuckHuntGalleryLayout.ScenePoint(.5, .78, aspect), lowerMissAt, aspect);
        game.Advance(boundaryFrame);
        Check(game.Snapshot.MiddlePhase == DuckHuntGalleryMiddlePhase.WaitingForHit,
            "The captured middle-boundary frame was not face-on.");
        var middleQuad = DuckHuntGalleryLayout.MiddleTargetQuad(game.Snapshot, aspect);
        var middleAim = middleQuad.Map(paperU, paperV);
        uiNow = faceDeadline + TimeSpan.FromMilliseconds(20);
        Check(scene.SubmitDuckHuntSnapshot(middleAim, boundaryFrame, boundaryClick, LaterThan(boundaryFrame), context.Epoch) &&
            scene.GalleryState.MiddlePhase == DuckHuntGalleryMiddlePhase.TurningAway &&
            scene.GalleryPunctures.Count == 1 && scene.GalleryPunctures[0].TargetIndex == DuckHuntGalleryGame.MiddlePunctureOffset,
            "A fresh face-on photo was lost because rendering had already begun turning away.");
        Check(scene.SubmitDuckHuntSnapshot(middleAim, faceDeadline, boundaryFrame, LaterThan(faceDeadline), context.Epoch) &&
            scene.GalleryPunctures.Count == 1,
            "A photo at the exact three-second deadline punctured the turning target.");
        Check(!Snapshot(null, context.Epoch) && scene.GalleryPunctures.Count == 1,
            "A missing middle-stage dot changed the last puncture.");

        uiNow = MonotonicClock.UtcNow;
        scene.ShowDuckHunt();
        Check(!scene.DuckHuntBoardReady && scene.GetDuckHuntInputContext() is null &&
            !Snapshot(paper, context.Epoch), "Returning to the welcome screen retained shooting input.");
        uiNow = MonotonicClock.UtcNow;
        scene.ShowDuckHuntTargetPractice();
        Draw();
        var reopened = Context();
        Check(reopened.Epoch != context.Epoch && !Snapshot(paper, context.Epoch) &&
            scene.GalleryState is { Phase: DuckHuntGalleryPhase.Ready, UpperPhase: DuckHuntGalleryUpperPhase.Closed,
                SkullHitMask: 0, Cycle: 0 } &&
            scene.GalleryPunctures.Count == 0, "Reopening retained a previous shelf, cycle or shot.");
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
