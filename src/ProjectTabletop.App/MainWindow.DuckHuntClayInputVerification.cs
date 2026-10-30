#if DEBUG
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Real timestamps through the production trained-click/photo gate. These
    // isolated scenes never acquire a camera, microphone, or projection window.
    private async Task<object> VerifyDuckHuntClayInputAsync()
    {
        const int width = 1280, height = 960;
        const double aspect = width / (double)height;
        int assertions = 0;
        var uiNow = MonotonicClock.UtcNow;
        using var scene = new SceneCompositor(blackjackClock: () => uiNow);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        scene.SetDisplayAspect(aspect);
        scene.SetBoardSetup(true);
        scene.SetDetectedBoardGrid([new(.06f, .05f), new(.96f, .09f), new(.91f, .95f), new(.09f, .90f)],
            Homography.FromFourPoints([new(0, 0), new(width, 0), new(width, height), new(0, height)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        uiNow = MonotonicClock.UtcNow;
        scene.ShowDuckHuntClayPigeon();
        await scene.EnsureBoardArtworkResourcesAsync(target.Device, BoardScreen.DuckHunt);
        Draw();
        var board = (BoardSession)typeof(SceneCompositor).GetField("_boardSession",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
        var game = (DuckHuntClayGame)typeof(BoardSession).GetField("_clay",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(board)!;
        var context = Context();
        Check(scene.DuckHuntBoardReady && scene.ClayState is { Phase: DuckHuntClayPhase.Ready, Paused: true },
            "Clay Pigeon launched before its trained microphone was available.");
        Check(!Snapshot(new(.5, .5), context.Epoch), "Clay Pigeon accepted a shot without microphone readiness.");
        scene.SetDuckHuntAudioStatus(true, "Isolated microphone fixture");
        Draw();
        Check(scene.ClayState is { Phase: DuckHuntClayPhase.Playing, Paused: false, Score: 0 },
            "Clay Pigeon did not start automatically when its existing input became ready.");
        Check(!board.DuckHuntDrawerOpen && board.Buttons.All(button => !button.Bounds.Contains(.5, .5)),
            "Clay Pigeon added a start control or opened its drawer before the round.");

        PrepareMovingPair();
        context = Context();
        scene.PublishDuckHuntDot(new(game.Targets[0].U, game.Targets[0].V), MonotonicClock.UtcNow, context.Epoch);
        Draw();
        Check(scene.ClayState.Score == 0 && game.Targets[0] is { Visible: true, Hit: false },
            "A red dot without a trained click shattered a clay.");
        Check(!Snapshot(null, context.Epoch) && scene.ClayState.Score == 0,
            "A no-dot click scored a clay hit.");
        Check(!Snapshot(new(game.Targets[0].U, game.Targets[0].V), context.Epoch - 1),
            "An obsolete camera epoch supplied a clay shot.");
        var stale = MonotonicClock.UtcNow.AddSeconds(-1);
        Check(!scene.SubmitDuckHuntSnapshot(new(game.Targets[0].U, game.Targets[0].V), stale, stale, stale, context.Epoch),
            "An expired camera photo supplied a clay shot.");
        Check(Snapshot(new(.05, .15), context.Epoch) && scene.ClayState.Score == 0,
            "An off-target camera photo scored a clay hit.");
        Check(ShotAtClay(0, context.Epoch) && scene.ClayState.Score == 1 && game.Targets[0].Hit && !game.Targets[1].Hit,
            "A fresh camera photo failed to hit exactly its moving clay.");
        var struck = game.Targets[0];
        Check(Snapshot(new(struck.U, struck.V), context.Epoch) && scene.ClayState.Score == 1,
            "The already shattered clay scored a second time.");

        scene.SetDuckHuntAudioStatus(false, "Isolated unavailable microphone");
        Draw();
        Check(scene.ClayState.Paused && !ShotAtClay(1, context.Epoch) && scene.ClayState.Score == 1,
            "The moving clay accepted a shot after microphone readiness was lost.");
        scene.SetDuckHuntAudioStatus(true, "Isolated microphone fixture");
        scene.SetDuckHuntLearningActive(true);
        Draw();
        Check(scene.ClayState.Paused && scene.GetDuckHuntInputContext() is null && !ShotAtClay(1, context.Epoch),
            "Click learning failed to pause the clay round and suppress shooting.");
        scene.SetDuckHuntLearningActive(false);
        Draw();
        var learned = Context();
        Check(learned.Epoch != context.Epoch && !Snapshot(new(struck.U, struck.V), context.Epoch),
            "Completing click learning reused work from the old epoch.");
        Check(ShotAtClay(1, learned.Epoch) && scene.ClayState.Score == 2,
            "The second clay could not be hit after input resumed.");

        // Move the bounded game clock to its final round without waiting a
        // minute. The live photo gate itself retains the host's real clock.
        var began = MonotonicClock.UtcNow - DuckHuntClayGame.RoundDuration - TimeSpan.FromSeconds(1);
        game.Reset(began);
        game.SetReady(true, began);
        Draw();
        Check(scene.ClayState.RoundFinished && board.DuckHuntDrawerOpen,
            "The completed clay round failed to open its replay drawer.");
        await Task.Delay(350);
        Draw();
        context = Context();
        uiNow = MonotonicClock.UtcNow;
        Check(board.ActivateButton("duck-hunt-play-again", uiNow), "The completed round could not activate PLAY AGAIN.");
        Draw();
        var replay = Context();
        Check(replay.Epoch != context.Epoch && !Snapshot(new(.5, .5), context.Epoch) &&
            scene.ClayState is { Phase: DuckHuntClayPhase.Playing, Score: 0, PairsLaunched: 0 },
            "Replay accepted a previous-round photo or retained its score.");

        uiNow = MonotonicClock.UtcNow;
        scene.ShowDuckHunt();
        Check(!scene.DuckHuntBoardReady && scene.GetDuckHuntInputContext() is null && !Snapshot(new(.5, .5), replay.Epoch),
            "The welcome screen retained clay shooting input.");
        uiNow = MonotonicClock.UtcNow;
        scene.ShowDuckHuntClayPigeon();
        Draw();
        var reopened = Context();
        Check(reopened.Epoch != replay.Epoch && !Snapshot(new(.5, .5), replay.Epoch),
            "Reopening Clay Pigeon reused a previous view's photo.");
        scene.SetCameraCheckRequired(true);
        Check(scene.ClayState.Paused && !scene.DuckHuntBoardReady &&
            scene.GetDuckHuntInputContext() is null && !Snapshot(new(.5, .5), reopened.Epoch),
            "Camera recovery allowed shooting or left the clay clock running.");
        scene.SetCameraCheckRequired(false);
        Draw();
        var recovered = Context();
        Check(!scene.ClayState.Paused && recovered.Epoch != reopened.Epoch,
            "Camera recovery failed to resume with a fresh input epoch.");
        scene.SetBoardSetup(true);
        Draw();
        Check(!scene.DuckHuntBoardReady && scene.ClayState.Paused &&
            scene.GetDuckHuntInputContext() is null && !Snapshot(new(.5, .5), recovered.Epoch),
            "Board calibration allowed a clay shot or continued the round.");
        return new { Ok = true, Assertions = assertions, RealClockSnapshotGate = true,
            AutomaticStart = true, PhotoTimeMovingHits = 2, ReplayInvalidatesEpoch = true };

        void PrepareMovingPair()
        {
            var began = MonotonicClock.UtcNow.AddSeconds(-2);
            game.Reset(began);
            game.SetReady(true, began);
            Draw();
        }
        SceneCompositor.DuckHuntInputContext Context() => scene.GetDuckHuntInputContext() ??
            throw new InvalidOperationException("Clay Pigeon has no calibrated shot context.");
        bool Snapshot(BoardAim? aim, long epoch)
        {
            var click = MonotonicClock.UtcNow;
            var frame = LaterThan(click);
            uiNow = frame;
            return scene.SubmitDuckHuntSnapshot(aim, frame, click, LaterThan(frame), epoch);
        }
        bool ShotAtClay(int index, long epoch)
        {
            var click = MonotonicClock.UtcNow;
            var frame = LaterThan(click);
            uiNow = frame;
            game.Advance(frame);
            var clay = game.Targets[index];
            return scene.SubmitDuckHuntSnapshot(new(clay.U, clay.V), frame, click, LaterThan(frame), epoch);
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
