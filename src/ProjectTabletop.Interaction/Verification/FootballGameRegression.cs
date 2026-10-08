using System.Numerics;
using System.Reflection;
using ProjectTabletop.Interaction;

internal static class FootballGameRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    public static void Run()
    {
        CheckCountdownPauseAndReset();
        CheckFastSweptKickAndSpeedCap();
        CheckBouncesAndRotation();
        CheckGoalsPostsAndHighBalls();
        CheckInputSafetyAndAppearances();
        CheckAiAndDeterminism();
        CheckWaitingKickersIdleFramesAndHeading();
        Console.WriteLine("Football verification passed: fresh-input countdown/pause/reset, swept bumper impacts, " +
            "speed caps, lively boundary/floor rebounds, height and quaternion rotation, exact-once goals, " +
            "posts/high balls, first-to-five, invalid/stale/jumping input, equal kicker styles, bounded physical AI, " +
            "kickers following input while paused, stable idle revisions and simulation-timed headings.");
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
        Require(paused.Phase == FootballPhase.WaitingForPlayers && !paused.Kickers[0].Present &&
            paused.BallPosition == previous.BallPosition, "A stale camera must pause without a catch-up kick.");
        game.Advance(rig.Now.AddSeconds(2));
        Require(game.Snapshot.BallPosition == paused.BallPosition, "A missing human must freeze play.");
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

    private sealed class Rig(FootballGame game, DateTimeOffset now)
    {
        public FootballGame Game { get; } = game;
        public DateTimeOffset Now = now;
        public Vector2 P1 = new(-.52f, .35f);
        public Vector2 P2 = new(.52f, -.35f);
        public void Step(int count = 1)
        {
            for (int i = 0; i < count; i++)
            {
                Now = Now.AddTicks(TimeSpan.TicksPerSecond / 120);
                Game.SetPlayerInput(0, P1, Now);
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
