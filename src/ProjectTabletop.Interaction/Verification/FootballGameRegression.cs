using System.Numerics;
using System.Reflection;
using ProjectTabletop.Interaction;

internal static class FootballGameRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    public static void Run()
    {
        CheckCountdownPauseAndReset();
        CheckQuietTrackingRecovery();
        CheckRecoveryCountdownAndHitches();
        CheckFastSweptKickAndSpeedCap();
        CheckBouncesAndRotation();
        CheckGoalsPostsAndHighBalls();
        CheckInputSafetyAndAppearances();
        CheckAiAndDeterminism();
        CheckAiGetsBehindTheBall();
        CheckWaitingKickersIdleFramesAndHeading();
        CheckMeasuredBarPose();
        CheckMeasuredBarCollisionSafety();
        Console.WriteLine("Football verification passed: fresh-input countdown/pause/reset, swept bumper impacts, " +
            "speed caps, lively boundary/floor rebounds, height and quaternion rotation, exact-once goals, " +
            "posts/high balls, first-to-five, invalid/stale/jumping input, equal kicker styles, bounded physical AI, " +
            "kickers following input while paused, stable idle revisions, simulation-timed headings, " +
            "rigid measured bar poses, bounded observation-speed collision movement and quiet short tracking recovery.");
    }

    private static void CheckCountdownPauseAndReset()
    {
        var game = new FootballGame();
        Require(game.Snapshot.Phase == FootballPhase.WaitingForPlayers && game.Snapshot.Score1 == 0,
            "A new game must wait for its human player.");
        game.Advance(Epoch);
        game.SetPlayerInput(0, new(-.4f, .3f), Epoch.AddMilliseconds(1));
        game.Advance(Epoch.AddMilliseconds(1));
        Require(game.Snapshot.Phase == FootballPhase.Countdown && game.Snapshot.Kickers[1].IsAi,
            "One human must start a countdown against AI.");
        var rig = new Rig(game, Epoch.AddMilliseconds(1));
        rig.P1 = new(-.4f, .3f);
        rig.Step(210);
        Require(game.Snapshot.Phase == FootballPhase.Playing, "Countdown must finish with continuously fresh input.");
        var previous = game.Snapshot;
        game.Advance(rig.Now.AddMilliseconds(300));
        var paused = game.Snapshot;
        Require(paused.Phase == FootballPhase.Playing && paused.RecoveringInput && !paused.Kickers[0].Present &&
            paused.BallPosition == previous.BallPosition && paused.BallVelocity == Vector2.Zero,
            "A brief stale camera must freeze quietly without a catch-up kick or another countdown.");
        game.Advance(rig.Now.AddSeconds(2));
        Require(game.Snapshot.Phase == FootballPhase.WaitingForPlayers && !game.Snapshot.RecoveringInput &&
            game.Snapshot.BallPosition == paused.BallPosition, "A sustained missing human must pause play.");
        rig.Now = rig.Now.AddSeconds(2);
        rig.Step();
        Require(game.Snapshot.Phase == FootballPhase.Countdown, "Reacquiring a human must give a fresh countdown.");
        game.SetStyle(0, FootballKickerStyle.Boot);
        game.Reset(rig.Now.AddMilliseconds(1));
        Require(game.Snapshot.Phase == FootballPhase.WaitingForPlayers && !game.Snapshot.Kickers[0].Present &&
            game.Snapshot.BallPosition == Vector2.Zero && game.Snapshot.Score1 == 0 &&
            game.Snapshot.Kickers[0].Style == FootballKickerStyle.Boot, "Reset must clear observations/scores but preserve style.");
        game.SetMode(FootballMode.TwoHumans, rig.Now.AddMilliseconds(2));
        game.SetPlayerInput(0, rig.P1, rig.Now.AddMilliseconds(3));
        game.Advance(rig.Now.AddMilliseconds(3));
        Require(game.Snapshot.Phase == FootballPhase.WaitingForPlayers && !game.Snapshot.Kickers[1].IsAi,
            "Two humans must require both actual human inputs.");
        var immutable = game.Snapshot;
        game.SetStyle(0, FootballKickerStyle.Pan);
        Require(immutable.Kickers[0].Style == FootballKickerStyle.Boot, "An old snapshot must not change after style selection.");
        Require(immutable.Kickers is not FootballKickerSnapshot[], "Snapshot kicker collection must not expose a mutable array.");
    }

    private static void CheckQuietTrackingRecovery()
    {
        var rig = Playing(FootballMode.HumanVsAi);
        rig.H1 = .4f;
        rig.Step(20);
        SeedBall(rig.Game, Vector2.Zero, new(.4f, .1f), .12f, .2f);
        var before = rig.Game.Snapshot;
        for (int gap = 300; gap <= 600; gap += 100)
        {
            rig.Game.Advance(rig.Now.AddMilliseconds(gap));
            var frozen = rig.Game.Snapshot;
            Require(frozen.RecoveringInput && frozen.Phase == FootballPhase.Playing && !frozen.Kickers[0].Present &&
                frozen.Kickers[0].MarkerAnchored && frozen.Kickers[0].Heading == .4f &&
                frozen.BallPosition == before.BallPosition && frozen.BallHeight == before.BallHeight &&
                frozen.BallRotation == before.BallRotation && frozen.BallVelocity == Vector2.Zero &&
                frozen.BallVerticalVelocity == 0 && frozen.Kickers.All(kicker => kicker.Velocity == Vector2.Zero) &&
                frozen.Kickers[1].Position == before.Kickers[1].Position && frozen.Score1 == before.Score1 && frozen.Score2 == before.Score2,
                "Quiet recovery must preserve the measured pose and freeze the airborne ball and AI without scoring.");
        }
        rig.Now = rig.Now.AddMilliseconds(650);
        rig.P1 = new(.6f, .2f);
        rig.H1 = -.5f;
        rig.Game.SetPlayerInput(0, rig.P1, rig.Now, rig.H1);
        rig.Game.Advance(rig.Now);
        var resumed = rig.Game.Snapshot;
        Require(!resumed.RecoveringInput && resumed.Phase == FootballPhase.Playing && resumed.Kickers[0].Present &&
            resumed.Kickers[0].Position == rig.P1 && resumed.Kickers[0].Heading == rig.H1 &&
            resumed.Kickers.All(kicker => kicker.Velocity == Vector2.Zero) && resumed.BallPosition == before.BallPosition &&
            resumed.BallVelocity == before.BallVelocity && resumed.BallVerticalVelocity == before.BallVerticalVelocity,
            "A confirmed short-gap return must resume the saved motion immediately without a catch-up sweep.");
        rig.Step();
        Require(rig.Game.Snapshot.Phase == FootballPhase.Playing && rig.Game.Snapshot.BallPosition != before.BallPosition,
            "The ball did not continue naturally after quiet tracking recovery.");

        var two = Playing();
        var last = two.Now;
        var original = two.Game.Snapshot;
        two.Game.Advance(last.AddMilliseconds(300));
        two.Game.SetPlayerInput(0, new(-.3f, .2f), last.AddMilliseconds(400));
        two.Game.Advance(last.AddMilliseconds(400));
        Require(two.Game.Snapshot.RecoveringInput && two.Game.Snapshot.Kickers[0].Present &&
            !two.Game.Snapshot.Kickers[1].Present && two.Game.Snapshot.BallPosition == original.BallPosition,
            "A two-player match resumed before the other player's marker returned.");
        two.Game.SetPlayerInput(1, two.P2, last.AddMilliseconds(500));
        two.Game.Advance(last.AddMilliseconds(500));
        Require(!two.Game.Snapshot.RecoveringInput && two.Game.Snapshot.Phase == FootballPhase.Playing &&
            two.Game.Snapshot.Kickers.All(kicker => kicker.Present),
            "Both returning players could not resume a short tracking interruption.");

        var expired = Playing(FootballMode.HumanVsAi);
        var expiry = expired.Now;
        expired.Game.Advance(expiry.AddMilliseconds(300));
        expired.Game.Advance(expiry + FootballGame.TrackingRecoveryTimeout);
        Require(expired.Game.Snapshot.RecoveringInput, "The recovery grace period expired before its boundary.");
        expired.Game.Advance(expiry + FootballGame.TrackingRecoveryTimeout + TimeSpan.FromMilliseconds(1));
        Require(!expired.Game.Snapshot.RecoveringInput && expired.Game.Snapshot.Phase == FootballPhase.WaitingForPlayers,
            "A sustained marker loss did not leave quiet recovery for the normal pause.");

        var explicitLoss = Playing(FootballMode.HumanVsAi);
        explicitLoss.Game.Advance(explicitLoss.Now.AddMilliseconds(300));
        explicitLoss.Game.SetPlayerInput(0, null, explicitLoss.Now.AddMilliseconds(301));
        Require(!explicitLoss.Game.Snapshot.RecoveringInput &&
            explicitLoss.Game.Snapshot.Phase == FootballPhase.WaitingForPlayers && !explicitLoss.Game.Snapshot.Kickers[0].MarkerAnchored,
            "An explicit camera/ambiguity disarm was incorrectly treated as quiet recovery.");
        explicitLoss.Game.SetPlayerInput(0, explicitLoss.P1, explicitLoss.Now.AddMilliseconds(350));
        explicitLoss.Game.Advance(explicitLoss.Now.AddMilliseconds(350));
        Require(explicitLoss.Game.Snapshot.Phase == FootballPhase.Countdown,
            "An explicitly disarmed player bypassed the reconnect countdown.");
    }

    private static void CheckRecoveryCountdownAndHitches()
    {
        var countdown = new FootballGame();
        countdown.Advance(Epoch);
        countdown.SetPlayerInput(0, new(-.4f, .3f), Epoch.AddMilliseconds(1), 0);
        countdown.Advance(Epoch.AddMilliseconds(1));
        countdown.SetPlayerInput(0, new(-.4f, .3f), Epoch.AddMilliseconds(101), 0);
        countdown.Advance(Epoch.AddMilliseconds(101));
        float remaining = countdown.Snapshot.CountdownSeconds;
        countdown.Advance(Epoch.AddMilliseconds(401));
        Require(countdown.Snapshot.RecoveringInput && countdown.Snapshot.Phase == FootballPhase.Countdown &&
            countdown.Snapshot.CountdownSeconds == remaining, "A short tracking miss restarted or consumed the countdown.");
        countdown.SetPlayerInput(0, new(-.4f, .3f), Epoch.AddMilliseconds(501), 0);
        countdown.Advance(Epoch.AddMilliseconds(501));
        Require(!countdown.Snapshot.RecoveringInput && countdown.Snapshot.CountdownSeconds == remaining,
            "Reacquisition did not preserve the partially completed countdown.");
        countdown.SetPlayerInput(0, new(-.4f, .3f), Epoch.AddMilliseconds(601), 0);
        countdown.Advance(Epoch.AddMilliseconds(601));
        Require(countdown.Snapshot.CountdownSeconds < remaining && countdown.Snapshot.CountdownSeconds > remaining - .11f,
            "Countdown recovery banked time from the tracking interruption.");

        foreach (int gap in new[] { 300, 800 })
        {
            var hitch = Playing(FootballMode.HumanVsAi);
            var original = hitch.Game.Snapshot;
            // The camera keeps reporting while drawing is blocked, so the final sample is
            // fresh. Render cadence alone must decide whether this is a brief or sustained gap.
            for (int elapsed = 100; elapsed <= gap; elapsed += 100)
                hitch.Game.SetPlayerInput(0, hitch.P1, hitch.Now.AddMilliseconds(elapsed));
            hitch.Game.Advance(hitch.Now.AddMilliseconds(gap));
            var after = hitch.Game.Snapshot;
            Require(!after.RecoveringInput && after.Phase == (gap == 300 ? FootballPhase.Playing : FootballPhase.Countdown) &&
                after.BallPosition == original.BallPosition && after.BallHeight == original.BallHeight &&
                after.Kickers.All(kicker => kicker.Velocity == Vector2.Zero),
                "Render hitches must discard catch-up motion and only sustained gaps may restart the countdown.");
        }

        var late = Playing(FootballMode.HumanVsAi);
        late.Game.Advance(late.Now.AddMilliseconds(600));
        Require(late.Game.Snapshot.RecoveringInput, "The long-gap return fixture did not enter recovery.");
        late.Game.SetPlayerInput(0, late.P1, late.Now.AddMilliseconds(800));
        late.Game.Advance(late.Now.AddMilliseconds(800));
        Require(!late.Game.Snapshot.RecoveringInput && late.Game.Snapshot.Phase == FootballPhase.Countdown,
            "A late fresh sample hid a sustained input loss before the next render tick.");
    }

    private static void CheckFastSweptKickAndSpeedCap()
    {
        var rig = Playing();
        rig.P1 = new(-.24f, 0);
        rig.Step(50);
        SeedBall(rig.Game, Vector2.Zero, Vector2.Zero);
        // At each camera sample the tip advances 16cm. Its circular collider must sweep the path,
        // even though neither sampled endpoint overlaps the small football.
        rig.P1 = new(-.08f, 0);
        rig.Step();
        rig.P1 = new(.08f, 0);
        rig.Step(8);
        var kicked = rig.Game.Snapshot;
        Require(kicked.BallVelocity.X > 1.5f && kicked.BallPosition.X > .08f,
            "A fast tracked-tip swipe must make direct contact and launch the ball.");
        Require(kicked.BallHeight > FootballGame.BallRadius && kicked.BallVerticalVelocity > 0,
            "A kick must produce a visible hop for the height-aware shadow.");
        Require(kicked.BallVelocity.Length() <= FootballGame.MaxBallSpeed + .0001f,
            "A powerful bumper hit must obey the ball-speed cap.");
        float peak = 0;
        rig.P1 = new(-.5f, .35f);
        for (int i = 0; i < 1500; i++)
        {
            rig.Step();
            var state = rig.Game.Snapshot;
            peak = Math.Max(peak, state.BallVelocity.Length());
            Require(state.BallVelocity.Length() <= FootballGame.MaxBallSpeed + .0001f &&
                float.IsFinite(state.BallPosition.X) && float.IsFinite(state.BallHeight),
                "Repeated energetic play must remain finite and capped.");
        }
        Require(peak > 2, "The cap fixture did not exercise an energetic ball.");

        var approaching = Playing();
        approaching.P1 = new(.12f, 0);
        approaching.Step(60);
        SeedBall(approaching.Game, new(-.08f, 0), new(FootballGame.MaxBallSpeed, 0));
        approaching.P1 = new(-.10f, 0);
        approaching.Step(8);
        Require(approaching.Game.Snapshot.BallVelocity.X < -1,
            "Opposing ball and bumper motion must collide continuously rather than tunnel through.");

        var gentle = Playing();
        gentle.P1 = new(-.15f, 0);
        gentle.Step(80);
        SeedBall(gentle.Game, Vector2.Zero, Vector2.Zero);
        float gentlePeak = 0;
        for (int i = 0; i < 45; i++)
        {
            gentle.P1 = new(-.15f + i * .003f, 0);
            gentle.Step();
            gentlePeak = Math.Max(gentlePeak, gentle.Game.Snapshot.BallVelocity.Length());
        }
        Require(gentlePeak > .3f && gentlePeak < 1.1f,
            "A gentle tracked movement must nudge the ball rather than amplify each camera sample into a full-power kick.");
    }

    private static void CheckBouncesAndRotation()
    {
        var rig = Playing();
        SeedBall(rig.Game, new(0, .465f), new(.5f, 1.6f));
        var before = rig.Game.Snapshot;
        rig.Step(3);
        var rebound = rig.Game.Snapshot;
        Require(rebound.BallVelocity.Y < -1.45f && MathF.Abs(rebound.BallVelocity.X - .5f) < .01f,
            "Pitch sides should make a lively rebound without damping the tangent.");
        Require(rebound.BallRotation != Quaternion.Identity &&
            MathF.Abs(rebound.BallRotation.Length() - 1) < .0001f,
            "Panel rotation must follow travel and remain a normalized quaternion.");
        Require(before.BallPosition == new Vector2(0, .465f), "Simulation updates must not mutate old ball snapshots.");
        SeedBall(rig.Game, Vector2.Zero, new(.2f, 0), .10f, -.9f);
        bool sawBounce = false;
        float lowest = 1;
        for (int i = 0; i < 25; i++)
        {
            rig.Step();
            var state = rig.Game.Snapshot;
            lowest = Math.Min(lowest, state.BallHeight);
            sawBounce |= state.BallVerticalVelocity > .65f;
            Require(state.BallHeight >= FootballGame.BallRadius - .000001f,
                "The height-aware shadow must never place the ball below the turf.");
        }
        Require(sawBounce && lowest < .04f, "The rubber football should bounce high after landing.");
    }

    private static void CheckGoalsPostsAndHighBalls()
    {
        var rig = Playing();
        for (int score = 1; score <= FootballGame.WinningScore; score++)
        {
            SeedBall(rig.Game, new(.76f, 0), new(1.4f, 0));
            rig.Step(9);
            Require(rig.Game.Snapshot.Score1 == score && rig.Game.Snapshot.Score2 == 0,
                "A complete crossing of the right goal must score exactly once for Player 1.");
            var goal = rig.Game.Snapshot;
            rig.Step(60);
            Require(rig.Game.Snapshot.Score1 == score, "A ball held in the goal must not score again.");
            if (score < FootballGame.WinningScore)
            {
                Require(goal.Phase == FootballPhase.Goal, "A non-winning goal must show a goal celebration.");
                rig.Step(310);
                Require(rig.Game.Snapshot.Phase == FootballPhase.Playing, "A goal must center and serve after a countdown.");
            }
        }
        Require(rig.Game.Snapshot.Phase == FootballPhase.Finished && rig.Game.Snapshot.Banner == "Player 1 wins!",
            "First to five must finish the match.");
        var finished = rig.Game.Snapshot;
        rig.Step(500);
        Require(rig.Game.Snapshot.Score1 == finished.Score1 && rig.Game.Snapshot.BallPosition == finished.BallPosition,
            "A finished match must not continue scoring or simulating the ball.");

        var post = Playing();
        SeedBall(post.Game, new(.71f, FootballGame.GoalHalfWidth), new(2, 0));
        post.Step(9);
        Require(post.Game.Snapshot.Score1 == 0 && post.Game.Snapshot.BallVelocity.X < 0,
            "The ball must rebound from a goalpost instead of scoring through it.");
        var high = Playing();
        SeedBall(high.Game, new(.75f, 0), new(1.8f, 0), .23f, 0);
        high.Step(8);
        Require(high.Game.Snapshot.Score1 == 0 && high.Game.Snapshot.BallVelocity.X < 0,
            "A ball above the goal opening must not score.");
        var partial = Playing();
        SeedBall(partial.Game, new(.79f, 0), new(.2f, 0));
        partial.Step(10);
        Require(partial.Game.Snapshot.Score1 == 0, "A ball partly across the line must not score until entirely over it.");
        partial.Step(30);
        Require(partial.Game.Snapshot.Score1 == 1, "The same low ball must score when completely across the line.");
        var left = Playing();
        SeedBall(left.Game, new(-.76f, 0), new(-1.4f, 0));
        left.Step(9);
        Require(left.Game.Snapshot.Score2 == 1, "The left goal must award Player 2.");
    }

    private static void CheckInputSafetyAndAppearances()
    {
        var rig = Playing();
        SeedBall(rig.Game, Vector2.Zero, Vector2.Zero);
        rig.P1 = new(.6f, .1f);
        rig.Step();
        Require(rig.Game.Snapshot.BallVelocity == Vector2.Zero,
            "An implausible tracking jump must reacquire without sweeping through the ball.");
        rig.Game.SetPlayerInput(0, new(float.NaN, 0), rig.Now.AddMilliseconds(1));
        rig.Game.Advance(rig.Now.AddMilliseconds(1));
        Require(!rig.Game.Snapshot.Kickers[0].Present && rig.Game.Snapshot.Phase == FootballPhase.WaitingForPlayers,
            "A non-finite observation must disarm and pause.");
        rig.Game.SetPlayerInput(0, new(20, 20), rig.Now.AddMilliseconds(2));
        rig.Game.Advance(rig.Now.AddMilliseconds(2));
        Require(!rig.Game.Snapshot.Kickers[0].Present, "Outside-pitch observations must not clamp into an active player.");
        rig.Now = rig.Now.AddMilliseconds(2);
        rig.P1 = new(-.5f, .3f);
        rig.Step(220);
        var before = rig.Game.Snapshot;
        rig.Game.SetPlayerInput(0, Vector2.Zero, rig.Now.AddSeconds(-1));
        rig.Game.Advance(rig.Now.AddSeconds(-1));
        Require(rig.Game.Snapshot.BallPosition == before.BallPosition &&
            rig.Game.Snapshot.Kickers[0].Position == before.Kickers[0].Position,
            "Old timestamps must not replay movement or advance the game backward.");
        var variants = Enum.GetValues<FootballKickerStyle>().Select(style =>
        {
            var variant = Playing();
            variant.Game.SetStyle(0, style);
            variant.P1 = new(-.13f, 0);
            variant.Step(60);
            SeedBall(variant.Game, Vector2.Zero, new(-1, 0));
            variant.Step(10);
            return variant.Game.Snapshot;
        }).ToArray();
        Require(variants.All(state => state.BallVelocity == variants[0].BallVelocity &&
            state.BallPosition == variants[0].BallPosition && state.BallHeight == variants[0].BallHeight),
            "Car, glove, pan and boot must have exactly equal physical footprints and response.");
    }

    private static void CheckAiAndDeterminism()
    {
        var first = Playing(FootballMode.HumanVsAi);
        var second = Playing(FootballMode.HumanVsAi);
        SeedBall(first.Game, new(.28f, .02f), new(.34f, .15f));
        SeedBall(second.Game, new(.28f, .02f), new(.34f, .15f));
        bool sawPhysicalReturn = false;
        var previousAi = first.Game.Snapshot.Kickers[1].Position;
        for (int i = 0; i < 1000; i++)
        {
            first.Step();
            second.Step();
            var a = first.Game.Snapshot;
            var b = second.Game.Snapshot;
            var ai = a.Kickers[1];
            Require(a.BallPosition == b.BallPosition && a.BallVelocity == b.BallVelocity &&
                a.BallRotation == b.BallRotation && a.Score1 == b.Score1 && a.Score2 == b.Score2,
                "Identical observations and fixed-step clocks must produce identical football.");
            Require(ai.IsAi && ai.Present && ai.Position.X >= .039f &&
                ai.Velocity.Length() <= FootballGame.AiMaxSpeed + .0001f &&
                Vector2.Distance(previousAi, ai.Position) <= FootballGame.AiMaxSpeed / 120 + .0001f,
                "AI must move continuously at its advertised speed and defend its own half.");
            if (i < 180 && a.BallPosition.X > 0 && a.BallVelocity.X < -.3f) sawPhysicalReturn = true;
            previousAi = ai.Position;
        }
        Require(sawPhysicalReturn, "The computer must actually reach and physically return a reachable ball.");
        var aiBefore = first.Game.Snapshot.Kickers[1];
        first.Game.SetPlayerInput(1, new(-.7f, -.4f), first.Now.AddMilliseconds(1));
        Require(first.Game.Snapshot.Kickers[1].Position == aiBefore.Position,
            "Human observations must not teleport the computer player.");
    }

    private static void CheckAiGetsBehindTheBall()
    {
        foreach (float y in new[] { -.32f, 0f, .32f })
        {
            var rig = Playing(FootballMode.HumanVsAi);
            SeedBall(rig.Game, new(.46f, y), Vector2.Zero);
            SeedAi(rig.Game, new(.32f, y));
            bool reachedBehind = false, returned = false, usedClearLane = false;
            for (int frame = 0; frame < 480; frame++)
            {
                rig.Step();
                var state = rig.Game.Snapshot;
                var ai = state.Kickers[1];
                usedClearLane |= MathF.Abs(ai.Position.Y - y) > .10f;
                reachedBehind |= ai.Position.X > state.BallPosition.X + .057f;
                Require(state.BallPosition.X <= .465f && state.Score1 == 0,
                    "An AI caught in front of the ball must go around it, not push it towards its own goal.");
                Require(ai.Velocity.Length() <= FootballGame.AiMaxSpeed + .0001f,
                    "Going around the ball must preserve the physical AI speed limit.");
                if (state.BallVelocity.X < -.25f)
                {
                    returned = true;
                    break;
                }
            }
            Require(usedClearLane && reachedBehind && returned,
                "The AI must circle a stationary ball, reach its own-goal side, then physically kick left.");
        }

        var aligned = Playing(FootballMode.HumanVsAi);
        SeedBall(aligned.Game, new(.45f, 0), Vector2.Zero);
        SeedAi(aligned.Game, new(.65f, 0));
        bool directReturn = false;
        for (int frame = 0; frame < 100; frame++)
        {
            aligned.Step();
            var state = aligned.Game.Snapshot;
            Require(MathF.Abs(state.Kickers[1].Position.Y) < .01f,
                "An AI already aligned behind the ball should strike directly without circling it.");
            if (state.BallVelocity.X < -.25f) { directReturn = true; break; }
        }
        Require(directReturn, "An AI behind the ball did not deliver a physical leftward strike.");
    }

    private static void CheckWaitingKickersIdleFramesAndHeading()
    {
        var game = new FootballGame();
        game.SetMode(FootballMode.TwoHumans, Epoch);
        game.Advance(Epoch);
        var now = Epoch;
        foreach (var target in new Vector2[] { new(-.5f, .1f), new(-.45f, .08f), new(-.41f, .05f) })
        {
            now = now.AddMilliseconds(30);
            game.SetPlayerInput(0, target, now);
            game.Advance(now);
            var kicker = game.Snapshot.Kickers[0];
            Require(game.Snapshot.Phase == FootballPhase.WaitingForPlayers && kicker.Present &&
                kicker.Position == target && kicker.Velocity == Vector2.Zero,
                "A present player's kicker must follow its input while waiting for the other player.");
        }
        var idle = game.Snapshot;
        game.Advance(now.AddMilliseconds(10));
        Require(ReferenceEquals(idle, game.Snapshot) && game.Revision == idle.Revision,
            "An unchanged pause must keep its revision so renderers can reuse the frame.");

        var rig = Playing();
        float before = rig.Game.Snapshot.Kickers[0].Heading;
        rig.P1 = new(-.52f, .30f);
        rig.Step(30);
        float after = rig.Game.Snapshot.Kickers[0].Heading;
        Require(before == 0 && after < -.05f && after > -MathF.PI / 2 - .0001f,
            "A moving kicker must turn toward its motion at the fixed simulated rate.");
    }

    private static void CheckMeasuredBarPose()
    {
        var waiting = new FootballGame();
        waiting.SetMode(FootballMode.TwoHumans, Epoch);
        var point = new Vector2(-.45f, .15f);
        waiting.SetPlayerInput(0, point, Epoch.AddMilliseconds(1), .3f);
        waiting.Advance(Epoch.AddMilliseconds(1));
        waiting.SetPlayerInput(0, point, Epoch.AddMilliseconds(31), MathF.PI / 2);
        waiting.Advance(Epoch.AddMilliseconds(31));
        var paused = waiting.Snapshot.Kickers[0];
        Require(waiting.Snapshot.Phase == FootballPhase.WaitingForPlayers && paused.Present &&
            paused.MarkerAnchored && paused.Position == point && paused.Heading == MathF.PI / 2 &&
            paused.Velocity == Vector2.Zero && waiting.Snapshot.BallPosition == Vector2.Zero,
            "A stationary bar must rotate its attached kicker immediately while waiting for Player 2.");

        var rig = Playing();
        rig.H1 = MathF.PI / 2;
        rig.Step(20);
        for (int i = 0; i < 20; i++)
        {
            rig.P1 += new Vector2(.003f, -.002f);
            rig.Step();
            var pose = rig.Game.Snapshot.Kickers[0];
            Require(pose.MarkerAnchored && pose.Position == rig.P1 && pose.Heading == MathF.PI / 2,
                "A measured bar must keep its exact visible anchor and orientation when moving sideways or backwards.");
            Require(pose.Velocity.Length() <= FootballGame.MaxKickerSpeed + .0001f,
                "Exact bar anchoring must not bypass the physical bumper speed cap.");
        }
        rig.Step(20);
        rig.H1 = -1.2f;
        rig.Step();
        var rotated = rig.Game.Snapshot.Kickers[0];
        Require(rotated.Heading == -1.2f && rotated.Position == rig.P1 && rotated.Velocity == Vector2.Zero,
            "Stationary marker rotation must set heading directly without waiting for positional movement.");

        rig.H1 = null;
        rig.Step();
        float fingerBefore = rig.Game.Snapshot.Kickers[0].Heading;
        rig.P1 += new Vector2(.06f, .02f);
        rig.Step(8);
        Require(!rig.Game.Snapshot.Kickers[0].MarkerAnchored &&
            rig.Game.Snapshot.Kickers[0].Heading > fingerBefore,
            "Returning to fingertip input must clear measured facing and restore movement-driven turning.");

        rig.Game.SetPlayerInput(0, rig.P1, rig.Now.AddMilliseconds(1), float.NaN);
        rig.Game.Advance(rig.Now.AddMilliseconds(1));
        Require(!rig.Game.Snapshot.Kickers[0].Present && !rig.Game.Snapshot.Kickers[0].MarkerAnchored &&
            rig.Game.Snapshot.Phase == FootballPhase.WaitingForPlayers,
            "A non-finite measured heading must safely disarm the input and clear its pose override.");
        rig.Game.Reset(rig.Now.AddMilliseconds(2));
        Require(!rig.Game.Snapshot.Kickers[0].MarkerAnchored && rig.Game.Snapshot.Kickers[0].Heading == 0,
            "A match reset must clear the measured pose and restore the initial heading.");

        var board = new BoardSession();
        board.ShowFootball(Epoch);
        board.SetFootballInput(0, point, Epoch.AddMilliseconds(1), .6f);
        board.TickFootball(Epoch.AddMilliseconds(1));
        Require(board.FootballState.Kickers[0].MarkerAnchored && board.FootballState.Kickers[0].Heading == .6f,
            "BoardSession must forward a tracked bar's heading to the game.");
    }

    private static void CheckMeasuredBarCollisionSafety()
    {
        var gentle = Playing();
        gentle.H1 = 0;
        gentle.P1 = new(-.15f, 0);
        gentle.Step(30);
        SeedBall(gentle.Game, Vector2.Zero, Vector2.Zero);
        float peak = 0;
        // A realistic 30Hz camera interval contains eight simulation steps. Physical contact
        // must use the bar's .3m/s observation speed instead of one frame's jump / one step.
        for (int i = 0; i < 20; i++)
        {
            gentle.Now = gentle.Now.AddTicks(TimeSpan.TicksPerSecond / 30);
            gentle.P1 += new Vector2(.01f, 0);
            gentle.Game.SetPlayerInput(0, gentle.P1, gentle.Now, gentle.H1);
            gentle.Game.SetPlayerInput(1, gentle.P2, gentle.Now);
            gentle.Game.Advance(gentle.Now);
            peak = Math.Max(peak, gentle.Game.Snapshot.BallVelocity.Length());
            Require(gentle.Game.Snapshot.Kickers[0].Position == gentle.P1,
                "A 30Hz bar sample must leave no visible follow-through lag.");
        }
        Require(peak > .3f && peak < 1.1f,
            "A gently moved bar must make a physical nudge without amplifying camera intervals into maximum-power hits.");

        var fast = Playing();
        fast.H1 = .7f;
        fast.P1 = new(-.24f, 0);
        fast.Step(30);
        SeedBall(fast.Game, Vector2.Zero, Vector2.Zero);
        fast.P1 = new(-.08f, 0);
        fast.Step();
        fast.P1 = new(.08f, 0);
        fast.Step(12);
        Require(fast.Game.Snapshot.BallVelocity.X > 1.5f &&
            fast.Game.Snapshot.BallVelocity.Length() <= FootballGame.MaxBallSpeed + .0001f &&
            fast.Game.Snapshot.Kickers[0].Velocity.Length() <= FootballGame.MaxKickerSpeed + .0001f,
            "A fast anchored-bar sweep must still hit the ball continuously with bounded physical speeds.");

        var refacing = Playing();
        refacing.H1 = -1.51f;
        refacing.P1 = new(-.3f, -.11f);
        refacing.Step(30);
        SeedBall(refacing.Game, new(-.3f, 0), Vector2.Zero);
        refacing.H1 = 1.51f;
        refacing.P1 = new(-.3f, .11f);
        refacing.Step(20);
        Require(refacing.Game.Snapshot.BallPosition == new Vector2(-.3f, 0) &&
            refacing.Game.Snapshot.BallVelocity == Vector2.Zero &&
            refacing.Game.Snapshot.Kickers[0].Position == refacing.P1 &&
            refacing.Game.Snapshot.Kickers[0].Heading == refacing.H1,
            "Switching a side-on bar's facing must reacquire the new anchor without sweeping through the ball between its two sides.");

        var jump = Playing();
        jump.H1 = 0;
        jump.Step(20);
        SeedBall(jump.Game, Vector2.Zero, Vector2.Zero);
        jump.P1 = new(.6f, .1f);
        jump.H1 = MathF.PI;
        jump.Step(15);
        Require(jump.Game.Snapshot.BallVelocity == Vector2.Zero && jump.Game.Snapshot.Kickers[0].Position == jump.P1,
            "A discontinuous bar pose must reacquire at its target without a phantom sweep through the ball.");
        jump.Game.Advance(jump.Now.AddMilliseconds(300));
        Require(jump.Game.Snapshot.RecoveringInput && !jump.Game.Snapshot.Kickers[0].Present &&
            jump.Game.Snapshot.Kickers[0].MarkerAnchored,
            "A briefly stale bar must preserve its visual pose while suspending play.");
        jump.Now = jump.Now.AddMilliseconds(300);
        jump.P1 = new(-.6f, .1f);
        jump.H1 = -.5f;
        jump.Step();
        Require(jump.Game.Snapshot.Phase == FootballPhase.Playing && !jump.Game.Snapshot.RecoveringInput &&
            jump.Game.Snapshot.Kickers[0].Position == jump.P1 &&
            jump.Game.Snapshot.Kickers[0].Heading == -.5f &&
            jump.Game.Snapshot.Kickers[0].Velocity == Vector2.Zero &&
            jump.Game.Snapshot.BallVelocity == Vector2.Zero,
            "Reacquiring a briefly stale bar must resume at its new pose with no saved motion or phantom kick.");
    }

    private static Rig Playing(FootballMode mode = FootballMode.TwoHumans)
    {
        var game = new FootballGame();
        game.SetMode(mode, Epoch);
        game.Advance(Epoch);
        var rig = new Rig(game, Epoch);
        rig.Step(210);
        Require(game.Snapshot.Phase == FootballPhase.Playing, "Fixture did not enter live play.");
        return rig;
    }

    // Arrange otherwise rare physical boundary conditions without adding test-only mutation to
    // the production API. Each scenario continues through the public input/Advance interface.
    private static void SeedBall(FootballGame game, Vector2 position, Vector2 velocity,
        float height = FootballGame.BallRadius, float vertical = 0)
    {
        Set("_ballPosition", position);
        Set("_ballVelocity", velocity);
        Set("_ballHeight", height);
        Set("_ballVerticalVelocity", vertical);
        Set("_ballRotation", Quaternion.Identity);
        Set("_ballSpin", 0f);
        Set("_phase", FootballPhase.Playing);
        Set("_servePending", false);
        Set("_snapshot", null);
        void Set(string name, object? value) => typeof(FootballGame)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, value);
    }

    private static void SeedAi(FootballGame game, Vector2 position)
    {
        var kickers = (Array)typeof(FootballGame).GetField("_kickers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
        var ai = kickers.GetValue(1)!;
        foreach (string name in new[] { "Position", "Target", "StepStart" })
            ai.GetType().GetField(name)!.SetValue(ai, position);
        ai.GetType().GetField("Velocity")!.SetValue(ai, Vector2.Zero);
        ai.GetType().GetField("CollisionQuiet")!.SetValue(ai, 0f);
        typeof(FootballGame).GetField("_aiThinkRemaining", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, 0f);
        typeof(FootballGame).GetField("_aiRouteSide", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, 0);
        typeof(FootballGame).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, null);
    }

    private sealed class Rig(FootballGame game, DateTimeOffset now)
    {
        public FootballGame Game { get; } = game;
        public DateTimeOffset Now = now;
        public Vector2 P1 = new(-.52f, .35f);
        public Vector2 P2 = new(.52f, -.35f);
        public float? H1;
        public void Step(int count = 1)
        {
            for (int i = 0; i < count; i++)
            {
                Now = Now.AddTicks(TimeSpan.TicksPerSecond / 120);
                Game.SetPlayerInput(0, P1, Now, H1);
                Game.SetPlayerInput(1, P2, Now);
                Game.Advance(Now);
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
