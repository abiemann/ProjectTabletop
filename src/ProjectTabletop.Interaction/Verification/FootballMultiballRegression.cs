using System.Collections;
using System.Numerics;
using System.Reflection;
using ProjectTabletop.Interaction;

internal static class FootballMultiballRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    public static void Run()
    {
        CheckWavesAndGrowth();
        CheckPausedClockAndGrowth();
        CheckGoalAndMatchReset();
        CheckBallContacts();
        CheckAiSecondaryBall();
        Console.WriteLine("Football multiball verification passed: active-play 15/30-second waves, exact spawn locations, " +
            "growth and stationary overlap separation, seven-ball limit, frozen input recovery/countdown, " +
            "secondary and simultaneous goals, fresh rally/match reset, immutable snapshots, finite ball contacts and AI threats.");
    }

    private static void CheckWavesAndGrowth()
    {
        var rig = Playing();
        var initial = rig.Game.Snapshot;
        rig.Advance(14.99);
        Require(rig.Game.Snapshot.Balls.Count == 1, "The first wave arrived before 15 seconds of active play.");
        rig.Advance(.02);
        var first = rig.Game.Snapshot;
        Require(first.Balls.Count == 3, "The first 15-second wave did not add exactly two balls.");
        var side = first.Balls.Skip(1).OrderBy(ball => ball.Position.Y).ToArray();
        float sideline = FootballGame.Height / 2 - FootballGame.BallRadius;
        Require(side[0].Position == new Vector2(0, -sideline) && side[1].Position == new Vector2(0, sideline),
            "The first wave did not appear on the two sidelines at halfway.");
        Require(side.All(ball => ball.Growth >= 0 && ball.Growth < .1f && ball.Velocity == Vector2.Zero),
            "New balls must begin small and stationary.");
        rig.Advance(FootballGame.BallGrowthDuration / 2);
        Require(rig.Game.Snapshot.Balls.Skip(1).All(ball => ball.Growth is > .4f and < .65f &&
            ball.Velocity == Vector2.Zero), "Ball growth did not advance smoothly during active play.");
        rig.Advance(FootballGame.BallGrowthDuration);
        Require(rig.Game.Snapshot.Balls.All(ball => ball.Growth == 1), "Growing balls did not reach full size.");
        Require(first.Balls.Skip(1).All(ball => ball.Growth < .1f) && initial.Balls.Count == 1 &&
            first.Balls is not FootballBallSnapshot[], "Later growth mutated or exposed a mutable old snapshot.");

        rig.AdvanceTo(29.99);
        Require(rig.Game.Snapshot.Balls.Count == 3, "The second wave arrived before 30 seconds of active play.");
        // Move mature sideline balls away so this assertion isolates exact requested spawn coordinates.
        SeedBall(rig.Game, 1, new(-.3f, -.35f), Vector2.Zero);
        SeedBall(rig.Game, 2, new(.3f, .35f), Vector2.Zero);
        rig.Advance(.02);
        var second = rig.Game.Snapshot;
        float circleEdge = FootballGame.CenterCircleRadius + FootballGame.BallRadius;
        var additions = second.Balls.Skip(3).Select(ball => ball.Position.Y).Order().ToArray();
        Require(second.Balls.Count == 7 && second.Balls.Skip(3).All(ball => ball.Position.X == 0) &&
            additions.SequenceEqual(new[] { -sideline, -circleEdge, circleEdge, sideline }),
            "The second wave must add two halfway sideline balls and two touching the outside of the centre circle.");
        Require(second.Balls.Select(ball => ball.Id).Distinct().Count() == 7,
            "Every ball in a rally must have a distinct stable identity.");
        var ids = second.Balls.Select(ball => ball.Id).ToArray();
        rig.Advance(35);
        Require(rig.Game.Snapshot.Phase == FootballPhase.Playing && rig.Game.Snapshot.Balls.Count == 7 &&
            rig.Game.Snapshot.Balls.Select(ball => ball.Id).SequenceEqual(ids),
            "A long scoreless rally added more than six extra balls or replaced existing identities.");

        var overlap = Playing();
        overlap.Advance(30.1);
        Require(overlap.Game.Snapshot.Balls.Count == 7, "The overlap fixture did not reach the second wave.");
        var early = overlap.Game.Snapshot;
        foreach (int oldIndex in new[] { 1, 2 })
        {
            int addedIndex = oldIndex + 2;
            var older = early.Balls[oldIndex];
            var newer = early.Balls[addedIndex];
            Require(Vector3.Distance(new(older.Position, older.Height), new(newer.Position, newer.Height)) >=
                FootballGame.BallRadius * (older.Growth + newer.Growth) - .0001f,
                "A growing sideline ball remained embedded in the prior wave.");
        }
        overlap.Advance(1);
        Require(overlap.Game.Snapshot.Balls.All(ball => ball.Velocity == Vector2.Zero && ball.VerticalVelocity == 0),
            "Spawn-overlap separation launched a ball without a kick.");
        RequireFinite(overlap.Game.Snapshot);
    }

    private static void CheckPausedClockAndGrowth()
    {
        var rig = Playing();
        rig.Advance(14.9);
        rig.Game.SetPlayerInput(0, null, rig.Now);
        rig.Advance(20, fresh: false);
        Require(rig.Game.Phase == FootballPhase.WaitingForPlayers && rig.Game.Snapshot.Balls.Count == 1,
            "Missing input consumed the remaining wave clock.");
        rig.Resume();
        Require(rig.Game.Snapshot.Balls.Count == 1, "The reconnect countdown consumed the wave clock.");
        rig.Advance(.11);
        Require(rig.Game.Snapshot.Balls.Count == 3, "The saved wave clock did not resume after fresh input.");
        var before = rig.Game.Snapshot;
        rig.Now = rig.Now.AddMilliseconds(300);
        rig.Game.Advance(rig.Now);
        var recovering = rig.Game.Snapshot;
        Require(recovering.RecoveringInput && SameGeometry(before, recovering),
            "A short input loss did not freeze every ball's position, rotation, and growth.");
        rig.Now = rig.Now.AddMilliseconds(200);
        rig.Feed();
        rig.Game.Advance(rig.Now);
        Require(!rig.Game.Snapshot.RecoveringInput && SameGeometry(before, rig.Game.Snapshot),
            "Short recovery banked ball growth or simulation time.");
        rig.Advance(.1);
        Require(rig.Game.Snapshot.Balls[1].Growth > before.Balls[1].Growth,
            "Growth did not resume after short tracking recovery.");
        before = rig.Game.Snapshot;
        rig.Game.SetPlayerInput(0, null, rig.Now);
        rig.Advance(10, fresh: false);
        Require(SameGeometry(before, rig.Game.Snapshot), "A long input pause advanced ball growth.");
        rig.Resume();
        Require(SameGeometry(before, rig.Game.Snapshot), "Reconnect countdown advanced ball growth.");
        rig.Advance(.5);
        Require(rig.Game.Snapshot.Balls.All(ball => ball.Growth == 1), "Growth remained stuck after reconnect.");

        var hitch = Playing();
        hitch.Advance(14.9);
        for (int step = 0; step < 3; step++)
        {
            hitch.Now = hitch.Now.AddMilliseconds(100);
            hitch.Feed();
        }
        hitch.Game.Advance(hitch.Now);
        Require(hitch.Game.Phase == FootballPhase.Playing && hitch.Game.Snapshot.Balls.Count == 1,
            "A render hitch banked time toward a new wave despite fresh camera input.");
        hitch.Advance(.11);
        Require(hitch.Game.Snapshot.Balls.Count == 3, "The wave clock did not resume after a discarded render hitch.");

        var ready = new FootballGame();
        var idle = new Rig(ready, Epoch);
        idle.Advance(40);
        Require(ready.Phase == FootballPhase.Ready && ready.Snapshot.Balls.Count == 1,
            "Idle preview added balls before the match started.");
    }

    private static void CheckGoalAndMatchReset()
    {
        foreach (bool simultaneous in new[] { false, true })
        {
            var rig = Playing();
            rig.Advance(30.5);
            // A secondary ball must score even when the primary ball remains safely on the pitch.
            int scorer = simultaneous ? 0 : 6;
            SeedBall(rig.Game, scorer, new(FootballGame.Width / 2 - .01f, 0), new(2, 0));
            if (simultaneous) SeedBall(rig.Game, 1, new(-FootballGame.Width / 2 + .01f, 0), new(-2, 0));
            rig.Advance(.05);
            var goal = rig.Game.Snapshot;
            Require(goal.Phase == FootballPhase.Goal && goal.Score1 + goal.Score2 == 1 && goal.Balls.Count == 1,
                "A goal must score exactly once and remove the other balls, including simultaneous opposing goals.");
            int score1 = goal.Score1, score2 = goal.Score2;
            for (int step = 0; step < 1000 && rig.Game.Phase != FootballPhase.Playing; step++) rig.Advance(.005);
            Require(rig.Game.Phase == FootballPhase.Playing && rig.Game.Snapshot.Balls.Count == 1 &&
                rig.Game.Snapshot.Score1 == score1 && rig.Game.Snapshot.Score2 == score2,
                "Goal celebration/countdown did not restart one ball without repeated scoring.");
            SeedBall(rig.Game, 0, Vector2.Zero, Vector2.Zero);
            rig.Advance(14.9);
            Require(rig.Game.Snapshot.Balls.Count == 1, "The new rally inherited a previous wave timer.");
            rig.Advance(.11);
            Require(rig.Game.Snapshot.Balls.Count == 3, "The new rally did not schedule its own first wave.");
            rig.Game.Reset(rig.Now);
            Require(rig.Game.Phase == FootballPhase.Ready && rig.Game.Snapshot.Balls.Count == 1 &&
                rig.Game.Snapshot.Balls[0].Growth == 1 && rig.Game.Snapshot.Score1 == 0 && rig.Game.Snapshot.Score2 == 0,
                "Reset retained extra balls, growth, or scores.");
        }
    }

    private static void CheckBallContacts()
    {
        var rig = Playing();
        rig.Advance(15.5);
        SeedBall(rig.Game, 0, new(-.06f, 0), new(2, 0));
        SeedBall(rig.Game, 1, new(.06f, 0), new(-2, 0));
        rig.Advance(.03);
        Require(rig.Game.Snapshot.Balls[0].Velocity.X < -1 && rig.Game.Snapshot.Balls[1].Velocity.X > 1,
            "Opposing full-size balls passed through each other or failed to exchange an impact.");

        SeedBall(rig.Game, 0, new(-.06f, 0), new(2, 0), .25f);
        SeedBall(rig.Game, 1, new(.06f, 0), new(-2, 0));
        rig.Advance(.06);
        Require(rig.Game.Snapshot.Balls[0].Position.X > 0 && rig.Game.Snapshot.Balls[0].Velocity.X > 1 &&
            rig.Game.Snapshot.Balls[1].Position.X < 0 && rig.Game.Snapshot.Balls[1].Velocity.X < -1,
            "A ball well above another ball collided with its ground projection.");

        SeedBall(rig.Game, 0, Vector2.Zero, Vector2.Zero);
        SeedBall(rig.Game, 1, Vector2.Zero, Vector2.Zero);
        rig.Advance(.01);
        Require(Vector2.Distance(rig.Game.Snapshot.Balls[0].Position, rig.Game.Snapshot.Balls[1].Position) >=
            FootballGame.BallRadius * 2 - .0001f, "Coincident balls were not separated safely.");

        var busy = Playing();
        busy.Advance(30.5);
        for (int i = 0; i < 7; i++)
        {
            float angle = i * MathF.Tau / 7;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            SeedBall(busy.Game, i, direction * .14f, -direction * FootballGame.MaxBallSpeed);
        }
        for (int step = 0; step < 200; step++)
        {
            busy.Advance(.005);
            RequireFinite(busy.Game.Snapshot);
        }
    }

    private static void CheckAiSecondaryBall()
    {
        var rig = Playing();
        rig.Advance(15.5);
        Set(rig.Game, "_mode", FootballMode.HumanVsAi);
        SeedBall(rig.Game, 0, new(-.5f, -.3f), Vector2.Zero);
        SeedBall(rig.Game, 1, new(.3f, 0), new(1, 0));
        SeedBall(rig.Game, 2, new(.3f, .3f), Vector2.Zero);
        Set(rig.Game, "_aiThinkRemaining", 0f);
        rig.Advance(.01);
        Require((int)Get(rig.Game, "_aiBallId")! == rig.Game.Snapshot.Balls[1].Id &&
            rig.Game.Snapshot.Kickers[1].Velocity.Length() <= FootballGame.AiMaxSpeed + .0001f,
            "The AI ignored a secondary ball heading toward its own goal or exceeded its physical speed limit.");
        object threat = ((IList)Get(rig.Game, "_balls")!)[1]!;
        Set(threat, "Growth", .5f);
        Set(rig.Game, "_aiThinkRemaining", 0f);
        rig.Advance(.01);
        Require((int)Get(rig.Game, "_aiBallId")! != rig.Game.Snapshot.Balls[1].Id,
            "The AI tried to play a secondary ball before it finished growing.");
    }

    private static bool SameGeometry(FootballSnapshot a, FootballSnapshot b) => a.Balls.Count == b.Balls.Count &&
        a.Balls.Zip(b.Balls).All(pair => pair.First.Id == pair.Second.Id && pair.First.Position == pair.Second.Position &&
            pair.First.Height == pair.Second.Height && pair.First.Rotation == pair.Second.Rotation &&
            pair.First.Growth == pair.Second.Growth);

    private static void RequireFinite(FootballSnapshot snapshot)
    {
        Require(snapshot.Balls.Count is >= 1 and <= 7 && snapshot.Balls.All(ball =>
            float.IsFinite(ball.Position.X) && float.IsFinite(ball.Position.Y) && float.IsFinite(ball.Height) &&
            float.IsFinite(ball.VerticalVelocity) && ball.Velocity.Length() <= FootballGame.MaxBallSpeed + .0001f &&
            MathF.Abs(ball.Rotation.Length() - 1) < .0001f && ball.Growth is >= 0 and <= 1),
            "Multiball contact produced a nonfinite, over-speed, invalid-size, or invalid-rotation state.");
    }

    private static Rig Playing()
    {
        var game = new FootballGame();
        game.SetMode(FootballMode.TwoHumans, Epoch);
        game.Start(Epoch);
        var rig = new Rig(game, Epoch);
        rig.Resume();
        // Isolate a scoreless rally without changing production mutation APIs.
        SeedBall(game, 0, Vector2.Zero, Vector2.Zero);
        Set(game, "_rallySeconds", 0d);
        Set(game, "_accumulator", 0d);
        rig.ActiveSeconds = 0;
        return rig;
    }

    private static void SeedBall(FootballGame game, int index, Vector2 position, Vector2 velocity,
        float height = FootballGame.BallRadius)
    {
        var balls = (IList)Get(game, "_balls")!;
        object ball = balls[index]!;
        Set(ball, "Position", position);
        Set(ball, "Velocity", velocity);
        Set(ball, "Height", height);
        Set(ball, "VerticalVelocity", 0f);
        Set(ball, "Rotation", Quaternion.Identity);
        Set(ball, "Spin", 0f);
        Set(game, "_snapshot", null);
    }

    private static object? Get(object value, string name) => value.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(value);
    private static void Set(object value, string name, object? item) => value.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(value, item);

    private sealed class Rig(FootballGame game, DateTimeOffset now)
    {
        public FootballGame Game { get; } = game;
        public DateTimeOffset Now = now;
        public double ActiveSeconds;
        public void Feed()
        {
            Game.SetPlayerInput(0, new(-.6f, -.4f), Now);
            Game.SetPlayerInput(1, new(.6f, .4f), Now);
        }
        public void Advance(double seconds, bool fresh = true)
        {
            long remaining = (long)Math.Round(seconds * TimeSpan.TicksPerSecond);
            while (remaining > 0)
            {
                long ticks = Math.Min(remaining, TimeSpan.TicksPerSecond / 10);
                Now = Now.AddTicks(ticks);
                if (fresh) Feed();
                Game.Advance(Now);
                remaining -= ticks;
            }
            ActiveSeconds += seconds;
        }
        public void AdvanceTo(double seconds) => Advance(seconds - ActiveSeconds);
        public void Resume()
        {
            for (int step = 0; step < 1000 && Game.Phase != FootballPhase.Playing; step++) Advance(.005);
            Require(Game.Phase == FootballPhase.Playing, "The fixture did not enter active play.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
