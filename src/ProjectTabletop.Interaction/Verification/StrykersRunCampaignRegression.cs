using System.Collections;
using System.Reflection;
using ProjectTabletop.Interaction;

internal static partial class StrykersRunCampaignRegression
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    public static void Run()
    {
        CheckMinuteBoundary();
        CheckBossEntrance();
        CheckBarrelThrowPresentation();
        CheckBarrelFuse();
        CheckCommanderWaves();
        CheckStormEncounter();
        CheckSkinWalker();
        CheckColossusAndNearestHit();
        CheckSurvivorsAndVictory();
        CheckPausedCampaign();
        CheckBossBalanceAndBounds();
        CheckPlayableBossFights();
        Console.WriteLine("Stryker's five-level campaign passed: exact active minutes, pausable three-second boss entrances, stationary arenas, " +
            "pausable barrel windup/release/recovery, three-second barrel fuses, commanded minions, travelling storm clouds and shootable spiders with returned lightning, fungal transformation and poison, " +
            "separate giant legs and warning stomps, survivor carryover, victory/reset, input pauses and bounded hazards.");
    }

    private static void CheckMinuteBoundary()
    {
        var fixture = new Fixture();
        var game = fixture.Game;
        fixture.Advance(59940);
        Require(game.Snapshot is { Stage: StrykersRunStage.Running, Boss: null } && game.Snapshot.LevelSeconds < 60,
            "The end-of-level boss appeared before the full active minute.");
        fixture.Tick();
        var start = game.Snapshot;
        Require(start is { Level: 1, LevelSeconds: 60, Stage: StrykersRunStage.BossEntrance,
                Boss: { Kind: StrykersRunBossKind.BarrelWarden, EntranceProgress: 0, Y: StrykersRunGame.BossEntranceStartY } } &&
            start.Distance == 480 && start.Enemies.Count == 0 && start.Gates.Count == 0 &&
            start.Objectives.Count == 0 && start.Bullets.Count == 0,
            "Exactly 7,200 active steps must stop the road, clear encounters and begin the boss's walk from offstage.");
        Put(Boss(game), "AttackRemaining", 10000);
        fixture.Advance(1000);
        Require(game.Snapshot.Distance == start.Distance && game.Snapshot.LevelSeconds == 60 &&
            game.Snapshot.StageSeconds > 0 && game.Snapshot.RunSeconds > start.RunSeconds,
            "Boss time must be additional to the minute, with no forward road motion.");
        fixture.Advance(2000);
        Require(game.Snapshot is { Stage: StrykersRunStage.Boss, StageSeconds: 0,
                Boss: { EntranceProgress: 1, Y: StrykersRunGame.BossY } } && game.Snapshot.Distance == start.Distance,
            "The full walk must take three additional active seconds after the one-minute level.");

        var frequent = new Fixture();
        var sparse = new Fixture();
        Put(frequent.Game, "_levelTicks", 7190); Put(sparse.Game, "_levelTicks", 7190);
        frequent.Advance(600, 10); sparse.Advance(600, 30);
        Require(SameCampaign(frequent.Game.Snapshot, sparse.Game.Snapshot),
            "Rendering cadence changed the minute boundary or initial boss state.");
    }

    private static void CheckBossEntrance()
    {
        for (int level = 1; level <= StrykersRunGame.LevelCount; level++)
        {
            var fixture = new Fixture(level, 13, completeEntrance: false);
            var game = fixture.Game;
            var ally = Activator.CreateInstance(typeof(StrykersRunGame).GetNestedType("Ally", BindingFlags.NonPublic)!)!;
            Put(ally, "Health", 7);
            Put(game, "_ally", ally);
            Put(game, "_fireRemaining", 0f);
            Invalidate(game);
            var start = game.Snapshot;
            Require(start.Stage == StrykersRunStage.BossEntrance && start.StageSeconds == 0 &&
                start.Boss is { EntranceProgress: 0, Y: StrykersRunGame.BossEntranceStartY, BarrelThrowProgress: -1 } &&
                start.Boss.Value.Kind == (StrykersRunBossKind)(level - 1),
                "Each distinct boss must begin above the arena rather than appear at its combat position.");

            // Even a projectile already at the combat location must not damage
            // an arriving boss through the ordinary swept-contact path.
            Add(game, "_bullets", "Bullet", 1000,
                level == 5 ? StrykersRunGame.BossLeftLegX : StrykersRunGame.BossX,
                level == 5 ? StrykersRunGame.BossLegY + .07f : BossShotY);
            Call(game, "MoveBullets");
            Call(game, "DamageBoss", level == 5 ? 1 : 0);
            Invalidate(game);
            Require(game.Snapshot.Boss!.Value.Health == start.Boss!.Value.MaxHealth && game.Snapshot.Bullets.Count == 1,
                "Boss hit detection and direct damage must both reject the entrance stage.");
            List(game, "_bullets").Clear();
            Put(game, "_targetX", .75f);
            float previousY = start.Boss.Value.Y;
            for (int tick = 1; tick < 360; tick++)
            {
                fixture.OneStep();
                var state = game.Snapshot;
                Require(state.Stage == StrykersRunStage.BossEntrance && state.StageSeconds == tick / 120f &&
                    state.Boss is { PhaseSeconds: 0, AttackSeconds: 0, BarrelThrowProgress: -1 } boss &&
                    boss.EntranceProgress == tick / 360f && boss.Y > previousY && boss.Y < StrykersRunGame.BossY &&
                    boss.Health == boss.MaxHealth && state.SoldierCount == 13 && state.Ally is { Health: 7 } &&
                    state.Distance == start.Distance && state.LevelSeconds == 60 && state.Bullets.Count == 0 &&
                    state.Hazards.Count == 0 && state.Enemies.Count == 0,
                    "The full entrance must move monotonically while preserving survivors, stopping the road and withholding combat.");
                previousY = state.Boss!.Value.Y;
                if (tick != 180) continue;
                Require(state.SquadX > start.SquadX, "The player must retain steering during the boss entrance.");
                game.ClearInput(Epoch.AddMilliseconds(fixture.Milliseconds + 1));
                var frozen = game.Snapshot;
                fixture.Milliseconds += 1000;
                game.Advance(Epoch.AddMilliseconds(fixture.Milliseconds));
                Require(game.Phase == StrykersRunPhase.WaitingForInput && SameCampaign(frozen, game.Snapshot),
                    "Lifting the finger must freeze the entrance without skipping the boss's walk.");
                fixture.OneStep();
                Require(game.Phase == StrykersRunPhase.Playing && SameCampaign(frozen, game.Snapshot),
                    "Returning input must resume at the exact entrance position without replaying paused time.");
            }
            fixture.OneStep();
            var arrived = game.Snapshot;
            Require(arrived is { Stage: StrykersRunStage.Boss, StageSeconds: 0,
                    Boss: { EntranceProgress: 1, Y: StrykersRunGame.BossY, PhaseSeconds: 0, AttackSeconds: 0 } } &&
                arrived.Boss.Value.Health == arrived.Boss.Value.MaxHealth && arrived.Bullets.Count == 0 &&
                Get<int>(Boss(game), "AttackRemaining") == 144,
                "Exactly 360 active ticks must finish the three-second entrance and start a fresh combat clock.");
            Put(game, "_fireRemaining", 10000f);
            for (int tick = 1; tick < 144; tick++) fixture.OneStep();
            Require(game.Snapshot.Hazards.Count == 0 && game.Snapshot.Enemies.Count == 0,
                "Walking onto the scene must not consume the boss's initial attack delay.");
            fixture.OneStep();
            Require(level == 2 ? game.Snapshot.Enemies.Count == 8 : game.Snapshot.Hazards.Count == 1,
                "The first attack must occur 144 active ticks after arrival.");
        }
        var reset = new Fixture(3, 13, completeEntrance: false);
        reset.Advance(1000);
        reset.Game.Reset(Epoch.AddMilliseconds(reset.Milliseconds + 1));
        Require(reset.Game.Snapshot is { Phase: StrykersRunPhase.Ready, Stage: StrykersRunStage.Running,
            Level: 1, StageSeconds: 0, Boss: null, SoldierCount: 1 },
            "Reset during an entrance must clear the arriving boss and its progress.");
    }

    private static void CheckBarrelThrowPresentation()
    {
        var fixture = new Fixture(1, 20);
        var game = fixture.Game;
        Require(game.Snapshot.Boss is { BarrelThrowProgress: -1 },
            "A newly arrived warden must stay idle until the first windup, rather than play a spurious recovery.");
        for (int tick = 1; tick < 144; tick++)
        {
            fixture.OneStep();
            float expected = tick < 78 ? -1 : (tick - 78) / 120f;
            Require(game.Snapshot.Boss!.Value.BarrelThrowProgress == expected && game.Snapshot.Hazards.Count == 0,
                "The first windup must begin 66 active ticks before the unchanged 144-tick first release.");
            if (tick == 100) CheckFrozenThrow(fixture);
        }
        // Aim is sampled at release, not at the beginning of the windup.
        Put(game, "_squadX", .72f); Put(game, "_targetX", .72f);
        fixture.OneStep();
        var barrel = game.Snapshot.Hazards.Single();
        Require(game.Snapshot.Boss!.Value.BarrelThrowProgress == StrykersRunGame.BarrelThrowReleaseProgress &&
            barrel is { Kind: StrykersRunHazardKind.Barrel, Age: 0, Duration: 3 } &&
            barrel.X == game.Snapshot.SquadX && barrel.TargetX == barrel.X && Math.Abs(barrel.X - .72f) < .00001f,
            "The release pose must coincide with a new barrel, its locked lane and the start of its three-second fuse.");
        Put(game, "_targetX", .15f);
        for (int tick = 1; tick <= 54; tick++)
        {
            fixture.OneStep();
            var state = game.Snapshot;
            Require(state.Boss!.Value.BarrelThrowProgress == (66 + tick) / 120f &&
                state.Hazards.Single() is { Kind: StrykersRunHazardKind.Barrel } rolling &&
                rolling.Age == tick / 120f && rolling.X == barrel.X,
                "Recovery must progress with the rolling barrel, retaining the release lane and fuse clock.");
            if (tick == 17) CheckFrozenThrow(fixture);
        }
        for (int tick = 55; tick < 276; tick++)
        {
            fixture.OneStep();
            float expected = tick < 210 ? -1 : (tick - 210) / 120f;
            Require(game.Snapshot.Boss!.Value.BarrelThrowProgress == expected && game.Snapshot.Hazards.Count == 1,
                "The warden must return to idle, then wind up before the unchanged 276-tick repeat release.");
        }
        fixture.OneStep();
        var second = game.Snapshot.Hazards.Single(hazard => hazard.Id != barrel.Id);
        Require(game.Snapshot.Boss!.Value.BarrelThrowProgress == StrykersRunGame.BarrelThrowReleaseProgress &&
            game.Snapshot.Hazards.Count == 2 && second.Age == 0 &&
            game.Snapshot.Hazards.Single(hazard => hazard.Id == barrel.Id).Age == 276 / 120f,
            "The second release must occur after exactly 2.3 active seconds without resetting the first barrel's fuse.");
        for (int tick = 277; tick < 360; tick++) fixture.OneStep();
        Require(game.Snapshot.Hazards.Single(hazard => hazard.Id == barrel.Id) is
            { Kind: StrykersRunHazardKind.Barrel, Age: < 3 }, "Throw presentation shortened the barrel fuse.");
        fixture.OneStep();
        Require(game.Snapshot.Hazards.Single(hazard => hazard.Id == barrel.Id) is
            { Kind: StrykersRunHazardKind.BarrelBlast, Age: 0 },
            "Throw presentation must preserve the explosion exactly 360 active ticks after release.");
        game.Reset(Epoch.AddMilliseconds(fixture.Milliseconds + 1));
        Require(game.Snapshot.Boss is null && game.Snapshot.Hazards.Count == 0,
            "Reset must discard an ongoing throw and every released barrel.");
        for (int level = 2; level <= StrykersRunGame.LevelCount; level++)
        {
            var other = new Fixture(level, 20);
            other.Attack();
            Require(other.Game.Snapshot.Boss is { BarrelThrowProgress: -1 },
                "Other bosses must never expose the warden's throwing pose.");
        }
    }

    private static void CheckFrozenThrow(Fixture fixture)
    {
        var game = fixture.Game;
        game.ClearInput(Epoch.AddMilliseconds(fixture.Milliseconds + 1));
        var frozen = game.Snapshot;
        fixture.Milliseconds += 1000;
        game.Advance(Epoch.AddMilliseconds(fixture.Milliseconds));
        Require(game.Phase == StrykersRunPhase.WaitingForInput && SameCampaign(frozen, game.Snapshot),
            "Lifting the finger must freeze the throw pose and all barrel clocks.");
        fixture.OneStep();
        Require(game.Phase == StrykersRunPhase.Playing && SameCampaign(frozen, game.Snapshot),
            "Resuming a throw must preserve the exact pose without replaying paused time.");
    }

    private static void CheckBarrelFuse()
    {
        var fixture = new Fixture(1, 20);
        fixture.Attack();
        var game = fixture.Game;
        var barrel = game.Snapshot.Hazards.Single();
        Require(barrel is { Kind: StrykersRunHazardKind.Barrel, Age: 0, Duration: 3 } && Math.Abs(barrel.X - .5f) < .00001f,
            "The first boss must throw a visible barrel with a three-second fuse.");
        Put(Boss(game), "AttackRemaining", 10000);
        Put(game, "_targetX", .85f);
        fixture.Advance(2980);
        var rolling = game.Snapshot.Hazards.Single();
        Require(rolling.Kind == StrykersRunHazardKind.Barrel && rolling.Age < 3 && rolling.X == barrel.X &&
            rolling.Y > barrel.Y && game.Snapshot.SoldierCount == 20,
            "A rolling barrel must keep its original straight lane and must not explode early.");
        Put(Hazards(game)[0]!, "Ticks", 359);
        fixture.OneStep();
        Require(game.Snapshot.Hazards is [{ Kind: StrykersRunHazardKind.BarrelBlast, Age: 0, Y: StrykersRunGame.SquadY }] &&
            game.Snapshot.SoldierCount == 20,
            "A barrel must explode on tick 360 at the locked endpoint, allowing a sideways dodge.");

        fixture = new Fixture(1, 20);
        fixture.Attack();
        game = fixture.Game;
        Put(Hazards(game)[0]!, "Ticks", 359);
        fixture.OneStep();
        Require(game.Snapshot.SoldierCount == 15, "A barrel explosion overlapping the squad must apply its damage once.");
        fixture.Advance(300);
        Require(game.Snapshot.SoldierCount == 15, "The same barrel blast applied damage on multiple presentation frames.");
    }

    private static void CheckCommanderWaves()
    {
        var fixture = new Fixture(2, 20);
        fixture.Attack();
        var game = fixture.Game;
        var first = game.Snapshot;
        Require(first.Boss is { Kind: StrykersRunBossKind.SwarmCommander } && first.Enemies.Count == 8 &&
            first.Enemies.All(enemy => enemy.Y > first.Boss.Value.Y && enemy.X < .5f),
            "The commander must direct a minion wave from behind, leaving an open right lane.");
        fixture.Attack();
        Require(game.Snapshot.Enemies.Count == 16 && game.Snapshot.Enemies.Skip(8).All(enemy => enemy.X > .5f),
            "The commander's next wave must alternate to the other side.");
        for (int i = 0; i < 20; i++) fixture.Attack();
        Require(game.Snapshot.Enemies.Count == StrykersRunGame.MaximumEnemies &&
            game.Snapshot.Hazards.Count <= StrykersRunGame.MaximumHazards,
            "Commanded minions must share the existing bounded enemy collection.");
    }

    private static void CheckSkinWalker()
    {
        foreach (float edge in new[] { StrykersRunGame.RoadLeft, StrykersRunGame.RoadRight })
        {
            var edgeFixture = new Fixture(4, 10);
            Put(edgeFixture.Game, "_squadX", edge); Put(edgeFixture.Game, "_targetX", edge);
            edgeFixture.Attack();
            Put(Hazards(edgeFixture.Game)[0]!, "Ticks", 89);
            edgeFixture.OneStep();
            Require(edgeFixture.Game.Snapshot.SoldierCount == 6,
                "A locked tongue attack must reach a stationary hero at either road edge.");
        }
        var fixture = new Fixture(4, 20);
        fixture.Attack();
        var game = fixture.Game;
        Require(game.Snapshot.Boss is { Phase: 1 } && game.Snapshot.Hazards is [{ Kind: StrykersRunHazardKind.Tongue, IsWarning: true }],
            "The skin walker must begin as a tongue-attacking zombie.");
        var boss = Boss(game);
        int maximum = Get<int>(boss, "MaxHealth");
        Put(boss, "Health", maximum / 2 + 1);
        Call(game, "DamageBoss", 0);
        Invalidate(game);
        Require(game.Snapshot.Boss is { Phase: 2, PhaseSeconds: 0 } && game.Snapshot.Hazards.Count == 0,
            "Crossing half health must shed the old skin and cancel the old tongue attack.");
        int half = game.Snapshot.Boss!.Value.Health;
        Call(game, "DamageBoss", 0);
        Invalidate(game);
        Require(game.Snapshot.Boss!.Value.Health == half,
            "The skin transformation must finish visibly before further shots can skip the fungal phase.");
        fixture.Advance(1200);
        fixture.Attack();
        Require(game.Snapshot.Hazards is [{ Kind: StrykersRunHazardKind.PoisonGas, IsWarning: true, Duration: 4.5f }],
            "The transformed skin walker must replace its tongue with poison gas.");
        Put(Boss(game), "AttackRemaining", 10000);
        fixture.Advance(2900);
        int poisoned = game.Snapshot.SoldierCount;
        Require(poisoned < 20 && poisoned >= 18 && game.Snapshot.Hazards.Single().Y == StrykersRunGame.SquadY,
            "Green gas must travel toward its fixed target and poison at a bounded rate once it arrives.");
        fixture.Advance(600);
        Require(game.Snapshot.SoldierCount < poisoned, "Lingering in the poison cloud must cause another poison tick.");
        Put(game, "_squadX", .85f); Put(game, "_targetX", .85f);
        int escaped = game.Snapshot.SoldierCount;
        fixture.Advance(700);
        Require(game.Snapshot.SoldierCount == escaped, "Leaving the visible poison cloud must stop poison damage.");

        fixture = new Fixture(4, 20);
        game = fixture.Game;
        Put(Boss(game), "Phase", 2); Put(Boss(game), "PhaseTicks", 132);
        fixture.Attack();
        Put(game, "_targetX", .85f);
        int firstCloud = game.Snapshot.Hazards.Single().Id;
        fixture.Advance(2990);
        Require(game.Snapshot.Hazards.Count == 1, "The fungal boss cast its next cloud before the three-second cadence.");
        fixture.Tick();
        Require(game.Snapshot.Hazards.Count == 2, "The fungal boss did not cast its next cloud after three active seconds.");
        fixture.Advance(1500);
        Require(game.Snapshot.Hazards.All(hazard => hazard.Id != firstCloud) &&
            game.Snapshot.Hazards is [{ Age: < 2, Y: < .61f }],
            "The old poison cloud must clear an escape route before the next cloud reaches the squad.");
    }

    private static void CheckColossusAndNearestHit()
    {
        var fixture = new Fixture(5, 20);
        var game = fixture.Game;
        var before = game.Snapshot.Boss!.Value;
        Add(game, "_bullets", "Bullet", 900, .5f, .41f);
        fixture.OneStep();
        Require(game.Snapshot.Boss!.Value.Health == before.Health && game.Snapshot.Bullets.Count == 1,
            "Shots between the giant's legs must pass through rather than damage an invisible body.");
        List(game, "_bullets").Clear();
        Add(game, "_bullets", "Bullet", 901, StrykersRunGame.BossLeftLegX, StrykersRunGame.BossLegY + .07f);
        fixture.OneStep();
        var hit = game.Snapshot.Boss!.Value;
        Require(hit.LeftLegHealth == before.LeftLegHealth - 1 && hit.RightLegHealth == before.RightLegHealth &&
            hit.Health == hit.LeftLegHealth + hit.RightLegHealth,
            "A leg shot must damage only that leg and update the shared boss health total.");
        Put(Boss(game), "LeftLegHealth", 1);
        Call(game, "DamageBoss", 1);
        Invalidate(game);
        Require(game.Snapshot.Boss is { LeftLegHealth: 0, RightLegHealth: > 0 } && game.Snapshot.Stage == StrykersRunStage.Boss,
            "Destroying one leg must still require the other leg to be defeated.");
        Add(game, "_bullets", "Bullet", 902, StrykersRunGame.BossLeftLegX, StrykersRunGame.BossLegY + .07f);
        fixture.OneStep();
        Require(game.Snapshot.Bullets.Count == 1, "A disabled leg must not consume shots or damage the healthy leg.");
        fixture.Attack();
        var stomp = game.Snapshot.Hazards.Single();
        Require(stomp is { Kind: StrykersRunHazardKind.Stomp, IsWarning: true, WarningSeconds: 1.15f },
            "A colossus footstep must show a red warning long enough to dodge before impact.");
        int soldiers = game.Snapshot.SoldierCount;
        Put(game, "_targetX", .85f);
        fixture.Advance(1250);
        Require(game.Snapshot.SoldierCount == soldiers && game.Snapshot.Hazards.Single().X == stomp.X,
            "A stomp must hit the warned location rather than chase a dodging player.");

        fixture = new Fixture(5, 20);
        game = fixture.Game;
        Put(game, "_squadX", .75f); Put(game, "_targetX", .75f);
        fixture.Attack();
        Put(Hazards(game)[0]!, "Ticks", 137);
        int rightHealth = game.Snapshot.Boss!.Value.RightLegHealth;
        Add(game, "_bullets", "Bullet", 910, StrykersRunGame.BossRightLegX, StrykersRunGame.BossLegY + .07f);
        Add(game, "_bullets", "Bullet", 911, .75f, StrykersRunGame.SquadY + .07f);
        fixture.OneStep();
        Require(game.Snapshot.Boss!.Value.RightLegHealth == rightHealth - 1 && game.Snapshot.Bullets is [{ Id: 910 }],
            "A stomping foot must be hittable at its displayed impact position, while its vacated resting position misses.");
        Put(Hazards(game)[0]!, "Ticks", 179);
        float recoilY = StrykersRunFormation.StompFootY(StrykersRunGame.SquadY, (180 - 138) / 120f);
        Add(game, "_bullets", "Bullet", 912, .75f, recoilY + .07f);
        fixture.OneStep();
        Require(game.Snapshot.Boss!.Value.RightLegHealth == rightHealth - 2,
            "The retracting foot collider must follow the same recoil position as its artwork.");
        Put(Boss(game), "RightLegHealth", 1);
        Call(game, "DamageBoss", 2);
        Invalidate(game);
        Require(game.Snapshot.Hazards.Count == 0 &&
            StrykersRunFormation.StompingLeg(game.Snapshot.Boss!.Value.LeftLegHealth, 0, .8f) == 0,
            "Breaking a stomping leg must interrupt that attack; only the surviving leg may launch a later stomp.");

        fixture = new Fixture(1, 20);
        game = fixture.Game;
        Put(Boss(game), "AttackRemaining", 10000);
        int health = game.Snapshot.Boss!.Value.Health;
        Add(game, "_enemies", "Enemy", 920, .5f, .405f, StrykersRunEnemyKind.Runner, 1);
        Add(game, "_bullets", "Bullet", 921, .5f, .425f);
        fixture.OneStep();
        Require(game.Snapshot.Boss!.Value.Health == health && game.Snapshot.Enemies.Count == 0,
            "A bullet must hit the nearer minion before the boss behind it.");
        Add(game, "_bullets", "Bullet", 922, .5f, BossShotY);
        fixture.OneStep();
        Require(game.Snapshot.Boss!.Value.Health == health - 1, "An unobstructed swept bullet did not hit the boss.");
    }

    private const float BossShotY = StrykersRunGame.BossY + .08f;

    private static void CheckSurvivorsAndVictory()
    {
        var fixture = new Fixture(1, 13);
        var game = fixture.Game;
        var ally = Activator.CreateInstance(typeof(StrykersRunGame).GetNestedType("Ally", BindingFlags.NonPublic)!)!;
        Put(ally, "Health", 7);
        Put(game, "_ally", ally);
        for (int level = 1; level <= 5; level++)
        {
            Require(game.Snapshot.Level == level && game.Snapshot.Boss!.Value.Kind == (StrykersRunBossKind)(level - 1),
                "The campaign must visit each of the five distinct bosses in order.");
            var boss = Boss(game);
            if (level == 5)
            {
                Put(boss, "LeftLegHealth", 0); Put(boss, "RightLegHealth", 1);
            }
            Put(boss, "Health", 1);
            if (level == 4) { Put(boss, "Phase", 2); Put(boss, "PhaseTicks", 132); }
            Call(game, "DamageBoss", level == 5 ? 2 : 0);
            Invalidate(game);
            Require(game.Snapshot.SoldierCount == 13 && game.Snapshot.Ally is { Health: 7 } &&
                game.Snapshot.Hazards.Count == 0 && game.Snapshot.Bullets.Count == 0 && game.Snapshot.Enemies.Count == 0,
                "Boss clearance must retain every surviving troop and the ally's exact HP while clearing attacks.");
            if (level == 5) break;
            Require(game.Snapshot.Stage == StrykersRunStage.Intermission, "A cleared level must announce its transition.");
            float distance = game.Snapshot.Distance;
            fixture.Advance(2000);
            Require(game.Snapshot is { Stage: StrykersRunStage.Running, LevelSeconds: 0 } &&
                game.Snapshot.Level == level + 1 && game.Snapshot.Distance == distance &&
                game.Snapshot.SoldierCount == 13 && game.Snapshot.Ally is { Health: 7 },
                "The next one-minute level must start automatically after the stationary clear announcement.");
            Put(game, "_levelTicks", 7199);
            fixture.OneStep();
            fixture.CompleteEntrance();
        }
        var won = game.Snapshot;
        Require(won is { Phase: StrykersRunPhase.Victory, Stage: StrykersRunStage.Complete, Level: 5, HeroAlive: true },
            "The fifth boss must end in a living-hero campaign victory, never a sixth level.");
        fixture.Advance(3500);
        Require(game.Snapshot.PhaseSeconds == StrykersRunGame.GameOverPresentationSeconds && SameCampaign(won, game.Snapshot),
            "Victory controls must settle while the completed campaign remains frozen.");
        game.Start(Epoch.AddMilliseconds(fixture.Milliseconds + 10));
        Require(game.Snapshot is { Phase: StrykersRunPhase.Starting, Stage: StrykersRunStage.Running, Level: 1,
                LevelSeconds: 0, SoldierCount: 1, Boss: null, Ally: null } && game.Snapshot.Hazards.Count == 0,
            "PLAY after victory must reset the whole campaign to the lone hero on level one.");
    }

    private static void CheckPausedCampaign()
    {
        foreach (int level in new[] { 1, 3, 4, 5 })
        {
            var fixture = new Fixture(level, 20);
            if (level == 4) { Put(Boss(fixture.Game), "Phase", 2); Put(Boss(fixture.Game), "PhaseTicks", 132); }
            fixture.Attack();
            var game = fixture.Game;
            game.ClearInput(Epoch.AddMilliseconds(fixture.Milliseconds + 1));
            var frozen = game.Snapshot;
            for (int i = 0; i < 100; i++)
            {
                fixture.Milliseconds += 10;
                game.Advance(Epoch.AddMilliseconds(fixture.Milliseconds));
            }
            Require(game.Snapshot.Phase == StrykersRunPhase.WaitingForInput && SameCampaign(frozen, game.Snapshot),
                "Missing rail input must freeze every boss, fuse, warning, poison tick and animation.");
            game.SetInput(.5f, Epoch.AddMilliseconds(fixture.Milliseconds - 1100));
            game.Advance(Epoch.AddMilliseconds(fixture.Milliseconds + 1));
            Require(game.Snapshot.Phase == StrykersRunPhase.WaitingForInput && SameCampaign(frozen, game.Snapshot),
                "An old camera sample must not restart a paused boss attack.");
            fixture.Tick();
            Require(game.Snapshot.Phase == StrykersRunPhase.Playing && SameCampaign(frozen, game.Snapshot),
                "Returning input must resume the exact boss frame without accumulating offline time.");
        }
    }

    private static void CheckBossBalanceAndBounds()
    {
        for (int level = 1; level <= 5; level++)
        {
            var lone = new Fixture(level, 1);
            var squad = new Fixture(level, 64);
            int loneHealth = lone.Game.Snapshot.Boss!.Value.MaxHealth;
            int squadHealth = squad.Game.Snapshot.Boss!.Value.MaxHealth;
            Require((level == 3 ? loneHealth == 360 : loneHealth is >= 100 and <= 200) && squadHealth == loneHealth * 13 / 2,
                "Boss health must be beatable by a lone hero and scale once to a large incoming squad.");
            Put(squad.Game, "_soldiers", 1);
            squad.OneStep();
            Require(squad.Game.Snapshot.Boss!.Value.MaxHealth == squadHealth,
                "Taking casualties must not resize or heal the current boss.");
        }
        var fixture = new Fixture(1, 64);
        for (int i = 0; i < 50; i++) fixture.Attack();
        Require(fixture.Game.Snapshot.Hazards.Count == StrykersRunGame.MaximumHazards,
            "Boss attacks must have a hard bound on active hazards.");
    }

    private static void CheckPlayableBossFights()
    {
        // Only encounter setup uses reflection. From here, a deterministic player
        // reads the public scene and supplies the same absolute rail input as a hand;
        // damage, firing, dodges and victory all go through the live fixed-step path.
        foreach (int soldiers in new[] { 1, 64 })
        for (int level = 1; level <= 5; level++)
        {
            var fixture = new Fixture(level, soldiers);
            var game = fixture.Game;
            Put(game, "_fireRemaining", 0f);
            bool sawAttack = false, sawFungal = false;
            var stormClouds = new HashSet<int>();
            int spiderKills = 0;
            float started = game.Snapshot.RunSeconds;
            for (int frame = 0; frame < 12000 && game.Snapshot.Stage == StrykersRunStage.Boss &&
                game.Snapshot.Phase != StrykersRunPhase.GameOver; frame++)
            {
                var state = game.Snapshot;
                sawAttack |= state.Hazards.Count > 0 || state.Enemies.Count > 0;
                sawFungal |= state.Boss is { Phase: 2 };
                foreach (var cloud in state.Hazards.Where(hazard => hazard.Kind == StrykersRunHazardKind.StormCloud))
                    stormClouds.Add(cloud.Id);
                spiderKills = Math.Max(spiderKills, state.Craters.Count);
                float target = ChooseBossLane(state);
                fixture.Milliseconds += 10;
                var now = Epoch.AddMilliseconds(fixture.Milliseconds);
                game.SetInput(Math.Clamp((target - StrykersRunGame.RoadLeft) /
                    (StrykersRunGame.RoadRight - StrykersRunGame.RoadLeft), 0, 1), now);
                game.Advance(now);
                if (game.Snapshot.Phase == StrykersRunPhase.GameOver)
                    Console.WriteLine($"Defeat at x={state.SquadX:F3}, target={target:F3}, time={state.StageSeconds:F2}; " +
                        string.Join("; ", state.Hazards.Select(hazard =>
                            $"{hazard.Kind} age={hazard.Age:F2} x={hazard.X:F3} y={hazard.Y:F3} target={hazard.TargetX:F3}")));
            }
            var end = game.Snapshot;
            Require(end.HeroAlive && end.Stage is StrykersRunStage.Intermission or StrykersRunStage.Complete,
                $"Level {level} could not be beaten by the steering-only player starting with {soldiers} soldier(s): " +
                $"stage={end.Stage}, phase={end.Phase}, bossHP={end.Boss?.Health}, squad={end.SoldierCount}.");
            Require(sawAttack && (level != 4 || sawFungal),
                "A full-strength squad skipped a boss's defining attack or the fungal phase.");
            Require(level != 3 || stormClouds.Count >= 3 && spiderKills is >= 2 and <= 10,
                "A public storm fight must allow several cloud/spider cycles and keep permanent craters naturally bounded by boss health.");
            Console.WriteLine($"Boss {level}: {soldiers} initial soldiers, {end.RunSeconds - started:F1}s, " +
                $"{end.SoldierCount} survivors, steering-only victory." +
                (level == 3 ? $" {stormClouds.Count} clouds, {spiderKills} spider kills." : ""));
        }
    }

    private static float ChooseBossLane(StrykersRunSnapshot state)
    {
        float aim = state.Boss is { Kind: StrykersRunBossKind.Colossus } boss ?
            boss.LeftLegHealth > 0 ? StrykersRunGame.BossLeftLegX : StrykersRunGame.BossRightLegX : .5f;
        var spider = state.Enemies.Where(enemy => enemy.Kind == StrykersRunEnemyKind.ElectricSpider && enemy.Y < .76f)
            .OrderByDescending(enemy => enemy.Y).FirstOrDefault();
        if (spider.Kind == StrykersRunEnemyKind.ElectricSpider) aim = spider.X;
        float best = aim, bestCost = float.PositiveInfinity;
        for (int candidate = -1; candidate <= 40; candidate++)
        {
            float x = candidate < 0 ? aim : .08f + candidate * .021f;
            var bounds = StrykersRunFormation.HorizontalBounds(x, state.SoldierCount);
            float cost = Math.Abs(x - aim) + Math.Abs(x - state.SquadX) * .08f;
            foreach (var hazard in state.Hazards)
            {
                bool imminent = hazard.Kind switch
                {
                    StrykersRunHazardKind.Barrel => hazard.Age >= 2.35f,
                    StrykersRunHazardKind.BarrelBlast => true,
                    StrykersRunHazardKind.PoisonGas => hazard.Age >= 2.05f,
                    StrykersRunHazardKind.StormCloud => !hazard.IsWarning && hazard.Y is > .56f and < 1.02f,
                    StrykersRunHazardKind.ReturnLightning => false,
                    _ => hazard.Age >= hazard.WarningSeconds - .50f
                };
                float radius = hazard.Kind == StrykersRunHazardKind.Barrel ? .14f : hazard.Radius;
                if (imminent && bounds.Left < hazard.TargetX + radius + .005f &&
                    bounds.Right > hazard.TargetX - radius - .005f) cost += 100;
                // A safe destination is not a safe dodge if reaching it crosses
                // a lingering cloud. Stay on the current side of live poison.
                if ((hazard.Kind == StrykersRunHazardKind.PoisonGas && hazard.Y > .61f ||
                     hazard.Kind == StrykersRunHazardKind.StormCloud && hazard.Y is > .64f and < 1.02f) &&
                    (state.SquadX < hazard.X - radius && x > hazard.X - radius ||
                     state.SquadX > hazard.X + radius && x < hazard.X + radius)) cost += 1000;
            }
            foreach (var enemy in state.Enemies)
                if (enemy.Y > (enemy.Kind == StrykersRunEnemyKind.ElectricSpider ? .70f : .56f) &&
                    enemy.Y < .81f && bounds.Left < enemy.X + .045f && bounds.Right > enemy.X - .045f)
                    cost += 20;
            if (cost < bestCost) { bestCost = cost; best = x; }
        }
        return best;
    }

    private sealed class Fixture
    {
        public readonly StrykersRunGame Game = StrykersRunGameRegression.Playing(quiet: true);
        public int Milliseconds = 500;
        public Fixture(int level = 0, int soldiers = 1, bool completeEntrance = true)
        {
            Put(Game, "_soldiers", soldiers);
            Put(Game, "_fireRemaining", 10000f);
            if (level != 0)
            {
                Put(Game, "_level", level);
                Call(Game, "BeginBoss");
                if (completeEntrance) CompleteEntrance();
                Put(Game, "_fireRemaining", 10000f);
            }
            Invalidate(Game);
        }
        public void OneStep() { Put(Game, "_accumulator", 0d); Tick(); }
        public void CompleteEntrance()
        {
            Invalidate(Game);
            for (int tick = 0; tick < 360; tick++) OneStep();
            Require(Game.Snapshot.Stage == StrykersRunStage.Boss, "The isolated combat fixture did not complete its entrance.");
        }
        public void Tick(int milliseconds = 10)
        {
            Milliseconds += milliseconds;
            var now = Epoch.AddMilliseconds(Milliseconds);
            Game.SetInput(Math.Clamp((Get<float>(Game, "_targetX") - .08f) / .84f, 0, 1), now);
            Game.Advance(now);
        }
        public void Advance(int milliseconds, int cadence = 10)
        {
            int end = Milliseconds + milliseconds;
            while (Milliseconds < end) Tick(Math.Min(cadence, end - Milliseconds));
        }
        public void Attack() { Put(Boss(Game), "AttackRemaining", 1); OneStep(); }
    }

    private static object Boss(StrykersRunGame game) => Get<object>(game, "_boss");
    private static IList Hazards(StrykersRunGame game) => List(game, "_hazards");
    private static IList List(StrykersRunGame game, string field) => Get<IList>(game, field);
    private static T Get<T>(object instance, string field) => (T)instance.GetType().GetField(field,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static void Put(object instance, string field, object value) => instance.GetType().GetField(field,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
    private static void Call(StrykersRunGame game, string method, params object[] args) => typeof(StrykersRunGame)
        .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(game, args);
    private static void Invalidate(StrykersRunGame game) => Call(game, "Change");
    private static void Add(StrykersRunGame game, string field, string type, params object[] values) =>
        List(game, field).Add(Activator.CreateInstance(typeof(StrykersRunGame).GetNestedType(type, BindingFlags.NonPublic)!, values)!);
    private static bool SameCampaign(StrykersRunSnapshot a, StrykersRunSnapshot b) => a.Level == b.Level &&
        a.LevelSeconds == b.LevelSeconds && a.Stage == b.Stage && a.StageSeconds == b.StageSeconds &&
        a.RunSeconds == b.RunSeconds && a.Distance == b.Distance && a.SoldierCount == b.SoldierCount &&
        a.SquadX == b.SquadX && a.Boss == b.Boss && a.Ally == b.Ally &&
        a.Hazards.SequenceEqual(b.Hazards) && a.Enemies.SequenceEqual(b.Enemies) && a.Bullets.SequenceEqual(b.Bullets) &&
        a.Craters.SequenceEqual(b.Craters);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
