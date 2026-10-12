#if DEBUG
using System.Numerics;
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
        // The pitch is built off the render path; the board shows Loading until then.
        await scene.EnsureBoardArtworkResourcesAsync(device, BoardScreen.Football);
        using var target = new CanvasRenderTarget(device, 1600, 900, 96);
        Draw(scene, target);
        Check(scene.FootballState.Phase == FootballPhase.WaitingForPlayers, "Football started without a player.");
        Check(scene.FootballState.Kickers.All(k => k.Style == FootballKickerStyle.Car),
            "Both teams did not start with car-front artwork.");
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
            !InTurfBounds(Camera(.5, .04, inset)) && !InTurfBounds(Camera(.5, .97, inset)),
            "The camera search outline did not match the calibrated grass.");
        for (int frame = 0; frame < 155; frame++)
        {
            now += TimeSpan.FromMilliseconds(16);
            scene.SetFootballPlayerCameraPoint(0, playerCamera, now);
            Draw(scene, target);
            if (frame == 20) await Save(target, "football-countdown-clear-pitch");
        }
        Check(scene.FootballState.Phase == FootballPhase.Playing && scene.FootballState.Kickers.All(k => k.Present),
            "Fresh calibrated input did not start a human-versus-AI match.");
        await Save(target, "football-playing");
        // The tip detectors' projected-scene reference matches the camera's own view of
        // the board (1000 synthetic camera pixels less the safety inset), not the projector raster.
        var projectedReference = scene.GetEyeTipProjectionFrames();
        double cameraSpan = 1000 * (1 - inset);
        Check(projectedReference.Count == 1 && Math.Abs(projectedReference[0].Width - cameraSpan) <= 2 &&
            Math.Abs(projectedReference[0].Height - cameraSpan) <= 2,
            "The projected-scene reference did not match the camera's view of the board.");
        Check(scene.GetHandAcquisitionContext(now) is { ContinuousSearchPolygon: null },
            "Black-bar play searched the pitch for hands.");
        scene.SetFootballFingerInput(0, true);
        var context = scene.GetHandAcquisitionContext(now);
        Check(context?.ContinuousSearchPolygon?.Length == 4, "The pitch did not allow finger acquisition away from buttons.");
        var hold = scene.GetHoldButtonContext(now);
        Check(hold is not null && hold.EnabledHoldIds.SetEquals(["football-exit", "football-reset", "football-mode"]),
            "Football controls did not expose caption-based hold references.");
        var beforeDrop = scene.FootballState.BallPosition;
        var aiBeforeDrop = scene.FootballState.Kickers[1].Position;
        now += TimeSpan.FromMilliseconds(300);
        Draw(scene, target);
        Check(scene.FootballState.Phase == FootballPhase.Playing && scene.FootballState.RecoveringInput &&
            !scene.FootballState.Kickers[0].Present && scene.FootballState.BallPosition == beforeDrop &&
            scene.FootballState.Kickers[1].Position == aiBeforeDrop,
            "A short tracking interruption did not quietly freeze the ball and computer.");
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
        now += TimeSpan.FromMilliseconds(16);
        scene.SetFootballPlayerCameraPoint(0, playerCamera, now);
        Draw(scene, target);
        Check(scene.FootballState.Phase == FootballPhase.Countdown,
            "Returning after a sustained loss bypassed the reconnect countdown.");
        Check(scene.ActivateFootballButton("football-mode") && scene.FootballState.Mode == FootballMode.TwoHumans,
            "The projected two-player control failed.");
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
        Check(scene.ActivateFootballButton("football-reset") && scene.FootballState.Score1 == 0 &&
            scene.FootballState.Score2 == 0 && scene.FootballState.Phase == FootballPhase.WaitingForPlayers,
            "Reset did not require fresh input for a new match.");
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
                        pitch.Y + (point.Y + .5) * pitch.Height, anchorInset, turnBoard);
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
                        pitch.Y + (point.Y + .5) * pitch.Height, anchorInset, turnBoard);
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

        using var renderer = new FootballRenderer(device);
        await renderer.EnsurePitchAsync();
        using var raw = new CanvasRenderTarget(device, 1824, 1132, 96);
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
        for (int y = 650; y < 735; y++)
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
        int redCarPixels = CountTeamStripePixels(teamCars, 432, 736, blue: false);
        int blueCarPixels = CountTeamStripePixels(teamCars, 1412, 396, blue: true);
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
                Check(ChangedPixels(undecorated, celebrated, loserLeft, 0, 912, 1132) == 0,
                    "Victory confetti crossed onto the losing player's half.");
                int changes = ChangedPixels(undecorated, celebrated, winnerLeft, 0, 912, 1132);
                Check(changes > 1500, "The winning half did not receive a substantial confetti shower.");
                confettiPixels = Math.Max(confettiPixels, changes);
                await Save(raw, $"football-player-{winner + 1}-victory-{seconds:0.00}s");
                if (seconds == FootballRenderer.VictoryDurationSeconds)
                {
                    settled = celebrated;
                    // This lies inside the opaque hood, away from its alpha edge.
                    // A large RGB change here verifies pieces render over the car.
                    int onCar = ChangedPixels(undecorated, celebrated,
                        winner == 0 ? 496 : 1296, 546, 32, 40, minimumChange: 24);
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
        foreach (double aspect in new[] { 1.0, 9.0 / 16 })
        {
            using var fitted = MakeScene(aspect, out double fittedInset);
            await fitted.EnsureBoardArtworkResourcesAsync(device, BoardScreen.Football);
            using var output = new CanvasRenderTarget(device, (float)(900 * aspect), 900, 96);
            Draw(fitted, output);
            var fittedField = fitted.FootballFieldBounds();
            Check(Math.Abs(fittedField.Width * aspect / fittedField.Height - FootballGame.Width) < .0001,
                "A changed board aspect stretched the pitch or ball.");
            await Save(output, aspect == 1 ? "football-square" : "football-portrait");
            VerifyGrassMarkerBounds(fitted, fittedInset, output);
        }
        return new { passed = true, renderedFrames, shadowPixels, redCarPixels, blueCarPixels,
            confettiPixels, confettiOnCarPixels, victoryClock = true, winnerHalfOnly = true,
            victorySettles = true, ordinaryGoalsWithoutConfetti = true,
            teamCarArtwork = true, calibratedInput = true,
            staleAndFutureRejected = true, shortLossRecoversQuietly = true, lossPausesPlay = true, twoPlayers = true,
            quarterTurnMapping = true, barPositionAndRotationAnchored = true, markersUseSurroundingGrass = true,
            captionControls = true, continuousFingerAcquisition = true, directory, images };

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
            Check(clockScene.FootballVictorySeconds == FootballRenderer.VictoryDurationSeconds,
                "The victory clock did not stop at the settled frame.");
            clockScene.UpdateFootballVictory(victory, now.AddSeconds(5));
            Check(clockScene.FootballVictorySeconds == FootballRenderer.VictoryDurationSeconds,
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
                PixelPoint WorldCamera(Vector2 point) => FieldCamera(point.X / FootballGame.Width + .5, point.Y + .5);

                Check(!current.TryMapFootballMarkerCameraPoint(Camera(.5, .04, padding, turnBoard), out _) &&
                    !current.TryMapFootballMarkerCameraPoint(Camera(.5, .95, padding, turnBoard), out _),
                    "The scoreboard or bottom controls accepted a football marker.");
                foreach (var point in new PixelPoint[] { new(-.069, .5), new(1.069, .5),
                    new(.5, -.065), new(.5, 1.065) })
                    Check(!current.TryMapFootballMarkerCameraPoint(FieldCamera(point.X, point.Y), out _),
                        "A point beyond the rendered grass accepted a football marker.");
                foreach (bool right in new[] { false, true })
                foreach (bool bottom in new[] { false, true })
                {
                    // These lie inside the turf's rectangular bounds, on opposite sides
                    // of its rounded corner. Transparent corner pixels cannot accept input.
                    double insideX = (right ? 1703 : -103) / 1600.0;
                    double insideY = (bottom ? 1057 : -57) / 1000.0;
                    double outsideX = (right ? 1708 : -108) / 1600.0;
                    double outsideY = (bottom ? 1062 : -62) / 1000.0;
                    Check(current.TryMapFootballMarkerCameraPoint(FieldCamera(insideX, insideY), out _) &&
                        !current.TryMapFootballMarkerCameraPoint(FieldCamera(outsideX, outsideY), out _),
                        "Marker acceptance did not follow the rendered rounded grass corner.");
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
                        new(-FootballGame.Width / 2 + FootballGame.KickerRadius, -.5f + FootballGame.KickerRadius),
                        new(FootballGame.Width / 2 - FootballGame.KickerRadius, .5f - FootballGame.KickerRadius));
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
            renderer.Draw(drawing, new Rect(112, 66, 1600, 1000), state, victorySeconds);
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
