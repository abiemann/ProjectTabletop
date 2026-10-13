using System.Collections;
using System.Numerics;
using System.Reflection;
using ProjectTabletop.Interaction;

internal static class StrykersRunGameRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int milliseconds) => Epoch.AddMilliseconds(milliseconds);

    public static void Run()
    {
        CheckStartAndInput();
        CheckInputPausesAndClock();
        CheckFrameRateIndependence();
        CheckGateChoicesAndHero();
        CheckWideFormationContacts();
        CheckSweptBullets();
        CheckBoundsAndBosses();
        CheckGameOverAndReset();
        Console.WriteLine("Stryker's Run simulation passed: lone persistent hero, recruits and gate arithmetic, " +
            "fresh horizontal control, complete input pauses, hitch discard, swept nearest contacts, bounded entities, bosses and game-over reset.");
    }

    private static void CheckStartAndInput()
    {
        var game = new StrykersRunGame();
        Require(game.Snapshot is { Phase: StrykersRunPhase.Ready, SoldierCount: 1, TroopCount: 0, HeroAlive: true },
            "A level must start with the main protagonist alone.");
        game.SetInput(1, At(0));
        game.Advance(At(100));
        Require(game.Snapshot is { Phase: StrykersRunPhase.Ready, SquadX: .5f, RunSeconds: 0 } && game.Snapshot.Bullets.Count == 0,
            "Rail observations must not start or fire before PLAY.");
        game.Start(At(100));
        for (int ms = 110; ms <= 560; ms += 10) game.Advance(At(ms));
        Require(game.Snapshot is { Phase: StrykersRunPhase.WaitingForInput, RunSeconds: 0, RecoveringInput: false },
            "The control withdrawal must finish without input, then wait without advancing the run.");
        game.SetInput(1, At(570));
        game.Advance(At(570));
        Require(game.Snapshot is { Phase: StrykersRunPhase.Playing, SquadX: .5f, RunSeconds: 0 },
            "Fresh input must enter play without replaying missing time or jumping the protagonist.");
        for (int ms = 580; ms <= 800; ms += 10) { game.SetInput(1, At(ms)); game.Advance(At(ms)); }
        Require(Near(game.Snapshot.SquadX, StrykersRunGame.RoadRight) && game.Snapshot.Bullets.Count > 0 &&
            game.Snapshot.Distance > 0 && game.Snapshot.SoldierCount == 1,
            "Fresh horizontal input must move the hero within the road and automatically shoot.");
        game.SetInput(float.NaN, At(810));
        Require(game.Snapshot.Phase == StrykersRunPhase.WaitingForInput,
            "Invalid rail input must immediately pause rather than invent a position.");
    }

    private static void CheckInputPausesAndClock()
    {
        var game = Playing();
        game.SetInput(.8f, At(510));
        game.Advance(At(510));
        game.ClearInput(At(511));
        var frozen = game.Snapshot;
        for (int ms = 520; ms <= 2520; ms += 10) game.Advance(At(ms));
        Require(game.Snapshot.RecoveringInput && SameRun(frozen, game.Snapshot),
            "Clearing rail input must freeze movement, bullets, enemies, effects, score and active timers immediately.");
        game.SetInput(.1f, At(510));
        game.Advance(At(2530));
        Require(game.Snapshot.Phase == StrykersRunPhase.WaitingForInput && SameRun(frozen, game.Snapshot),
            "An observation before the reset barrier revived the paused run.");
        game.SetInput(.1f, At(2540));
        game.Advance(At(2540));
        Require(game.Snapshot.Phase == StrykersRunPhase.Playing && SameRun(frozen, game.Snapshot),
            "Recovery must preserve the exact paused scene before applying new movement.");
        game.Advance(At(2550));
        var progressed = game.Snapshot;
        Require(progressed.RunSeconds > frozen.RunSeconds && progressed.SquadX < frozen.SquadX,
            "The first subsequent live step did not use the new horizontal position.");
        game.Advance(At(2549));
        game.Advance(At(2550));
        Require(ReferenceEquals(progressed, game.Snapshot), "Duplicate or older samples advanced the simulation.");
        game.SetInput(.5f, At(3000));
        game.Advance(At(3000));
        Require(SameRun(progressed, game.Snapshot), "A render hitch replayed its simulation debt.");
        game.Advance(At(3100));
        var beforeStale = game.Snapshot;
        game.Advance(At(3260));
        Require(game.Snapshot.Phase == StrykersRunPhase.WaitingForInput && SameRun(beforeStale, game.Snapshot),
            "Input older than 250 ms advanced the road or automatic firing.");
    }

    private static void CheckGateChoicesAndHero()
    {
        var game = Playing(quiet: true);
        Put(game, "_squadX", .28f); Put(game, "_targetX", .28f);
        Gate(game, 1, .28f, StrykersRunGateKind.Add, 5);
        Gate(game, 1, .72f, StrykersRunGateKind.Subtract, 50);
        Tick(game, 510);
        Require(game.Snapshot is { SoldierCount: 6, TroopCount: 5, HeroAlive: true } && game.Snapshot.Gates.Count == 0,
            "Crossing one gate must recruit around the original hero and remove both choices in that row.");
        Gate(game, 2, .28f, StrykersRunGateKind.Subtract, 5);
        Tick(game, 520);
        Require(game.Snapshot is { SoldierCount: 1, TroopCount: 0, HeroAlive: true, Phase: StrykersRunPhase.Playing },
            "A penalty must remove all recruited troops before the persistent hero dies.");
        Gate(game, 3, .28f, StrykersRunGateKind.Multiply, int.MaxValue);
        Tick(game, 530);
        Require(game.Snapshot.SoldierCount == StrykersRunGame.MaximumSoldiers,
            "Multiplication overflowed or exceeded the squad cap.");
        Gate(game, 4, .28f, StrykersRunGateKind.Add, int.MaxValue);
        Tick(game, 540);
        Require(game.Snapshot.SoldierCount == StrykersRunGame.MaximumSoldiers,
            "Addition overflowed the bounded squad.");
        Put(game, "_soldiers", 4);
        Enemy(game, 100, .28f, StrykersRunGame.SquadY - .0001f, StrykersRunEnemyKind.Brute, 10);
        Tick(game, 550);
        Require(game.Snapshot is { SoldierCount: 1, HeroAlive: true, TroopCount: 0 },
            "Monster damage must consume recruits before the hero, without replacing his identity.");
        Enemy(game, 101, .28f, StrykersRunGame.SquadY - .0001f, StrykersRunEnemyKind.Runner, 10);
        Tick(game, 560);
        Require(game.Snapshot is { SoldierCount: 0, HeroAlive: false, TroopCount: 0, Phase: StrykersRunPhase.GameOver },
            "The protagonist's death must end the run immediately.");
        game = Playing(quiet: true);
        Put(game, "_squadX", .28f); Put(game, "_targetX", .28f);
        Gate(game, 1, .28f, StrykersRunGateKind.Subtract, int.MaxValue);
        Tick(game, 510);
        Require(game.Snapshot is { SoldierCount: 0, Phase: StrykersRunPhase.GameOver },
            "A lethal subtraction must clamp at zero and end the run.");
    }

    private static void CheckFrameRateIndependence()
    {
        var frequent = new StrykersRunGame();
        var sparse = new StrykersRunGame();
        frequent.Start(At(0)); sparse.Start(At(0));
        for (int ms = 10; ms <= 2000; ms += 10) { frequent.SetInput(.5f, At(ms)); frequent.Advance(At(ms)); }
        for (int ms = 33; ms < 2000; ms += 33) { sparse.SetInput(.5f, At(ms)); sparse.Advance(At(ms)); }
        sparse.SetInput(.5f, At(2000)); sparse.Advance(At(2000));
        Require(SameRun(frequent.Snapshot, sparse.Snapshot),
            "Different presentation frame rates changed fixed-step movement, firing, contacts or random waves.");
    }

    private static void CheckSweptBullets()
    {
        // This segment crosses a target even though both sampled endpoints miss.
        object?[] sweep = [new Vector2(0, .1f), new Vector2(0, -.2f), .02f, 0f];
        bool contact = (bool)typeof(StrykersRunGame).GetMethod("Sweep", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, sweep)!;
        Require(contact && Near((float)sweep[3]!, .4f), "A fast bullet tunneled through an enemy between samples.");
        var game = Playing(quiet: true);
        Enemy(game, 101, .5f, .50f, StrykersRunEnemyKind.Runner, 1);
        Enemy(game, 102, .5f, .51f, StrykersRunEnemyKind.Runner, 1);
        Add(game, "_bullets", "Bullet", 103, .5f, .54f);
        Tick(game, 510);
        Require(game.Snapshot.Score == 10 && game.Snapshot.Bullets.Count == 0 &&
            game.Snapshot.Enemies is [{ Id: 101, Health: 1 }],
            "A bullet must hit only its nearest swept enemy, regardless of collection order.");
    }

    private static void CheckWideFormationContacts()
    {
        foreach (float heroX in new[] { StrykersRunGame.RoadLeft, StrykersRunGame.RoadRight })
        {
            var slots = Enumerable.Range(0, 63).Select(index => StrykersRunFormation.TroopPosition(heroX, 64, index)).ToArray();
            var bounds = StrykersRunFormation.HorizontalBounds(heroX, 64);
            Require(bounds.Left <= heroX - StrykersRunFormation.BodyHalfWidth &&
                bounds.Right >= heroX + StrykersRunFormation.BodyHalfWidth &&
                slots.All(slot => slot.X - StrykersRunFormation.BodyHalfWidth >= StrykersRunGame.RoadLeft - .000001f &&
                    slot.X + StrykersRunFormation.BodyHalfWidth <= StrykersRunGame.RoadRight + .000001f &&
                    slot.X - StrykersRunFormation.BodyHalfWidth >= bounds.Left - .000001f &&
                    slot.X + StrykersRunFormation.BodyHalfWidth <= bounds.Right + .000001f &&
                    slot.Y > StrykersRunGame.SquadY && slot.Y < .90f),
                "The clamped recruit formation escaped the shared contact footprint or protected rail.");
            float flank = slots.MaxBy(slot => Math.Abs(slot.X - heroX)).X;
            Require(Math.Abs(flank - heroX) > .15f, "The edge fixture does not exercise recruits outside the old hero-centered hitbox.");
            var game = Playing(quiet: true);
            Put(game, "_squadX", heroX); Put(game, "_targetX", heroX); Put(game, "_soldiers", 64);
            Enemy(game, 101, flank, StrykersRunGame.SquadY - .0001f, StrykersRunEnemyKind.Runner, 10);
            Tick(game, 510);
            Require(game.Snapshot is { SoldierCount: 63, TroopCount: 62, HeroAlive: true },
                "A monster passed through the visible clamped flank without costing a recruit.");
            game = Playing(quiet: true);
            Put(game, "_squadX", heroX); Put(game, "_targetX", heroX); Put(game, "_soldiers", 64);
            Enemy(game, 102, heroX < .5f ? .7f : .3f, StrykersRunGame.SquadY - .0001f, StrykersRunEnemyKind.Runner, 10);
            Put(game, "_fireRemaining", 0f);
            Tick(game, 510);
            Require(game.Snapshot.SoldierCount == 64 && game.Snapshot.Bullets.Count == 12 &&
                game.Snapshot.Bullets.Any(bullet => Near(bullet.X, heroX)) &&
                game.Snapshot.Bullets.All(bullet => Near(bullet.X, heroX) || slots.Any(slot => Near(slot.X, bullet.X))),
                "An off-formation monster caused damage or a shot did not originate from the hero/recruit formation.");
        }
    }

    private static void CheckBoundsAndBosses()
    {
        var game = Playing(quiet: true);
        Put(game, "_runSeconds", 54f); Put(game, "_enemyRemaining", 0f);
        Tick(game, 510);
        Require(game.Snapshot.Wave == 4 && game.Snapshot.Enemies.Count(enemy => enemy.Kind == StrykersRunEnemyKind.Boss) == 1,
            "The fourth wave must introduce one boss.");
        Tick(game, 520);
        Require(game.Snapshot.Enemies.Count(enemy => enemy.Kind == StrykersRunEnemyKind.Boss) == 1,
            "A boss repeated on each frame of its wave.");
        game = Playing(quiet: true);
        for (int i = 0; i < StrykersRunGame.MaximumEnemies; i++) Enemy(game, 1000 + i, .1f, -2, StrykersRunEnemyKind.Runner, 10);
        for (int i = 0; i < StrykersRunGame.MaximumBullets; i++) Add(game, "_bullets", "Bullet", 2000 + i, .9f, .5f);
        for (int i = 0; i < StrykersRunGame.MaximumEffects + 20; i++)
            typeof(StrykersRunGame).GetMethod("AddEffect", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(game, [StrykersRunEffectKind.Hit, .5f, .5f]);
        Put(game, "_enemyRemaining", 0f); Put(game, "_fireRemaining", 0f); Put(game, "_soldiers", 64);
        Tick(game, 510);
        Require(game.Snapshot.Enemies.Count == StrykersRunGame.MaximumEnemies &&
            game.Snapshot.Bullets.Count == StrykersRunGame.MaximumBullets && game.Snapshot.Effects.Count <= StrykersRunGame.MaximumEffects &&
            game.Snapshot.Enemies.All(enemy => float.IsFinite(enemy.X) && float.IsFinite(enemy.Y) && enemy.AnimationPhase is >= 0 and < 1),
            "The active scene exceeded its bounds or generated invalid animation coordinates.");
        var captured = game.Snapshot;
        Tick(game, 520);
        Require(captured.Enemies[0].Y != game.Snapshot.Enemies[0].Y && captured.Bullets[0].Y != game.Snapshot.Bullets[0].Y,
            "A snapshot was mutated by a later simulation step.");
    }

    private static void CheckGameOverAndReset()
    {
        var game = Playing(quiet: true);
        Enemy(game, 100, .5f, StrykersRunGame.SquadY - .0001f, StrykersRunEnemyKind.Boss, 100);
        Tick(game, 510);
        var ended = game.Snapshot;
        Require(ended.Phase == StrykersRunPhase.GameOver, "The game-over fixture did not kill its lone hero.");
        for (int ms = 520; ms <= 3910; ms += 10) game.Advance(At(ms));
        Require(game.Snapshot.GameOverSeconds == StrykersRunGame.GameOverPresentationSeconds && SameRun(ended, game.Snapshot),
            "The falling wall must complete without input while the run stays frozen.");
        var settled = game.Snapshot;
        game.Advance(At(3920));
        Require(ReferenceEquals(settled, game.Snapshot), "The settled game-over presentation kept producing new state.");
        game.Start(At(4000));
        Require(game.Snapshot is { Phase: StrykersRunPhase.Starting, HeroAlive: true, TroopCount: 0, Score: 0, RunSeconds: 0 } &&
            game.Snapshot.Enemies.Count == 0 && game.Snapshot.Bullets.Count == 0 && game.Snapshot.Effects.Count == 0,
            "PLAY did not start a clean level with the original lone protagonist.");
    }

    internal static StrykersRunGame Playing(bool quiet = false)
    {
        var game = new StrykersRunGame();
        game.Start(At(0));
        for (int ms = 10; ms <= 500; ms += 10) { game.SetInput(.5f, At(ms)); game.Advance(At(ms)); }
        Require(game.Snapshot.Phase == StrykersRunPhase.Playing, "The fixture never entered play.");
        if (quiet)
        {
            List(game, "_bullets").Clear(); List(game, "_effects").Clear();
            Put(game, "_fireRemaining", 100f); Put(game, "_enemyRemaining", 100f); Put(game, "_gateRemaining", 100f);
        }
        return game;
    }

    private static void Tick(StrykersRunGame game, int ms) { game.SetInput(Math.Clamp((Get<float>(game, "_targetX") - .08f) / .84f, 0, 1), At(ms)); game.Advance(At(ms)); }
    private static void Enemy(StrykersRunGame game, int id, float x, float y, StrykersRunEnemyKind kind, int health) => Add(game, "_enemies", "Enemy", id, x, y, kind, health);
    private static void Gate(StrykersRunGame game, int row, float x, StrykersRunGateKind kind, int value)
    {
        var gate = Add(game, "_gates", "Gate", row * 10 + List(game, "_gates").Count, row, x, kind, value);
        gate.GetType().GetField("Y")!.SetValue(gate, StrykersRunGame.SquadY - .0005f);
    }
    private static object Add(StrykersRunGame game, string field, string type, params object[] values)
    {
        var nested = typeof(StrykersRunGame).GetNestedType(type, BindingFlags.NonPublic)!;
        var item = Activator.CreateInstance(nested, values)!;
        List(game, field).Add(item);
        return item;
    }
    private static IList List(StrykersRunGame game, string field) => Get<IList>(game, field);
    private static T Get<T>(StrykersRunGame game, string field) => (T)typeof(StrykersRunGame).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
    private static void Put(StrykersRunGame game, string field, object value) => typeof(StrykersRunGame).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, value);
    private static bool SameRun(StrykersRunSnapshot a, StrykersRunSnapshot b) => a.SquadX == b.SquadX && a.RunSeconds == b.RunSeconds &&
        a.Distance == b.Distance && a.SoldierCount == b.SoldierCount && a.Score == b.Score && a.Wave == b.Wave &&
        a.Enemies.SequenceEqual(b.Enemies) && a.Gates.SequenceEqual(b.Gates) && a.Bullets.SequenceEqual(b.Bullets) && a.Effects.SequenceEqual(b.Effects);
    private static bool Near(float a, float b) => Math.Abs(a - b) < .0001f;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
