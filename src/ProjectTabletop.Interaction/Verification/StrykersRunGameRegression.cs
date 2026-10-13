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
        CheckRoadPresentation();
        CheckGateChoicesAndHero();
        CheckWideFormationContacts();
        CheckSweptBullets();
        CheckObjectivesAndDefender();
        CheckDefenderCombatAndLoss();
        CheckNaturalReinforcements();
        CheckRushAndObjectiveBounds();
        CheckBounds();
        CheckGameOverAndReset();
        Console.WriteLine("Stryker's Run simulation passed: lone persistent hero, recruits and gate arithmetic, " +
            "shootable rescues and supplies, mortal defending ally, runner rushes, fresh horizontal control, complete input pauses, " +
            "smooth road presentation, hitch discard, swept nearest contacts, bounded entities and game-over reset.");
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
        for (int ms = 10; ms <= 8000; ms += 10) { frequent.SetInput(.5f, At(ms)); frequent.Advance(At(ms)); }
        for (int ms = 33; ms < 8000; ms += 33) { sparse.SetInput(.5f, At(ms)); sparse.Advance(At(ms)); }
        sparse.SetInput(.5f, At(8000)); sparse.Advance(At(8000));
        Require(SameRun(frequent.Snapshot, sparse.Snapshot),
            "Different presentation frame rates changed fixed-step movement, firing, contacts or random waves.");
    }

    private static void CheckRoadPresentation()
    {
        var game = Playing(quiet: true);
        var initial = game.Snapshot;
        float start = game.RoadPresentationDistance;
        game.Advance(Epoch.AddMilliseconds(500.5));
        Require(Near(game.RoadPresentationDistance, start + .004f) && SameRun(initial, game.Snapshot) &&
            initial.Revision == game.Snapshot.Revision,
            "The road must move between fixed ticks without advancing simulation state.");
        foreach (double milliseconds in new[] { 501.75, 506.25, 517, 533.75, 549, 550 })
        {
            game.Advance(Epoch.AddMilliseconds(milliseconds));
            Require(Near(game.RoadPresentationDistance, start + (float)(milliseconds - 500) * .008f),
                "Jittered presentation intervals changed the road's eight-unit-per-second speed.");
        }
        var coarse = Playing(quiet: true);
        coarse.Advance(At(550));
        Require(SameRun(game.Snapshot, coarse.Snapshot) && Near(game.RoadPresentationDistance, coarse.RoadPresentationDistance),
            "Fractional road presentation changed fixed-step gameplay or accumulated distance.");

        foreach (string reason in new[] { "clear", "null", "stale" })
        {
            var paused = Playing(quiet: true);
            paused.SetInput(.5f, At(506)); // Refresh the quiet fixture's snapshot before freezing it.
            paused.Advance(At(507)); // Leave a visible fractional step ahead of the simulation.
            if (reason == "stale") paused.Advance(At(650));
            float frozenRoad = paused.RoadPresentationDistance;
            var frozenRun = paused.Snapshot;
            if (reason == "clear") paused.ClearInput(At(508));
            if (reason == "null") paused.SetInput(null, At(508));
            Require(paused.RoadPresentationDistance == frozenRoad, "Removing input moved the presentation road backwards.");
            paused.Advance(At(757));
            Require(paused.Phase == StrykersRunPhase.WaitingForInput && paused.RoadPresentationDistance == frozenRoad &&
                SameRun(frozenRun, paused.Snapshot), "Input loss (" + reason + ") advanced the road or paused simulation.");
            paused.SetInput(.5f, At(1200)); paused.Advance(At(1200));
            Require(paused.RoadPresentationDistance == frozenRoad && SameRun(frozenRun, paused.Snapshot),
                "Resuming input moved the road backwards or replayed paused time.");
            paused.Advance(At(1210));
            Require(paused.RoadPresentationDistance >= frozenRoad && paused.RoadPresentationDistance <= frozenRoad + .0801f &&
                paused.Snapshot.Distance > frozenRun.Distance, "Resumed scrolling replayed time debt or failed to continue.");
            float beforeHitch = paused.RoadPresentationDistance;
            var beforeHitchRun = paused.Snapshot;
            paused.SetInput(.5f, At(1600)); paused.Advance(At(1600));
            Require(paused.RoadPresentationDistance == beforeHitch && SameRun(beforeHitchRun, paused.Snapshot),
                "Discarding a render hitch rewound the road or replayed missed time.");
            paused.Advance(At(1603));
            Require(paused.RoadPresentationDistance >= beforeHitch && paused.RoadPresentationDistance <= beforeHitch + .0241f,
                "The next fractional frame after a discarded hitch jumped or reversed the road.");
            paused.Reset(At(1700));
            Require(paused.RoadPresentationDistance == 0 && paused.Snapshot.Distance == 0,
                "Reset retained the road's presentation offset.");
        }

        var boss = Playing(quiet: true);
        Put(boss, "_levelTicks", 60 * 120 - 1);
        Put(boss, "_distance", (60 * 120 - 1) * 8 / 120f);
        boss.Advance(At(507));
        Require(boss.RoadPresentationDistance < 480, "Fractional scrolling crossed the end of the level early.");
        Tick(boss, 510);
        Require(boss.Snapshot.Stage == StrykersRunStage.BossEntrance && Near(boss.RoadPresentationDistance, 480),
            "The road did not settle at the boss boundary.");
        for (int ms = 520; ms <= 1000; ms += 10)
        {
            Tick(boss, ms);
            Require(Near(boss.RoadPresentationDistance, 480) && Near(boss.Snapshot.Distance, 480),
                "The presentation road kept scrolling during the boss fight.");
        }
        boss.Start(At(1010));
        Require(boss.RoadPresentationDistance == 0, "PLAY retained the previous run's presentation distance.");
    }

    private static void CheckObjectivesAndDefender()
    {
        var game = Playing(quiet: true);
        Objective(game, 200, .5f, .52f, StrykersRunObjectiveKind.Rescue, 2);
        Add(game, "_bullets", "Bullet", 201, .5f, .585f);
        Tick(game, 510);
        var sealedPod = game.Snapshot;
        Require(game.Snapshot.Ally is null && game.Snapshot.Objectives is [{ Health: 1 }],
            "The rescue must count actual lock hits and stay sealed until its final hit.");
        Add(game, "_bullets", "Bullet", 202, .5f, .585f);
        Tick(game, 520);
        Require(game.Snapshot.Ally is { Health: StrykersRunGame.DefenderMaximumHealth } &&
            game.Snapshot.SoldierCount == 1 && game.Snapshot.Objectives.Count == 0 &&
            sealedPod.Objectives is [{ Health: 1 }] && game.Snapshot.Effects.Any(effect => effect.Kind == StrykersRunEffectKind.AllyUnlocked),
            "Breaking the last lock must free a full-health defender without replacing the hero or mutating a prior snapshot.");
        float allyX = game.Snapshot.Ally!.Value.X;
        Add(game, "_bullets", "Bullet", 203, allyX, game.Snapshot.Ally.Value.Y + .04f);
        Put(game, "_soldiers", 3);
        Gate(game, 1, .5f, StrykersRunGateKind.Subtract, 2);
        Tick(game, 530);
        Require(game.Snapshot.SoldierCount == 1 && game.Snapshot.Ally is { Health: StrykersRunGame.DefenderMaximumHealth },
            "Friendly bullets and troop gates must not damage the ally or count him as a soldier.");
        Objective(game, 204, .5f, .52f, StrykersRunObjectiveKind.Reinforcements, 1);
        Add(game, "_bullets", "Bullet", 205, .5f, .585f);
        Tick(game, 540);
        Require(game.Snapshot.SoldierCount == 7 && game.Snapshot.Objectives.Count == 0 &&
            game.Snapshot.Effects.Any(effect => effect.Kind == StrykersRunEffectKind.SupplyOpened),
            "A destroyed supply must add exactly six recruits while leaving the defender separate.");
        Put(game, "_soldiers", 62);
        Objective(game, 206, .5f, .52f, StrykersRunObjectiveKind.Reinforcements, 1);
        Add(game, "_bullets", "Bullet", 207, .5f, .585f);
        Tick(game, 550);
        Require(game.Snapshot.SoldierCount == StrykersRunGame.MaximumSoldiers,
            "A supply reward must respect the existing bounded formation.");

        game = Playing(quiet: true);
        Objective(game, 300, .5f, .40f, StrykersRunObjectiveKind.Rescue, 1);
        Enemy(game, 301, .5f, .51f, StrykersRunEnemyKind.Runner, 1);
        Add(game, "_bullets", "Bullet", 302, .5f, .538f);
        Tick(game, 510);
        Require(game.Snapshot.Score == 10 && game.Snapshot.Ally is null && game.Snapshot.Objectives is [{ Health: 1 }],
            "A nearer enemy must absorb a swept bullet before the lock behind it.");
        game = Playing(quiet: true);
        Objective(game, 303, .5f, .50f, StrykersRunObjectiveKind.Reinforcements, 1);
        Enemy(game, 304, .5f, .59f, StrykersRunEnemyKind.Runner, 1);
        Add(game, "_bullets", "Bullet", 305, .5f, .63f);
        Tick(game, 510);
        Require(game.Snapshot.SoldierCount == 7 && game.Snapshot.Score == 0 && game.Snapshot.Enemies is [{ Health: 1 }],
            "A nearer supply must absorb a bullet before an enemy, without double-hitting either target.");

        game = Playing(quiet: true);
        Objective(game, 306, .5f, .52f, StrykersRunObjectiveKind.Reinforcements, 2);
        Add(game, "_bullets", "Bullet", 307, .60f, .585f);
        Add(game, "_bullets", "Bullet", 308, .64f, .585f);
        Tick(game, 510);
        Require(game.Snapshot.Objectives is [{ Health: 1 }] && game.Snapshot.Bullets is [{ Id: 308 }],
            "Shots crossing a container's visible side must damage its lock, while shots beyond its shell must miss.");

        game = Playing(quiet: true);
        var entering = Objective(game, 309, .5f, .18f, StrykersRunObjectiveKind.Rescue, 1);
        Add(game, "_bullets", "Bullet", 310, .5f, .24f);
        Tick(game, 510);
        Require(game.Snapshot.Ally is null && game.Snapshot.Objectives is [{ Health: 1 }] && game.Snapshot.Bullets.Count == 1,
            "An objective still entering from behind the HUD must not absorb shots or unlock out of sight.");
        entering.GetType().GetField("Y")!.SetValue(entering, StrykersRunGame.ObjectiveShootableY - .0001f);
        Tick(game, 520);
        Require(game.Snapshot.Ally is not null && game.Snapshot.Objectives.Count == 0 && game.Snapshot.Bullets.Count == 0,
            "An objective must become shootable once its numbered lock is fully visible below the HUD.");

        game = Playing(quiet: true);
        Objective(game, 400, .5f, StrykersRunGame.SquadY + .061f, StrykersRunObjectiveKind.Reinforcements, 1);
        Tick(game, 510);
        Require(game.Snapshot.Objectives.Count == 0 && game.Snapshot.SoldierCount == 1,
            "Passing through or missing a supply must not grant its shooting reward.");
    }

    private static void CheckDefenderCombatAndLoss()
    {
        var game = Playing(quiet: true);
        Rescue(game);
        var ally = game.Snapshot.Ally!.Value;
        Enemy(game, 410, ally.X, ally.Y - .08f, StrykersRunEnemyKind.Brute, 5);
        Tick(game, 530);
        Require(game.Snapshot.Ally is { AttackSeconds: < .02f } && game.Snapshot.Enemies is [{ Health: 1 }] &&
            game.Snapshot.Effects.Any(effect => effect.Kind == StrykersRunEffectKind.AllyAttack),
            "The defender must strike nearby threats automatically with a visible attack pulse.");
        Tick(game, 540);
        Require(game.Snapshot.Enemies is [{ Health: 1 }], "A melee pulse must respect its cooldown.");
        List(game, "_enemies").Clear();
        Enemy(game, 411, ally.X, ally.Y - .0001f, StrykersRunEnemyKind.Boss, 100);
        Tick(game, 550);
        Require(game.Snapshot.Ally is { Health: 6 } && game.Snapshot.SoldierCount == 1 && game.Snapshot.Enemies.Count == 0,
            "Intercepting a boss must consume defender HP before it reaches the squad.");
        Enemy(game, 412, .82f, ally.Y - .0001f, StrykersRunEnemyKind.Runner, 100);
        Tick(game, 560);
        Require(game.Snapshot.Ally is { Health: 6 } && game.Snapshot.Enemies.Count == 1,
            "The defender must protect only his visible footprint, not the entire road.");
        List(game, "_enemies").Clear();
        Enemy(game, 413, ally.X, ally.Y - .0001f, StrykersRunEnemyKind.Boss, 100);
        Tick(game, 570);
        Require(game.Snapshot.Ally is null && game.Snapshot is { HeroAlive: true, SoldierCount: 1, Phase: StrykersRunPhase.Playing } &&
            game.Snapshot.Effects.Any(effect => effect.Kind == StrykersRunEffectKind.AllyLost),
            "The defender must disappear at zero HP without ending a living hero's run.");
        Enemy(game, 414, .5f, StrykersRunGame.SquadY - .0001f, StrykersRunEnemyKind.Runner, 100);
        Tick(game, 580);
        Require(game.Snapshot is { HeroAlive: false, Phase: StrykersRunPhase.GameOver },
            "A dead defender must not keep protecting the hero, whose death still ends the run.");

        game = Playing(quiet: true);
        Rescue(game);
        Objective(game, 420, .8f, .3f, StrykersRunObjectiveKind.Reinforcements, 12);
        Tick(game, 530);
        game.ClearInput(At(531));
        var frozen = game.Snapshot;
        for (int ms = 540; ms <= 1500; ms += 10) game.Advance(At(ms));
        Require(SameRun(frozen, game.Snapshot), "Missing input must freeze ally animation, attack timers and moving objective HP/positions.");
        game.SetInput(.1f, At(1510)); game.Advance(At(1510));
        Require(SameRun(frozen, game.Snapshot), "Recovering input must not replay ally or objective time debt.");
        for (int ms = 1520; ms <= 1800; ms += 10) { game.SetInput(0, At(ms)); game.Advance(At(ms)); }
        var edge = game.Snapshot.Ally!.Value;
        Require(edge.X == StrykersRunFormation.DefenderPosition(StrykersRunGame.RoadLeft).X && edge.Y < StrykersRunGame.SquadY &&
            edge.X - StrykersRunFormation.DefenderHalfWidth >= StrykersRunGame.RoadLeft,
            "The ally must follow ahead of the hero while staying inside the unchanged road bounds.");
        game.Start(At(1810));
        Require(game.Snapshot.Ally is null && game.Snapshot.Objectives.Count == 0 && game.Snapshot.SoldierCount == 1,
            "PLAY must reset the ally and every unfinished lock instead of carrying an unlock between runs.");
    }

    private static void CheckNaturalReinforcements()
    {
        var game = new StrykersRunGame();
        game.Start(At(0));
        float unlockAt = -1;
        bool supplyOpened = false;
        for (int ms = 10; ms <= 26000; ms += 10)
        {
            var before = game.Snapshot;
            float target = .5f;
            var objective = before.Objectives.OrderByDescending(item => item.Y).FirstOrDefault();
            if (objective.Id != 0) target = objective.X;
            var goodGate = before.Gates.FirstOrDefault(gate => gate.Y > .66f && gate.Kind != StrykersRunGateKind.Subtract);
            if (goodGate.Id != 0) target = goodGate.X;
            game.SetInput((target - StrykersRunGame.RoadLeft) / (StrykersRunGame.RoadRight - StrykersRunGame.RoadLeft), At(ms));
            game.Advance(At(ms));
            var after = game.Snapshot;
            if (unlockAt < 0 && after.Ally is not null)
            {
                unlockAt = after.RunSeconds;
                Require(before.SoldierCount == 1 && after.SoldierCount == 1 && unlockAt <= 15,
                    "The first naturally spawned rescue must be shootable by the lone initial hero within 15 active seconds.");
            }
            if (before.Objectives.Any(item => item.Kind == StrykersRunObjectiveKind.Reinforcements) &&
                after.Effects.Any(effect => effect.Kind == StrykersRunEffectKind.SupplyOpened && effect.Age <= 1f / 120))
            {
                Require(after.SoldierCount == Math.Min(StrykersRunGame.MaximumSoldiers, before.SoldierCount + 6),
                    "A naturally spawned shot-to-open supply did not deliver exactly six recruits.");
                supplyOpened = true;
                break;
            }
            Require(after.Phase != StrykersRunPhase.GameOver, "The natural first encounters were not attainable with visible lane choices.");
        }
        Require(unlockAt >= 2 && supplyOpened,
            "The public gameplay path must spawn and unlock a first rescue, then offer a reachable supply cache.");
        Console.WriteLine($"Natural encounters passed: lone-hero rescue at {unlockAt:F2}s; six-recruit supply at {game.Snapshot.RunSeconds:F2}s.");
    }

    private static void CheckRushAndObjectiveBounds()
    {
        var game = Playing(quiet: true);
        Put(game, "_rushRemaining", 0f);
        Tick(game, 510);
        Require(game.Snapshot.Enemies.Count == 4 && game.Snapshot.Enemies.All(enemy => enemy.Kind == StrykersRunEnemyKind.Runner) &&
            game.Snapshot.Enemies.Max(enemy => enemy.X) - game.Snapshot.Enemies.Min(enemy => enemy.X) < .2f,
            "The first rush must introduce a compact readable band of four runners.");
        for (int i = 4; i < StrykersRunGame.MaximumEnemies; i++) Enemy(game, 500 + i, .1f, -2, StrykersRunEnemyKind.Runner, 10);
        Put(game, "_rushRemaining", 0f);
        Tick(game, 520);
        Require(game.Snapshot.Enemies.Count == StrykersRunGame.MaximumEnemies,
            "A runner rush must share the existing enemy cap.");
        for (int ms = 530; ms <= 550; ms += 10)
        {
            Put(game, "_objectiveRemaining", 0f);
            Tick(game, ms);
        }
        Require(game.Snapshot.Objectives.Count == StrykersRunGame.MaximumObjectives &&
            game.Snapshot.Objectives.Count(item => item.Kind == StrykersRunObjectiveKind.Rescue) == 1,
            "Encounter scheduling must allow at most two objectives and one rescue pod.");
        List(game, "_objectives").Clear();
        Rescue(game, 560);
        Put(game, "_objectiveRemaining", 0f);
        Tick(game, 580);
        Require(game.Snapshot.Objectives is [{ Kind: StrykersRunObjectiveKind.Reinforcements }],
            "A living ally must replace later rescue offers with supplies, never create a second ally.");
        Put(game, "_ally", null!);
        Put(game, "_objectiveRemaining", 0f);
        Tick(game, 590);
        Require(game.Snapshot.Objectives.Any(item => item.Kind == StrykersRunObjectiveKind.Rescue),
            "Losing a defender must allow a subsequent rescue encounter.");
    }

    private static void Rescue(StrykersRunGame game, int ms = 510)
    {
        Objective(game, 4000 + ms, .5f, .52f, StrykersRunObjectiveKind.Rescue, 1);
        Add(game, "_bullets", "Bullet", 5000 + ms, .5f, .585f);
        Tick(game, ms);
        Require(game.Snapshot.Ally is not null, "The rescue fixture did not unlock its defender.");
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

    private static void CheckBounds()
    {
        var game = Playing(quiet: true);
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
            Put(game, "_objectiveRemaining", 100f); Put(game, "_rushRemaining", 100f);
        }
        return game;
    }

    private static void Tick(StrykersRunGame game, int ms) { game.SetInput(Math.Clamp((Get<float>(game, "_targetX") - .08f) / .84f, 0, 1), At(ms)); game.Advance(At(ms)); }
    private static void Enemy(StrykersRunGame game, int id, float x, float y, StrykersRunEnemyKind kind, int health) => Add(game, "_enemies", "Enemy", id, x, y, kind, health);
    private static object Objective(StrykersRunGame game, int id, float x, float y, StrykersRunObjectiveKind kind, int health) =>
        Add(game, "_objectives", "Objective", id, x, y, kind, health);
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
        a.Enemies.SequenceEqual(b.Enemies) && a.Gates.SequenceEqual(b.Gates) && a.Bullets.SequenceEqual(b.Bullets) && a.Effects.SequenceEqual(b.Effects) &&
        a.Objectives.SequenceEqual(b.Objectives) && a.Ally == b.Ally && a.Level == b.Level &&
        a.LevelSeconds == b.LevelSeconds && a.Stage == b.Stage && a.StageSeconds == b.StageSeconds &&
        a.Boss == b.Boss && a.Hazards.SequenceEqual(b.Hazards);
    private static bool Near(float a, float b) => Math.Abs(a - b) < .0001f;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
