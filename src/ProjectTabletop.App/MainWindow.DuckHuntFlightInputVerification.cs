#if DEBUG
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Real host timestamps through the production photo gate; no camera, microphone,
    // app window or output session is created by this isolated verification.
    private async Task<object> VerifyDuckHuntFlightInputAsync()
    {
        const int width = 1280, height = 960;
        int assertions = 0;
        var uiNow = MonotonicClock.UtcNow;
        using var scene = new SceneCompositor(blackjackClock: () => uiNow);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        scene.SetDisplayAspect(width / (double)height);
        scene.SetBoardSetup(true);
        scene.SetDetectedBoardGrid([new(.06f, .05f), new(.96f, .09f), new(.91f, .95f), new(.09f, .90f)],
            Homography.FromFourPoints([new(0, 0), new(width, 0), new(width, height), new(0, height)],
                [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        scene.SetBoardSetup(false);
        uiNow = MonotonicClock.UtcNow;
        scene.ShowDuckHuntHunt();
        await scene.EnsureBoardArtworkResourcesAsync(target.Device, BoardScreen.DuckHunt);
        scene.SetDuckHuntAudioStatus(true, "Isolated microphone fixture");
        Draw();
        var board = (BoardSession)typeof(SceneCompositor).GetField("_boardSession",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
        var game = (DuckHuntFlightGame)typeof(BoardSession).GetField("_huntFlight",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(board)!;
        var context = Context();
        var start = new BoardAim(.5, .5);
        Check(scene.DuckHuntBoardReady && scene.HuntFlightState is
            { Phase: DuckHuntFlightPhase.Ready, Paused: false }, "HUNT cannot listen while awaiting START.");
        scene.PublishDuckHuntDot(start, MonotonicClock.UtcNow, context.Epoch);
        Draw();
        Check(scene.HuntFlightState.StartVisible && scene.HuntFlightState.BirdsLaunched == 0,
            "Aim alone activated START.");
        Check(!scene.ActivateDuckHuntAt(start.U, start.V) &&
            board.Buttons.All(button => !button.Bounds.Contains(start.U, start.V)),
            "START is reachable by ordinary button activation.");
        Check(!Snapshot(null, context.Epoch) && scene.HuntFlightState.StartVisible,
            "A click without a red dot activated START.");
        Check(Snapshot(new(.05, .20), context.Epoch) && scene.HuntFlightState.StartVisible,
            "A valid off-target shot activated START.");
        Check(!Snapshot(start, context.Epoch - 1), "An obsolete input epoch activated START.");
        var stale = MonotonicClock.UtcNow.AddSeconds(-1);
        Check(!scene.SubmitDuckHuntSnapshot(start, stale, stale, stale, context.Epoch),
            "An expired camera image activated START.");

        scene.SetDuckHuntAudioStatus(false, "Microphone unavailable");
        Check(!Snapshot(start, context.Epoch) && scene.HuntFlightState.StartVisible,
            "START accepted a shot with no microphone.");
        scene.SetDuckHuntAudioStatus(true, "Isolated microphone fixture");
        scene.SetDuckHuntLearningActive(true);
        Check(scene.GetDuckHuntInputContext() is null && !Snapshot(start, context.Epoch),
            "Click learning supplied a HUNT shot.");
        scene.SetDuckHuntLearningActive(false);
        Draw();
        var learned = Context();
        Check(learned.Epoch != context.Epoch && !Snapshot(start, context.Epoch),
            "Finishing learning reused old click work.");
        context = learned;
        Check(Snapshot(start, context.Epoch) && scene.HuntFlightState is
            { Phase: DuckHuntFlightPhase.Launching, Score: 0, BirdsLaunched: 0 },
            "A fresh click photo on START did not begin HUNT.");
        Check(!board.DuckHuntDrawerOpen, "Starting HUNT left the drawer open.");
        var roundContext = Context();
        Check(roundContext.Epoch != context.Epoch && !Snapshot(start, context.Epoch),
            "START did not invalidate earlier shot work.");

        // Place a real flight in progress without waiting or changing the camera
        // gate's clock. Its current pose is then evaluated at the fresh frame time.
        var began = MonotonicClock.UtcNow.AddSeconds(-2);
        game.Reset(began);
        game.SetReady(true, began);
        Check(game.Shoot(start, began, width / (double)height), "Could not prepare the moving-bird fixture.");
        Draw();
        context = Context();
        Check(!Snapshot(null, context.Epoch) && scene.HuntFlightState.Score == 0,
            "A no-dot click scored a moving-bird hit.");
        Check(ShotAtBird(context.Epoch) && scene.HuntFlightState is
            { Phase: DuckHuntFlightPhase.Hit, Score: 1 },
            "A fresh camera photo did not hit its moving bird.");
        var frozen = scene.HuntFlightState;
        Check(Snapshot(new(frozen.U, frozen.V), context.Epoch) && scene.HuntFlightState.Score == 1,
            "A second click scored the frozen bird twice.");

        await CheckConfirmationPhoto(ambiguous: true);
        await CheckConfirmationPhoto(ambiguous: false);
        context = Context();

        uiNow = MonotonicClock.UtcNow;
        scene.ShowDuckHunt();
        Check(!scene.DuckHuntBoardReady && scene.GetDuckHuntInputContext() is null &&
            !Snapshot(start, context.Epoch), "Returning to the welcome screen retained shooting input.");
        uiNow = MonotonicClock.UtcNow;
        scene.ShowDuckHuntHunt();
        Draw();
        var reopened = Context();
        Check(reopened.Epoch != context.Epoch && !Snapshot(start, context.Epoch) &&
            scene.HuntFlightState is { Phase: DuckHuntFlightPhase.Ready, Score: 0 },
            "Reopening HUNT reused a previous round or shot.");
        scene.SetBoardSetup(true);
        Check(!scene.DuckHuntBoardReady && !Snapshot(start, reopened.Epoch),
            "Calibration allowed a HUNT shot.");
        return new { Ok = true, Assertions = assertions, RealClockSnapshotGate = true,
            AdjacentPhotoFallback = true, SelectedHistoricalPose = true, MissingDotNeverBorrowsAim = true,
            Scope = "Production scene/tracker/game path; camera acquisition, audio callback and MainWindow reporting are not invoked." };

        async Task CheckConfirmationPhoto(bool ambiguous)
        {
            scene.ClearDuckHuntInput();
            var began = MonotonicClock.UtcNow.AddSeconds(-2);
            game.Reset(began);
            game.SetReady(true, began);
            Check(game.Shoot(start, began, width / (double)height),
                "Could not prepare the adjacent-photo flight fixture.");
            Draw();
            var input = Context();
            // Place both images inside a real, already elapsed click interval.
            // Using fixed past offsets keeps scheduler jitter out of the 50 ms
            // predecessor limit while still exercising the production host clock.
            await Task.Delay(60);
            var confirmed = MonotonicClock.UtcNow;
            var onset = confirmed - TimeSpan.FromMilliseconds(40);
            var previousPhoto = confirmed - TimeSpan.FromMilliseconds(25);
            var currentPhoto = confirmed - TimeSpan.FromMilliseconds(5);
            game.Advance(previousPhoto);
            var photographed = game.Snapshot;
            var bounds = DuckHuntFlightLayout.SpriteBounds(photographed, width / (double)height);
            double bodyU = photographed.Direction > 0 ? DuckHuntFlightLayout.BodyCenterU : 1 - DuckHuntFlightLayout.BodyCenterU;
            var aim = new BoardAim(bounds.X + bodyU * bounds.Width,
                bounds.Y + DuckHuntFlightLayout.BodyCenterV * bounds.Height);
            scene.PublishDuckHuntDot(aim, previousPhoto, input.Epoch);
            uiNow = currentPhoto;
            game.Advance(currentPhoto);
            Check(game.Snapshot.U != photographed.U && game.Snapshot.FlapPhase != photographed.FlapPhase,
                "The adjacent-photo fixture did not advance the bird after its earlier image.");

            bool accepted = scene.SubmitDuckHuntSnapshot(null, currentPhoto, onset, confirmed,
                input.Epoch, ambiguous, out var selected);
            if (ambiguous)
            {
                Check(accepted && selected is { } shot && shot.Aim == aim && shot.ClickedAt == previousPhoto,
                    "The ambiguous confirmation photo did not select its immediate post-click predecessor.");
                var hit = scene.HuntFlightState;
                Check(hit is { Phase: DuckHuntFlightPhase.Hit, Score: 1, Shots: 1 } &&
                    hit.U == photographed.U && hit.V == photographed.V && hit.FlapPhase == photographed.FlapPhase,
                    "The recovered shot used a later bird pose or dispatched more than one hit.");
                var measured = scene.DuckHuntMeasurement;
                Check(measured.NewDetectedXY == aim && measured.NewFrameTimestamp == previousPhoto && measured.ClickOnset == onset,
                    "Shot diagnostics did not retain the selected photo's coordinates and timestamp.");
                Check(!scene.SubmitDuckHuntSnapshot(null, currentPhoto, onset, confirmed,
                        input.Epoch, true, out var duplicate) && duplicate is null &&
                    scene.HuntFlightState is { Score: 1, Shots: 1 },
                    "The same ambiguous click dispatched a second recovered shot.");
            }
            else
            {
                Check(!accepted && selected is null && scene.HuntFlightState is { Score: 0, Shots: 0 },
                    "A clear no-dot photo borrowed the preceding aim.");
                Check(!scene.SubmitDuckHuntSnapshot(aim, currentPhoto, onset, confirmed,
                        input.Epoch, false, out _) && scene.HuntFlightState is { Score: 0, Shots: 0 },
                    "A rejected no-dot click could be retried with a different photo result.");
            }
        }

        SceneCompositor.DuckHuntInputContext Context() => scene.GetDuckHuntInputContext() ??
            throw new InvalidOperationException("HUNT has no calibrated shot context.");

        bool Snapshot(BoardAim? aim, long epoch)
        {
            var click = MonotonicClock.UtcNow;
            var frame = LaterThan(click);
            uiNow = frame;
            return scene.SubmitDuckHuntSnapshot(aim, frame, click, LaterThan(frame), epoch);
        }

        bool ShotAtBird(long epoch)
        {
            var click = MonotonicClock.UtcNow;
            var frame = LaterThan(click);
            uiNow = frame;
            game.Advance(frame);
            var bird = game.Snapshot;
            var bounds = DuckHuntFlightLayout.SpriteBounds(bird, width / (double)height);
            double bodyU = bird.Direction > 0 ? DuckHuntFlightLayout.BodyCenterU : 1 - DuckHuntFlightLayout.BodyCenterU;
            var aim = new BoardAim(bounds.X + bodyU * bounds.Width,
                bounds.Y + DuckHuntFlightLayout.BodyCenterV * bounds.Height);
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
