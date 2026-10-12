#if DEBUG
using System.Numerics;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.App.Projection.Football;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Private scenes exercise actual Win2D output and calibrated camera mapping
    // without starting the user's camera, changing profiles or opening a projector.
    private async Task<object> VerifyFootballAsync()
    {
        string directory = Path.Combine(_appDataDirectory, "FootballVerification", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(directory);
        var images = new List<string>();
        var device = CanvasDevice.GetSharedDevice();
        var now = MonotonicClock.UtcNow;
        int renderedFrames = 0;
        using var scene = MakeScene(16.0 / 9, out double inset);
        var audioCues = new List<FootballAudioUpdate>();
        scene.FootballAudioRequested += audioCues.Add;
        // The pitch is built off the render path; the board shows Loading until then.
        await scene.EnsureBoardArtworkResourcesAsync(device, BoardScreen.Football);
        using var target = new CanvasRenderTarget(device, 1600, 900, 96);
        Draw(scene, target);
        Check(scene.FootballState.Phase == FootballPhase.WaitingForPlayers, "Football started without a player.");
        Check(scene.FootballState.Kickers.All(k => k.Style == FootballKickerStyle.Car),
            "Both teams did not start with car-front artwork.");
        VerifyPitchLayout(scene);
        VerifyVisibleControls(scene);
        VerifyGrassStatus(scene);
        var idleControls = Session(scene).Buttons.ToArray();
        await Save(target, "football-waiting-overlay-controls");
        var field = scene.FootballFieldBounds();
        var playerCamera = Camera(field.X + field.Width * .22, field.Y + field.Height * .5, inset);
        Check(scene.TryMapFootballCameraPoint(playerCamera, out var uv) && Math.Abs(uv.X - .22) < .0001 &&
            Math.Abs(uv.Y - .5) < .0001, "Field letterboxing and camera inverse disagreed.");
        Check(!scene.TryMapFootballCameraPoint(Camera(.5, .04, inset), out _), "Scoreboard accepted a playing point.");
        // Marker detection searches only around this outline, so it must hold the whole
        // pitch, yet leave out board area well beyond the grass.
        var turf = scene.GetFootballCameraTurfPolygon();
        bool InTurfBounds(PixelPoint point) => turf is { Length: 4 } &&
            point.X >= turf.Min(corner => corner.X) && point.X <= turf.Max(corner => corner.X) &&
            point.Y >= turf.Min(corner => corner.Y) && point.Y <= turf.Max(corner => corner.Y);
        Check(new[] { (field.X, field.Y), (field.X + field.Width, field.Y), (field.X, field.Y + field.Height),
                (field.X + field.Width, field.Y + field.Height) }.All(corner => InTurfBounds(Camera(corner.Item1, corner.Item2, inset))) &&
            !InTurfBounds(Camera(.5, .04, inset)) && !InTurfBounds(Camera(.5, .995, inset)),
            "The camera search outline did not match the calibrated grass.");
        for (int frame = 0; frame < 155; frame++)
        {
            now += TimeSpan.FromMilliseconds(16);
            scene.SetFootballPlayerCameraPoint(0, playerCamera, now);
            Draw(scene, target);
            if (frame == 20)
            {
                Check(scene.FootballState.Phase == FootballPhase.Countdown, "The countdown fixture skipped its countdown.");
                VerifyHiddenControls(scene);
                await Save(target, "football-countdown-clear-pitch");
            }
        }
        Check(scene.FootballState.Phase == FootballPhase.Playing && scene.FootballState.Kickers.All(k => k.Present),
            "Fresh calibrated input did not start a human-versus-AI match.");
        Check(audioCues.Count(cue => (cue.Cues & FootballAudioCue.StartWhistle) != 0) == 1,
            "Football did not emit exactly one kickoff whistle from its live scene tick.");
        await Save(target, "football-playing");
        // The tip detectors' projected-scene reference matches the camera's own view of
        // the board (1000 synthetic camera pixels less the safety inset), not the projector raster.
        var projectedReference = scene.GetEyeTipProjectionFrames();
        double cameraSpan = 1000 * (1 - inset);
        Check(projectedReference.Count == 1 && Math.Abs(projectedReference[0].Width - cameraSpan) <= 2 &&
            Math.Abs(projectedReference[0].Height - cameraSpan) <= 2,
            "The projected-scene reference did not match the camera's view of the board.");
        Check(scene.GetHandAcquisitionContext(now) is { ContinuousSearchPolygon: null, ObserveMotion: false,
            ExpectedScene: null, RestrictAcquisitionToSearchRegions: true, StationarySearchCenters.Length: 0 },
            "Black-bar play searched for hands or analyzed hidden button captions.");
        scene.SetFootballFingerInput(0, true);
        var context = scene.GetHandAcquisitionContext(now);
        Check(context is { ContinuousSearchPolygon.Length: 4, ObserveMotion: false, ExpectedScene: null },
            "The pitch lost finger acquisition or kept analyzing hidden button captions.");
        VerifyHiddenControls(scene);
        var beforeDrop = scene.FootballState.BallPosition;
        var aiBeforeDrop = scene.FootballState.Kickers[1].Position;
        now += TimeSpan.FromMilliseconds(300);
        Draw(scene, target);
        Check(scene.FootballState.Phase == FootballPhase.Playing && scene.FootballState.RecoveringInput &&
            !scene.FootballState.Kickers[0].Present && scene.FootballState.BallPosition == beforeDrop &&
            scene.FootballState.Kickers[1].Position == aiBeforeDrop,
            "A short tracking interruption did not quietly freeze the ball and computer.");
        VerifyHiddenControls(scene);
        var paused = scene.FootballState.BallPosition;
        now += TimeSpan.FromMilliseconds(40);
        scene.SetFootballPlayerCameraPoint(0, playerCamera, now.AddSeconds(-1));
        scene.SetFootballPlayerCameraPoint(0, playerCamera, now.AddSeconds(1));
        Draw(scene, target);
        Check(scene.FootballState.RecoveringInput && scene.FootballState.BallPosition == paused,
            "Stale/future input restarted a paused game.");
        now += TimeSpan.FromMilliseconds(16);
        scene.SetFootballPlayerCameraPoint(0, playerCamera, now);
        Draw(scene, target);
        Check(scene.FootballState.Phase == FootballPhase.Playing && !scene.FootballState.RecoveringInput &&
            scene.FootballState.BallPosition == paused && scene.FootballState.Kickers[1].Position == aiBeforeDrop,
            "Fresh input after a short loss restarted the countdown or caught up old physics.");
        now += TimeSpan.FromMilliseconds(800);
        Draw(scene, target);
        Check(scene.FootballState.Phase == FootballPhase.WaitingForPlayers && !scene.FootballState.RecoveringInput,
            "A sustained missing marker failed to pause the match.");
        VerifyVisibleControls(scene);
        await Save(target, "football-paused-overlay-controls");
        now += TimeSpan.FromMilliseconds(16);
        scene.SetFootballPlayerCameraPoint(0, playerCamera, now);
        Draw(scene, target);
        Check(scene.FootballState.Phase == FootballPhase.Countdown,
            "Returning after a sustained loss bypassed the reconnect countdown.");
        VerifyHiddenControls(scene);
        scene.ResetFootball();
        Draw(scene, target);
        Check(scene.ActivateFootballButton("football-mode") && scene.FootballState.Mode == FootballMode.TwoHumans,
            "The inactive projected two-player control failed.");
        for (int frame = 0; frame < 120; frame++)
        {
            now += TimeSpan.FromMilliseconds(16);
            scene.SetFootballPlayerCameraPoint(0, playerCamera, now);
            scene.SetFootballPlayerCameraPoint(1, Camera(field.X + field.Width * .78, field.Y + field.Height * .5, inset), now);
            Draw(scene, target);
        }
        Check(scene.FootballState.Phase == FootballPhase.Playing && scene.FootballState.Kickers.All(k => !k.IsAi && k.Present),
            "Two fresh markers did not start two-player play.");
        scene.SetFootballStyle(0, FootballKickerStyle.Pan);
        scene.SetFootballStyle(1, FootballKickerStyle.Boot);
        Draw(scene, target);
        await Save(target, "football-pan-and-boot");
        VerifyHiddenControls(scene);
        var goalGame = (FootballGame)typeof(BoardSession).GetField("_football", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(Session(scene))!;
        typeof(FootballGame).GetField("_phase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(goalGame, FootballPhase.Goal);
        typeof(FootballGame).GetField("_phaseRemaining", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(goalGame, 1.25f);
        typeof(FootballGame).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(goalGame, null);
        now += TimeSpan.FromMilliseconds(16);
        Draw(scene, target);
        Check(scene.FootballState.Phase == FootballPhase.Goal, "The goal fixture skipped its celebration.");
        VerifyHiddenControls(scene);
        await Save(target, "football-goal-clear-pitch");
        scene.ResetFootball();
        Check(scene.FootballState.Score1 == 0 &&
            scene.FootballState.Score2 == 0 && scene.FootballState.Phase == FootballPhase.WaitingForPlayers,
            "The laptop reset did not require fresh input for a new match.");
        now += TimeSpan.FromMilliseconds(16);
        scene.SetBoardFacingDegrees(90);
        Draw(scene, target);
        field = scene.FootballFieldBounds();
        var rotated = Camera(field.X + field.Width * .2, field.Y + field.Height * .65, inset, true);
        Check(scene.TryMapFootballCameraPoint(rotated, out uv) && Math.Abs(uv.X - .2) < .0001 && Math.Abs(uv.Y - .65) < .0001,
            "Quarter-turn board orientation changed football aiming.");
        scene.SetBlackOutput(true);
        now += TimeSpan.FromMilliseconds(16);
        scene.SetFootballPlayerCameraPoint(0, rotated, now);
        Check(!scene.TryMapFootballCameraPoint(rotated, out _), "Black output still accepted football camera input.");
        Check(!scene.TryMapFootballMarkerCameraPoint(rotated, out _), "Black output still accepted a football marker.");

        // The car sits ahead of the bar, including on a rotated board, and its
        // orientation follows stationary bar rotation rather than movement.
        using (var anchored = MakeScene(16.0 / 9, out double anchorInset))
        {
            await anchored.EnsureBoardArtworkResourcesAsync(device, BoardScreen.Football);
            anchored.SetFootballMode(FootballMode.TwoHumans);
            foreach (bool turnBoard in new[] { false, true })
            {
                anchored.SetBoardFacingDegrees(turnBoard ? 90 : 0);
                Draw(anchored, target);
                var pitch = anchored.FootballFieldBounds();
                foreach (float angle in new[] { 0f, .6f, -.4f })
                {
                    var center = new Vector2(-.42f, .04f);
                    var forward = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                    var along = new Vector2(-forward.Y, forward.X);
                    var geometry = new BlackBarGeometry(ToCamera(center - along * .055f), ToCamera(center + along * .055f),
                        ToCamera(center - forward * .008f), ToCamera(center + forward * .008f));
                    now += TimeSpan.FromMilliseconds(33);
                    Check(anchored.SetFootballPlayerBar(0, new(ToCamera(center), 12, 300, 1) { Bar = geometry }, now),
                        "A calibrated physical bar could not drive the car front.");
                    Draw(anchored, target);
                    var car = anchored.FootballState.Kickers[0];
                    Check(car.MarkerAnchored && Math.Abs(car.Heading - angle) < .001 &&
                        Vector2.Distance(car.Position, center + forward * (.008f + FootballGame.KickerRadius + FootballBarPose.Clearance)) < .001,
                        "The car front did not stay in front of the rotated bar.");
                    PixelPoint ToCamera(Vector2 point) => Camera(pitch.X + (point.X / FootballGame.Width + .5) * pitch.Width,
                        pitch.Y + (point.Y / FootballGame.Height + .5) * pitch.Height, anchorInset, turnBoard);
                }
                // A brief interruption must not choose the opposite side of a nearly
                // horizontal bar when one pixel changes the sign of its normal.
                float previousHeading = 0;
                foreach (float angle in new[] { -MathF.PI / 2 + .01f, -MathF.PI / 2 - .01f })
                {
                    var center = new Vector2(-.42f, .04f);
                    var forward = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                    var along = new Vector2(-forward.Y, forward.X);
                    PixelPoint PoseCamera(Vector2 point) => Camera(pitch.X + (point.X / FootballGame.Width + .5) * pitch.Width,
                        pitch.Y + (point.Y / FootballGame.Height + .5) * pitch.Height, anchorInset, turnBoard);
                    now += TimeSpan.FromMilliseconds(300);
                    var observation = new ColorTipObservation(PoseCamera(center), 12, 300, 1)
                    {
                        Bar = new(PoseCamera(center - along * .055f), PoseCamera(center + along * .055f),
                            PoseCamera(center - forward * .008f), PoseCamera(center + forward * .008f))
                    };
                    Check(anchored.SetFootballPlayerBar(0, observation, now), "A fresh bar failed after a short interruption.");
                    Draw(anchored, target);
                    float actual = anchored.FootballState.Kickers[0].Heading;
                    if (previousHeading != 0)
                        Check(Math.Abs(MathF.IEEERemainder(actual - previousHeading, MathF.Tau)) < .05,
                            "A short tracking interruption flipped a side-on car.");
                    previousHeading = actual;
                }
            }
            await Save(target, "football-bar-anchored");
            VerifyGrassMarkerBounds(anchored, anchorInset, target);
            await Save(target, "football-markers-behind-goals");
        }

        await VerifyFinishedInputAsync();

        using var renderer = new FootballRenderer(device);
        await renderer.EnsurePitchAsync();
        int rawHeight = (int)FootballFieldGeometry.Height + 132;
        int rawMidfieldY = (int)FootballFieldGeometry.Height / 2 + 66;
        using var raw = new CanvasRenderTarget(device, 1824, rawHeight, 96);
        var snapshot = new FootballGame().Snapshot with
        {
            Phase = FootballPhase.Playing,
            Kickers = new[]
            {
                new FootballKickerSnapshot(0, new(-.48f, .17f), Vector2.Zero, true, FootballKickerStyle.Car, false),
                new FootballKickerSnapshot(1, new(.5f, -.17f), Vector2.Zero, true, FootballKickerStyle.Glove, true)
            }
        };
        RenderRaw(snapshot with { BallPosition = new(-.5f, -.35f) });
        byte[] background = raw.GetPixelBytes();
        RenderRaw(snapshot with { BallHeight = .20f, BallRotation = Quaternion.CreateFromYawPitchRoll(.45f, .7f, .3f) });
        byte[] airborne = raw.GetPixelBytes();
        int shadowPixels = 0;
        for (int y = rawMidfieldY + 84; y < rawMidfieldY + 169; y++)
        for (int x = 960; x < 1070; x++)
        {
            int offset = (y * 1824 + x) * 4;
            if (background[offset + 1] - airborne[offset + 1] > 6) shadowPixels++;
        }
        Check(shadowPixels > 350, "Airborne ball lacked a visible, separated directional shadow on the turf.");
        await Save(raw, "football-airborne-shadow");
        RenderRaw(snapshot);
        await Save(raw, "football-car-front-and-glove");
        RenderRaw(snapshot with
        {
            Kickers = new[]
            {
                snapshot.Kickers[0] with { MarkerAnchored = true },
                snapshot.Kickers[1] with { Style = FootballKickerStyle.Car, Heading = MathF.PI }
            }
        });
        byte[] teamCars = raw.GetPixelBytes();
        int redCarPixels = CountTeamStripePixels(teamCars, 432, rawMidfieldY + 170, blue: false);
        int blueCarPixels = CountTeamStripePixels(teamCars, 1412, rawMidfieldY - 170, blue: true);
        Check(redCarPixels > 100 && blueCarPixels > 100,
            "The two car fronts did not render their distinct red and blue stripes.");
        await Save(raw, "football-red-and-blue-car-fronts");
        // A finished match is otherwise static, so drive the victory clock
        // explicitly and compare the native pixels with its undecorated scene.
        var victory = snapshot with
        {
            Phase = FootballPhase.Finished,
            Score1 = FootballGame.WinningScore,
            Score2 = 3,
            Kickers = new[]
            {
                snapshot.Kickers[0] with { Position = new(-.4f, 0), MarkerAnchored = true },
                snapshot.Kickers[1] with { Position = new(.4f, 0), Style = FootballKickerStyle.Car, Heading = MathF.PI }
            }
        };
        VerifyVictoryClock();
        int confettiPixels = 0, confettiOnCarPixels = 0;
        foreach (int winner in new[] { 0, 1 })
        {
            var finished = victory with
            {
                Score1 = winner == 0 ? FootballGame.WinningScore : 3,
                Score2 = winner == 1 ? FootballGame.WinningScore : 3
            };
            RenderRaw(finished);
            byte[] undecorated = raw.GetPixelBytes();
            byte[]? settled = null;
            foreach (double seconds in winner == 0 ? new[] { .35, 1.7, 4, 10 } : new[] { 1.7, 10 })
            {
                RenderRaw(finished, seconds);
                byte[] celebrated = raw.GetPixelBytes();
                int winnerLeft = winner == 0 ? 0 : 912;
                int loserLeft = winner == 0 ? 912 : 0;
                Check(ChangedPixels(undecorated, celebrated, loserLeft, 0, 912, rawHeight) == 0,
                    "Victory confetti crossed onto the losing player's half.");
                int changes = ChangedPixels(undecorated, celebrated, winnerLeft, 0, 912, rawHeight);
                Check(changes > 1500, "The winning half did not receive a substantial confetti shower.");
                confettiPixels = Math.Max(confettiPixels, changes);
                await Save(raw, $"football-player-{winner + 1}-victory-{seconds:0.00}s");
                if (seconds == FootballRenderer.VictoryDurationSeconds)
                {
                    settled = celebrated;
                    // This lies inside the opaque hood, away from its alpha edge.
                    // A large RGB change here verifies pieces render over the car.
                    int onCar = ChangedPixels(undecorated, celebrated,
                        winner == 0 ? 496 : 1296, rawMidfieldY - 20, 32, 40, minimumChange: 24);
                    Check(onCar > 20, "The winning car did not receive settled confetti on its hood.");
                    confettiOnCarPixels += onCar;
                }
            }
            RenderRaw(finished, FootballRenderer.VictoryDurationSeconds + 10);
            Check(settled is not null && settled.AsSpan().SequenceEqual(raw.GetPixelBytes()),
                "Settled victory confetti continued changing after the animation ended.");
        }
        foreach (var phase in new[] { FootballPhase.Playing, FootballPhase.Goal })
        {
            var ordinary = victory with { Phase = phase, Score1 = 3, Score2 = 2 };
            RenderRaw(ordinary);
            byte[] before = raw.GetPixelBytes();
            RenderRaw(ordinary, 1.7);
            Check(before.AsSpan().SequenceEqual(raw.GetPixelBytes()),
                "An ordinary goal or active match incorrectly fired victory cannons.");
        }
        using (var thumbnail = new CanvasRenderTarget(device, 1200, 600, 96))
        {
            using (var drawing = thumbnail.CreateDrawingSession()) renderer.DrawThumbnail(drawing, new Rect(0, 0, 1200, 600));
            await Save(thumbnail, "football-thumbnail");
        }
        foreach (double aspect in new[] { 1.28, 1.0, 9.0 / 16 })
        {
            using var fitted = MakeScene(aspect, out double fittedInset);
            await fitted.EnsureBoardArtworkResourcesAsync(device, BoardScreen.Football);
            using var output = new CanvasRenderTarget(device, (float)(900 * aspect), 900, 96);
            Draw(fitted, output);
            var fittedField = fitted.FootballFieldBounds();
            Check(Math.Abs(fittedField.Width * aspect / fittedField.Height - FootballGame.Width / FootballGame.Height) < .0001,
                "A changed board aspect stretched the pitch or ball.");
            VerifyPitchLayout(fitted);
            VerifyVisibleControls(fitted);
            await Save(output, aspect == 1.28 ? "football-wider-sidelines" : aspect == 1 ? "football-square" : "football-portrait");
            VerifyGrassStatus(fitted);
            VerifyGrassMarkerBounds(fitted, fittedInset, output);
        }
        return new { passed = true, renderedFrames, shadowPixels, redCarPixels, blueCarPixels,
            confettiPixels, confettiOnCarPixels, victoryClock = true, winnerHalfOnly = true,
            victorySettles = true, ordinaryGoalsWithoutConfetti = true, finishedInputSuspended = true,
            controlsWaitForConfetti = true, victoryControlsSlide = true, movingControlsDisarmed = true,
            teamCarArtwork = true, calibratedInput = true,
            staleAndFutureRejected = true, shortLossRecoversQuietly = true, lossPausesPlay = true, twoPlayers = true,
            quarterTurnMapping = true, barPositionAndRotationAnchored = true, markersUseSurroundingGrass = true,
            captionControls = true, activeControlsHidden = true, inactiveControlsOverlayPitch = true,
            expandedPitch = true, statusOnGrass = true, continuousFingerAcquisition = true, directory, images };

        async Task VerifyFinishedInputAsync()
        {
            foreach (int winner in new[] { 0, 1 })
            {
                using var current = MakeScene(16.0 / 9, out double padding);
                await current.EnsureBoardArtworkResourcesAsync(device, BoardScreen.Football);
                current.SetFootballMode(FootballMode.TwoHumans);
                Draw(current, target);
                var pitch = current.FootballFieldBounds();
                PixelPoint ToCamera(Vector2 point) => Camera(pitch.X + (point.X / FootballGame.Width + .5) * pitch.Width,
                    pitch.Y + (point.Y / FootballGame.Height + .5) * pitch.Height, padding);
                var center = new Vector2(winner == 0 ? -.4f : .4f, .04f);
                var forward = new Vector2(winner == 0 ? 1 : -1, 0);
                var along = new Vector2(-forward.Y, forward.X);
                var observation = new ColorTipObservation(ToCamera(center), 12, 300, 1)
                {
                    Bar = new(ToCamera(center - along * .055f), ToCamera(center + along * .055f),
                        ToCamera(center - forward * .008f), ToCamera(center + forward * .008f))
                };
                now += TimeSpan.FromMilliseconds(33);
                Check(current.FootballInputActive && current.SetFootballPlayerBar(winner, observation, now),
                    "The victory input fixture could not acquire its calibrated marker.");
                Draw(current, target);
                Check(current.FootballState.Kickers[winner].MarkerAnchored,
                    "The victory input fixture did not preserve its marker anchor.");
                current.SetFootballFingerInput(winner, true);
                Check(current.GetHandAcquisitionContext(now)?.ContinuousSearchPolygon?.Length == 4,
                    "The victory input fixture did not enable fingertip acquisition before the win.");
                long playingEpoch = current.FootballInputRevision;

                // Reach the real game's finished state without spending five full matches
                // in this rendering check; all lifecycle calls below use the private scene.
                const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                var session = (BoardSession)typeof(SceneCompositor).GetField("_boardSession", fields)!.GetValue(current)!;
                var game = (FootballGame)typeof(BoardSession).GetField("_football", fields)!.GetValue(session)!;
                var restingControls = session.Buttons.ToArray();
                Finish();
                var final = current.FootballState;
                Check(!current.FootballInputActive && current.FootballInputRevision == playingEpoch + 1 &&
                    !current.FootballInputActive && current.FootballInputRevision == playingEpoch + 1 &&
                    current.GetFootballCameraTurfPolygon() is null,
                    "A finished match did not stop marker acquisition and invalidate its input epoch exactly once.");
                now += TimeSpan.FromMilliseconds(16);
                Check(!current.SetFootballPlayerBar(winner, observation, now, out bool ignored) && ignored,
                    "An in-flight bar observation was accepted after the win.");
                current.SetFootballPlayerFieldPoint(winner, new(.5, .75), now);
                Draw(current, target);
                Check(ReferenceEquals(final, current.FootballState) && current.FootballVictorySeconds == 0,
                    "Stopping recognition changed the winning pose or prevented the celebration from starting.");
                var celebrationStarted = now;
                VerifyHiddenControls(current);
                foreach (double seconds in new[] { .5, 9.9, BoardSession.FootballVictoryDurationSeconds })
                {
                    AdvanceFinished(seconds);
                    VerifyHiddenControls(current);
                    Check(current.GetHandAcquisitionContext(now) is { ContinuousSearchPolygon: null,
                        ExpectedScene: null, ObserveMotion: false, StationarySearchCenters.Length: 0 },
                        "The confetti celebration resumed pitch or control acquisition before its controls appeared.");
                }
                await Save(target, winner == 0 ? "football-red-wins-controls-hidden" : "football-blue-wins-controls-hidden");

                AdvanceFinished(BoardSession.FootballVictoryDurationSeconds + BoardSession.FootballVictoryControlsSlideSeconds / 2);
                var movingControls = session.Buttons;
                Check(session.FootballControlsAnimating && movingControls.Count == 3 &&
                    movingControls.Select((button, index) => !button.Enabled &&
                        button.Bounds.Y > restingControls[index].Bounds.Y + .01 && button.Bounds.Y < 1.02 &&
                        !current.ActivateFootballButton(button.Id) &&
                        !current.ActivateFootballAt(button.Bounds.X + button.Bounds.Width / 2, button.Bounds.Y + .01)).All(valid => valid) &&
                    current.GetHoldButtonContext(now) is null &&
                    current.GetHandAcquisitionContext(now) is { ContinuousSearchPolygon: null,
                        ExpectedScene: null, ObserveMotion: false, IlluminatedHint: null },
                    "The sliding victory controls jumped to their final position or accepted input while moving.");
                await Save(target, winner == 0 ? "football-red-wins-controls-entering" : "football-blue-wins-controls-entering");

                AdvanceFinished(BoardSession.FootballVictoryDurationSeconds + BoardSession.FootballVictoryControlsSlideSeconds);
                Check(!session.FootballControlsAnimating && session.Buttons.All(button => button.Enabled),
                    "The victory controls did not finish their entrance or become available afterward.");
                VerifyVisibleControls(current);
                VerifyGrassStatus(current);
                await Save(target, winner == 0 ? "football-red-wins-overlay-controls" : "football-blue-wins-overlay-controls");

                var oldFrame = now;
                Check(current.ActivateFootballButton("football-reset") && current.FootballInputActive &&
                    current.FootballState.Phase == FootballPhase.WaitingForPlayers && current.FootballVictorySeconds < 0,
                    "The reset caption did not restore recognition for a fresh match.");
                now += TimeSpan.FromMilliseconds(16);
                Check(!current.SetFootballPlayerBar(winner, observation, oldFrame, out bool ignoredOld) && ignoredOld &&
                    current.SetFootballPlayerBar(winner, observation, now),
                    "Restarted recognition accepted old input or rejected a fresh marker.");
                Draw(current, target);
                Check(current.FootballState.Kickers[winner].MarkerAnchored,
                    "Restarted recognition did not anchor the car to its new marker observation.");
                Finish();
                Draw(current, target);
                Check(!current.FootballInputActive && current.FootballVictorySeconds == 0 && session.Buttons.Count == 0,
                    "The next win skipped its celebration or retained the previous controls.");
                now += TimeSpan.FromSeconds(BoardSession.FootballVictoryDurationSeconds + BoardSession.FootballVictoryControlsSlideSeconds);
                Draw(current, target);
                Check(current.ActivateFootballButton("football-exit") &&
                    current.CurrentBoardScreen == BoardScreen.Menu && !current.FootballInputActive,
                    "The exit caption stopped working after the match was won.");

                void AdvanceFinished(double seconds)
                {
                    now = celebrationStarted.AddSeconds(seconds);
                    Check(!current.SetFootballPlayerBar(winner, observation, now, out bool ignoredLate) && ignoredLate,
                        "A marker resumed recognition during the victory presentation.");
                    current.SetFootballPlayerFieldPoint(winner, new(.5, .75), now);
                    Draw(current, target);
                    Check(ReferenceEquals(final, current.FootballState) &&
                        Math.Abs(current.FootballVictorySeconds - seconds) < 1.0 / 60 + .000001 &&
                        !current.FootballInputActive && current.FootballInputRevision == playingEpoch + 1 &&
                        current.GetFootballCameraTurfPolygon() is null,
                        "A finished match aged its final pose, repeated its input reset, or stopped the presentation clock early.");
                }

                void Finish()
                {
                    typeof(FootballGame).GetField("_phase", fields)!.SetValue(game, FootballPhase.Finished);
                    typeof(FootballGame).GetField("_score1", fields)!.SetValue(game, winner == 0 ? FootballGame.WinningScore : 3);
                    typeof(FootballGame).GetField("_score2", fields)!.SetValue(game, winner == 1 ? FootballGame.WinningScore : 3);
                    typeof(FootballGame).GetField("_snapshot", fields)!.SetValue(game, null);
                }
            }
        }

        void VerifyVictoryClock()
        {
            using var clockScene = MakeScene(16.0 / 9, out _);
            Check(clockScene.FootballVictorySeconds < 0, "A new Football board already had a victory clock.");
            clockScene.UpdateFootballVictory(victory, now);
            Check(clockScene.FootballVictorySeconds == 0, "Entering Finished did not start the victory clock.");
            clockScene.UpdateFootballVictory(victory, now.AddMilliseconds(8));
            Check(clockScene.FootballVictorySeconds == 0, "The victory clock changed within its 60 Hz frame.");
            clockScene.UpdateFootballVictory(victory with { Revision = victory.Revision + 1 }, now.AddMilliseconds(125));
            Check(Math.Abs(clockScene.FootballVictorySeconds - 7.0 / 60) < .000001,
                "Repeated finished snapshots restarted the celebration or skipped frame quantization.");
            clockScene.UpdateFootballVictory(victory, now.AddSeconds(20));
            double presentationDuration = BoardSession.FootballVictoryDurationSeconds + BoardSession.FootballVictoryControlsSlideSeconds;
            Check(clockScene.FootballVictorySeconds == presentationDuration,
                "The victory clock did not stop after both confetti and control entrance finished.");
            clockScene.UpdateFootballVictory(victory, now.AddSeconds(5));
            Check(clockScene.FootballVictorySeconds == presentationDuration,
                "A repeated earlier draw rewound the victory clock.");
            clockScene.UpdateFootballVictory(snapshot, now.AddSeconds(21));
            Check(clockScene.FootballVictorySeconds < 0, "A new match retained the old victory clock.");
            clockScene.UpdateFootballVictory(victory with { Score1 = 3, Score2 = FootballGame.WinningScore }, now.AddSeconds(22));
            clockScene.UpdateFootballVictory(victory with { Score1 = 3, Score2 = FootballGame.WinningScore }, now.AddSeconds(22.5));
            Check(clockScene.FootballVictorySeconds == .5, "The next winner did not start a fresh celebration.");
        }

        static int ChangedPixels(byte[] before, byte[] after, int left, int top, int width, int height, int minimumChange = 1)
        {
            int changed = 0;
            for (int y = top; y < top + height; y++)
            for (int x = left; x < left + width; x++)
            {
                int offset = (y * 1824 + x) * 4;
                if (Math.Abs(before[offset] - after[offset]) >= minimumChange ||
                    Math.Abs(before[offset + 1] - after[offset + 1]) >= minimumChange ||
                    Math.Abs(before[offset + 2] - after[offset + 2]) >= minimumChange ||
                    Math.Abs(before[offset + 3] - after[offset + 3]) >= minimumChange) changed++;
            }
            return changed;
        }

        static int CountTeamStripePixels(byte[] pixels, int centerX, int centerY, bool blue)
        {
            int count = 0;
            for (int y = centerY - 80; y <= centerY + 80; y++)
            for (int x = centerX - 80; x <= centerX + 80; x++)
            {
                int offset = (y * 1824 + x) * 4;
                int b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
                if (blue ? b > r + 30 && b > g + 10 : r > b + 30 && r > g + 25) count++;
            }
            return count;
        }

        static BoardSession Session(SceneCompositor current) => (BoardSession)typeof(SceneCompositor)
            .GetField("_boardSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(current)!;

        static Rect TurfBounds(Rect pitch) => new(
            pitch.X + pitch.Width * FootballFieldGeometry.TurfLeft / FootballFieldGeometry.Width,
            pitch.Y + pitch.Height * FootballFieldGeometry.TurfTop / FootballFieldGeometry.Height,
            pitch.Width * FootballFieldGeometry.TurfWidth / FootballFieldGeometry.Width,
            pitch.Height * FootballFieldGeometry.TurfHeight / FootballFieldGeometry.Height);

        void VerifyPitchLayout(SceneCompositor current)
        {
            var pitch = current.FootballFieldBounds();
            var grass = TurfBounds(pitch);
            double aspect = current.FootballPreviewAspect;
            // Compare with the previous 1600-by-1000 pitch and its fitted turf:
            // wider means more space between the two sidelines, not wider goals.
            double oldWidth = Math.Min(.976 * 1600 / 1820, .895 * 1600 / (1128 * aspect));
            double oldHeight = oldWidth * aspect / 1.6;
            Check(pitch.Height > oldHeight + .005 && grass.X >= .012 - .000001 && grass.Right <= .988 + .000001 &&
                grass.Y >= .075 - .000001 && grass.Bottom <= .975 + .000001,
                "The wider sidelines failed to use the released space or clipped the turf outside the board.");
            if (Math.Abs(aspect - 1.28) < .0001)
                Check(pitch.Height > oldHeight * 1.17 && pitch.Y < .5275 - oldHeight / 2 &&
                    pitch.Bottom > .5275 + oldHeight / 2,
                    "The physical board's two sidelines did not both expand away from midfield.");
        }

        void VerifyVisibleControls(SceneCompositor current)
        {
            var pitch = current.FootballFieldBounds();
            var grass = TurfBounds(pitch);
            var buttons = Session(current).Buttons;
            Check(current.GetHoldButtonContext(now) is { } controls &&
                controls.EnabledHoldIds.SetEquals(["football-exit", "football-reset", "football-mode"]) &&
                buttons.Count == 3 && buttons.All(button => button.Bounds.Y < pitch.Bottom &&
                    button.Bounds.Y + button.Bounds.Height > grass.Bottom &&
                    button.Bounds.Y + button.Bounds.Height <= 1),
                "Inactive controls lost their caption references or did not partially overlap the field's lower edge.");
        }

        void VerifyHiddenControls(SceneCompositor current)
        {
            Check(Session(current).Buttons.Count == 0 && current.GetHoldButtonContext(now) is null &&
                idleControls.All(button => !current.ActivateFootballButton(button.Id) &&
                    !current.ActivateFootballAt(button.Bounds.X + button.Bounds.Width / 2,
                        button.Bounds.Y + button.Bounds.Height / 2)),
                "Active play retained a visible, caption-driven, or pointer-activated projected control.");
        }

        void VerifyGrassStatus(SceneCompositor current)
        {
            using var preview = new CanvasRenderTarget(device, (float)(900 * current.FootballPreviewAspect), 900, 96);
            using (var drawing = preview.CreateDrawingSession())
                current.DrawFootballPreview(drawing, (float)preview.Size.Width, (float)preview.Size.Height);
            renderedFrames++;
            var pitch = current.FootballFieldBounds();
            var grass = TurfBounds(pitch);
            byte[] pixels = preview.GetPixelBytes();
            int width = (int)preview.SizeInPixels.Width, height = (int)preview.SizeInPixels.Height, lettering = 0;
            // The scoreboard and touchline lie outside this band. Cream lettering
            // here proves that the actual rendered message moved onto the grass.
            for (int y = (int)Math.Ceiling(grass.Y * height) + 2; y < (int)(pitch.Y * height) - 2; y++)
            for (int x = (int)((pitch.X + pitch.Width * .1) * width); x < (pitch.Right - pitch.Width * .1) * width; x++)
            {
                int offset = (y * width + x) * 4;
                if (pixels[offset] > 205 && pixels[offset + 1] > 225 && pixels[offset + 2] > 225) lettering++;
            }
            Check(lettering >= 12, $"The rendered status message did not appear on the upper grass margin " +
                $"(aspect {current.FootballPreviewAspect:0.000}, {lettering} lettering pixels).");
        }

        void VerifyGrassMarkerBounds(SceneCompositor current, double padding, CanvasRenderTarget output)
        {
            current.SetFootballMode(FootballMode.TwoHumans);
            foreach (bool turnBoard in new[] { false, true })
            {
                current.SetBoardFacingDegrees(turnBoard ? 90 : 0);
                Draw(current, output);
                var pitch = current.FootballFieldBounds();
                PixelPoint FieldCamera(double x, double y) => Camera(pitch.X + x * pitch.Width,
                    pitch.Y + y * pitch.Height, padding, turnBoard);
                PixelPoint WorldCamera(Vector2 point) => FieldCamera(point.X / FootballGame.Width + .5,
                    point.Y / FootballGame.Height + .5);

                Check(!current.TryMapFootballMarkerCameraPoint(Camera(.5, .04, padding, turnBoard), out _) &&
                    !current.TryMapFootballMarkerCameraPoint(Camera(.5, .995, padding, turnBoard), out _),
                    "The scoreboard or area below the turf accepted a football marker.");
                foreach (var point in new PixelPoint[] { new(-.069, .5), new(1.069, .5),
                    new(.5, -65 / FootballFieldGeometry.Height), new(.5, 1 + 65 / FootballFieldGeometry.Height) })
                    Check(!current.TryMapFootballMarkerCameraPoint(FieldCamera(point.X, point.Y), out _),
                        "A point beyond the rendered grass accepted a football marker.");
                foreach (bool right in new[] { false, true })
                foreach (bool bottom in new[] { false, true })
                {
                    // These lie inside the turf's rectangular bounds, on opposite sides
                    // of its rounded corner. Transparent corner pixels cannot accept input.
                    double insideX = (right ? FootballFieldGeometry.Width + 103 : -103) / FootballFieldGeometry.Width;
                    double insideY = (bottom ? FootballFieldGeometry.Height + 57 : -57) / FootballFieldGeometry.Height;
                    double outsideX = (right ? FootballFieldGeometry.Width + 108 : -108) / FootballFieldGeometry.Width;
                    double outsideY = (bottom ? FootballFieldGeometry.Height + 62 : -62) / FootballFieldGeometry.Height;
                    Check(current.TryMapFootballMarkerCameraPoint(FieldCamera(insideX, insideY), out _) &&
                        !current.TryMapFootballMarkerCameraPoint(FieldCamera(outsideX, outsideY), out _),
                        "Marker acceptance did not follow the rendered rounded grass corner.");
                }

                // Both new strips beyond the old sidelines must participate in
                // calibrated play, including when the board is turned sideways.
                foreach (float y in new[] { -.51f, .51f })
                {
                    var center = new Vector2(-.4f, y);
                    var cameraPoint = WorldCamera(center);
                    Check(current.TryMapFootballCameraPoint(cameraPoint, out var widened) &&
                        Math.Abs(widened.Y - (y / FootballGame.Height + .5)) < .0001,
                        "The newly widened sideline strip did not map to the playing field.");
                    var observation = new ColorTipObservation(cameraPoint, 12, 300, 1)
                    {
                        Bar = new(WorldCamera(center - new Vector2(0, .055f)), WorldCamera(center + new Vector2(0, .055f)),
                            WorldCamera(center - new Vector2(.008f, 0)), WorldCamera(center + new Vector2(.008f, 0)))
                    };
                    now += TimeSpan.FromMilliseconds(33);
                    Check(current.SetFootballPlayerBar(0, observation, now),
                        "The widened sideline strip rejected a fresh bar observation.");
                    Draw(current, output);
                    Check(Math.Abs(current.FootballState.Kickers[0].Position.Y - y) < .001,
                        "The car remained clamped to the old sideline or used the wrong vertical scale.");
                }

                foreach (double distance in new[] { .015, .065 })
                for (int player = 0; player < 2; player++)
                {
                    double x = player == 0 ? -distance : 1 + distance;
                    var cameraPoint = FieldCamera(x, .5);
                    Check(current.TryMapFootballMarkerCameraPoint(cameraPoint, out var mapped) &&
                        Math.Abs(mapped.X - x) < .0001 && Math.Abs(mapped.Y - .5) < .0001,
                        "Grass behind a goal was rejected or clamped before calculating the marker pose.");
                    Check(!current.TryMapFootballCameraPoint(cameraPoint, out _),
                        "The marker's extra grass area incorrectly expanded fingertip play.");
                    var center = new Vector2((float)(x - .5) * FootballGame.Width, 0);
                    var forward = new Vector2(player == 0 ? 1 : -1, 0);
                    var along = new Vector2(-forward.Y, forward.X);
                    const float halfThickness = .035f;
                    var observation = new ColorTipObservation(cameraPoint, 16, 700, 1)
                    {
                        Bar = new(WorldCamera(center - along * .055f), WorldCamera(center + along * .055f),
                            WorldCamera(center - forward * halfThickness), WorldCamera(center + forward * halfThickness))
                    };
                    var detection = new ColorTipDetectionResult([observation], "black-bar-pair-candidate");
                    var tracker = new ColorTipTracker();
                    PixelPoint? MarkerField(PixelPoint point) => current.TryMapFootballMarkerCameraPoint(point, out var fieldPoint)
                        ? fieldPoint : null;
                    now += TimeSpan.FromMilliseconds(33);
                    Check(FootballTipAssignment.Update(tracker, player, detection, MarkerField, now, now).Action == FootballTipAction.Hold,
                        "A behind-goal marker bypassed fresh-frame confirmation.");
                    now += TimeSpan.FromMilliseconds(33);
                    Check(FootballTipAssignment.Update(tracker, player, detection, MarkerField, now, now).Action == FootballTipAction.Publish,
                        "A behind-goal marker was lost during player-half assignment.");
                    Check(!current.SetFootballPlayerBar(1 - player, observation, now, out bool ignoredInvalid) && !ignoredInvalid,
                        "A behind-goal marker drove the opposing player's car.");
                    Check(current.SetFootballPlayerBar(player, observation, now),
                        "A marker on the grass behind a goal could not drive its car.");
                    Draw(current, output);
                    var car = current.FootballState.Kickers[player];
                    var expected = Vector2.Clamp(center + forward *
                        (halfThickness + FootballGame.KickerRadius + FootballBarPose.Clearance),
                        new(-FootballGame.Width / 2 + FootballGame.KickerRadius, -FootballGame.Height / 2 + FootballGame.KickerRadius),
                        new(FootballGame.Width / 2 - FootballGame.KickerRadius, FootballGame.Height / 2 - FootballGame.KickerRadius));
                    Check(car.Present && car.MarkerAnchored && Vector2.Distance(car.Position, expected) < .001 &&
                        Vector2.Dot(new(MathF.Cos(car.Heading), MathF.Sin(car.Heading)), forward) > .999,
                        "The behind-goal car lost its measured offset, inward facing or pitch boundary clamp.");
                    Check(!current.SetFootballPlayerBar(player, observation, now.AddSeconds(-1), out bool ignoredOld) && ignoredOld &&
                        !current.SetFootballPlayerBar(player, observation, now.AddSeconds(1), out bool ignoredFuture) && ignoredFuture,
                        "The expanded grass area admitted stale or future marker input.");
                    var acceptedAt = now;
                    now += TimeSpan.FromMilliseconds(300);
                    Check(!current.SetFootballPlayerBar(player, observation, acceptedAt.AddMilliseconds(10), out bool ignoredLate) && ignoredLate,
                        "A newer sample that aged before publication was mistaken for an invalid marker.");
                }
            }
        }

        SceneCompositor MakeScene(double aspect, out double safetyInset)
        {
            var result = new SceneCompositor(blackjackClock: () => now, footballClock: () => now);
            result.SetDisplayAspect(aspect);
            result.SetBoardFacingDegrees(0);
            result.SetBoardSetup(true);
            Vector2[] corners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            Point2[] unit = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            Point2[] camera = [new(0, 0), new(1000, 0), new(1000, 1000), new(0, 1000)];
            safetyInset = result.SetDetectedBoardGrid(corners, Homography.FromFourPoints(camera, unit));
            result.SetBoardSetup(false);
            result.ShowFootball();
            return result;
        }
        static PixelPoint Camera(double x, double y, double padding, bool rotated = false)
        {
            if (rotated) (x, y) = (1 - y, x);
            return new(1000 * (padding / 2 + x * (1 - padding)), 1000 * (padding / 2 + y * (1 - padding)));
        }
        void Draw(SceneCompositor current, CanvasRenderTarget output)
        {
            using var drawing = output.CreateDrawingSession();
            current.Draw(drawing, (float)output.Size.Width, (float)output.Size.Height, preview: false, runningSlowly: false);
            renderedFrames++;
        }
        void RenderRaw(FootballSnapshot state, double victorySeconds = -1)
        {
            using var drawing = raw.CreateDrawingSession();
            drawing.Clear(Windows.UI.Color.FromArgb(255, 13, 30, 25));
            renderer.Draw(drawing, new Rect(112, 66, FootballFieldGeometry.Width, FootballFieldGeometry.Height), state, victorySeconds);
            renderedFrames++;
        }
        async Task Save(CanvasRenderTarget output, string name)
        {
            string path = Path.Combine(directory, name + ".png");
            await output.SaveAsync(path, CanvasBitmapFileFormat.Png);
            images.Add(path);
        }
        static void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
        }
    }
}
#endif
