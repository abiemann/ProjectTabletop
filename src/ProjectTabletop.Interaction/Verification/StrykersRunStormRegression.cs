using ProjectTabletop.Interaction;

internal static partial class StrykersRunCampaignRegression
{
    private static void CheckStormEncounter()
    {
        CheckStormSequence();
        CheckStormRainDamage();
        CheckSpiderDamageAndReturn();
        CheckSpiderContactAndBounds();
        CheckStormCraterLifecycle();
        CheckStationaryStormRisk();
    }

    private static void CheckStormSequence()
    {
        var fixture = new Fixture(3, 20);
        var game = fixture.Game;
        foreach (float lane in new[] { .24f, .76f, .5f, .24f })
        {
            Hazards(game).Clear(); List(game, "_enemies").Clear();
            fixture.Attack();
            Put(Boss(game), "AttackRemaining", 10000);
            var first = game.Snapshot.Hazards.Single();
            Require(first is { Kind: StrykersRunHazardKind.StormCloud, Age: 0, IsWarning: true } &&
                first.X == lane && first.TargetX == lane && first.Y == StrykersRunGame.StormCloudStartY &&
                first.WarningSeconds == StrykersRunGame.StormCloudWarningSeconds &&
                Math.Abs(first.Duration - 4.35f) < .00001f && game.Snapshot.Enemies.Count == 0,
                "Storm attacks must cycle left/right/centre with a stationary charged cloud before lightning or a spider.");
            for (int tick = 1; tick <= 89; tick++) fixture.OneStep();
            Require(game.Snapshot.Hazards.Single() is { IsWarning: true } && game.Snapshot.Enemies.Count == 0,
                "A charging cloud spawned its spider or rain too early.");
            fixture.OneStep();
            Require(game.Snapshot.Hazards.Single() is { IsWarning: false, Y: StrykersRunGame.StormCloudStartY } &&
                game.Snapshot.Enemies.Count == 0, "Rain must activate exactly after .75 active seconds before its spider follows.");
            for (int tick = 91; tick < 132; tick++) fixture.OneStep();
            Require(game.Snapshot.Enemies.Count == 0, "The spider must wait .35 active seconds after rain begins.");
            fixture.OneStep();
            var spider = game.Snapshot.Enemies.Single();
            Require(spider.Kind == StrykersRunEnemyKind.ElectricSpider && spider.Health == spider.MaxHealth &&
                spider.Health == 23 && Math.Abs(spider.X - lane) > .2f &&
                game.Snapshot.Hazards.Single().Y > first.Y,
                "The travelling rain must release a shootable spider in a distinct lane using entry-scaled HP.");
            var frozen = game.Snapshot;
            game.ClearInput(Epoch.AddMilliseconds(fixture.Milliseconds + 1));
            fixture.Milliseconds += 1000;
            game.Advance(Epoch.AddMilliseconds(fixture.Milliseconds));
            Require(SameCampaign(frozen, game.Snapshot), "Lifting input must freeze the moving cloud and walking spider.");
            fixture.OneStep();
            Require(SameCampaign(frozen, game.Snapshot), "Resuming must not fast-forward rain or spider movement.");
            for (int tick = 133; tick < 160; tick++) fixture.OneStep();
            Require(game.Snapshot.Enemies.Count == 1, "One cloud must not spawn its spider on more than one tick.");
        }
        game.Reset(Epoch.AddMilliseconds(fixture.Milliseconds + 1));
        Require(game.Snapshot is { Boss: null, Level: 1, Stage: StrykersRunStage.Running } &&
            game.Snapshot.Enemies.Count == 0 && game.Snapshot.Hazards.Count == 0 && game.Snapshot.Craters.Count == 0,
            "Reset must clear the storm cloud, its spider and attack sequence.");
    }

    private static void CheckStormRainDamage()
    {
        var fixture = new Fixture(3, 20);
        var game = fixture.Game;
        Put(game, "_squadX", .24f); Put(game, "_targetX", .24f);
        fixture.Attack(); Put(Boss(game), "AttackRemaining", 10000);
        var cloud = Hazards(game)[0]!;
        Put(cloud, "Ticks", 329);
        fixture.OneStep();
        Require(game.Snapshot.SoldierCount == 16, "Rain must damage defenders only when its travelling ground footprint reaches them.");
        for (int tick = 331; tick < 378; tick++) fixture.OneStep();
        Require(game.Snapshot.SoldierCount == 16, "Lightning rain damage must be bounded to one pulse per .4 active seconds.");
        fixture.OneStep();
        Require(game.Snapshot.SoldierCount == 12, "Remaining beneath rain must receive its next visible pulse.");
        Put(game, "_squadX", .76f); Put(game, "_targetX", .76f);
        fixture.Advance(1500);
        Require(game.Snapshot.SoldierCount == 12 && game.Snapshot.Hazards.Count == 0,
            "Leaving the rain must stop damage and the cloud must expire after travelling beyond the squad.");

        fixture = new Fixture(3, 20); game = fixture.Game;
        fixture.Attack(); Put(Boss(game), "AttackRemaining", 10000);
        Put(Hazards(game)[0]!, "Ticks", 329);
        fixture.OneStep();
        Require(game.Snapshot.SoldierCount == 20, "A cloud must not damage the entire arena or an adjacent safe lane.");
    }

    private static void CheckSpiderDamageAndReturn()
    {
        var fixture = new Fixture(3, 20);
        var game = fixture.Game;
        Put(Boss(game), "AttackRemaining", 10000);
        int bossHealth = game.Snapshot.Boss!.Value.Health;
        Add(game, "_bullets", "Bullet", 901, .5f, BossShotY);
        fixture.OneStep();
        Require(game.Snapshot.Boss!.Value.Health == bossHealth - 1,
            "Ordinary bullets must still damage Storm Revenant without waiting for a spider.");
        bossHealth--;
        Add(game, "_enemies", "Enemy", 902, .5f, .5f, StrykersRunEnemyKind.ElectricSpider, 2);
        Add(game, "_bullets", "Bullet", 903, .5f, .54f);
        fixture.OneStep();
        Require(game.Snapshot.Enemies.Single() is { Health: 1, MaxHealth: 2 } && game.Snapshot.Hazards.Count == 0 &&
            game.Snapshot.Boss!.Value.Health == bossHealth,
            "A nearer electric spider must absorb a normal shot, keep its remaining HP and withhold return lightning until defeated.");
        var spider = List(game, "_enemies")[0]!;
        Add(game, "_bullets", "Bullet", 904, .5f, .54f);
        fixture.OneStep();
        var returning = game.Snapshot.Hazards.Single();
        var crater = game.Snapshot.Craters.Single();
        Require(game.Snapshot.Enemies.Count == 0 && game.Snapshot.Boss!.Value.Health == bossHealth &&
            returning is { Kind: StrykersRunHazardKind.ReturnLightning, Age: 0, WarningSeconds: .65f, Duration: .90f } &&
            returning.X == Get<float>(spider, "X") && returning.Y == Get<float>(spider, "Y") &&
            returning.TargetX == StrykersRunGame.BossX && returning.TargetY == StrykersRunGame.BossY &&
            crater.Id == 902 && crater.X == returning.X && crater.Y == returning.Y && crater.Age == 0,
            "A shot-down spider must release lightning from its exact death position; the boss takes no damage before arrival.");
        Call(game, "DamageEnemy", spider, 1); Invalidate(game);
        Require(game.Snapshot.Hazards.Count == 1 && game.Snapshot.Craters.Count == 1,
            "Repeated damage to an already defeated spider must not duplicate return lightning or its crater.");
        for (int tick = 1; tick < 78; tick++) fixture.OneStep();
        Require(game.Snapshot.Boss!.Value.Health == bossHealth, "Returned lightning damaged its boss before its .65-second flight completed.");
        game.ClearInput(Epoch.AddMilliseconds(fixture.Milliseconds + 1));
        var frozen = game.Snapshot;
        fixture.Milliseconds += 1000; game.Advance(Epoch.AddMilliseconds(fixture.Milliseconds));
        Require(SameCampaign(frozen, game.Snapshot), "A paused return must not land or remove boss health.");
        fixture.OneStep(); fixture.OneStep();
        int expected = bossHealth - game.Snapshot.Boss!.Value.MaxHealth / 8;
        Require(game.Snapshot.Boss!.Value.Health == expected && game.Snapshot.Hazards.Single().Age == .65f,
            "A return must apply its aggregate damage exactly once on arrival and retain a short impact tail.");
        fixture.Advance(300);
        Require(game.Snapshot.Boss!.Value.Health == expected && game.Snapshot.Hazards.Count == 0 &&
            game.Snapshot.Craters.Single().X == crater.X && game.Snapshot.Craters.Single().Y == crater.Y,
            "The return's visible impact tail must not deal repeated damage.");

        // A lethal return is processed while iterating hazards. Clearing every
        // encounter must neither revisit a removed item nor fire after victory.
        Add(game, "_enemies", "Enemy", 905, .5f, .5f, StrykersRunEnemyKind.ElectricSpider, 1);
        Call(game, "DamageEnemy", List(game, "_enemies")[0]!, 1);
        fixture.Attack();
        var bolt = Hazards(game).Cast<object>().Single(item => Get<StrykersRunHazardKind>(item, "Kind") == StrykersRunHazardKind.ReturnLightning);
        Put(bolt, "Ticks", 77); Put(Boss(game), "Health", 1); Put(game, "_fireRemaining", 0f);
        fixture.OneStep();
        Require(game.Snapshot.Stage == StrykersRunStage.Intermission && game.Snapshot.Boss is null &&
            game.Snapshot.Hazards.Count == 0 && game.Snapshot.Enemies.Count == 0 && game.Snapshot.Bullets.Count == 0,
            "Lethal returned lightning must atomically clear the arena and finish this step without firing into intermission.");
    }

    private static void CheckSpiderContactAndBounds()
    {
        foreach (bool allyContact in new[] { false, true })
        {
            var fixture = new Fixture(3, 20);
            var game = fixture.Game;
            Put(Boss(game), "AttackRemaining", 10000);
            if (allyContact)
            {
                var ally = Activator.CreateInstance(typeof(StrykersRunGame).GetNestedType("Ally", System.Reflection.BindingFlags.NonPublic)!)!;
                Put(game, "_ally", ally);
            }
            Add(game, "_enemies", "Enemy", 920, .5f, (allyContact ? .66f : StrykersRunGame.SquadY) - .0005f,
                StrykersRunEnemyKind.ElectricSpider, 40);
            fixture.OneStep();
            Require(game.Snapshot.Enemies.Count == 0 && game.Snapshot.Hazards.Count == 0 &&
                game.Snapshot.Craters.Count == 0 &&
                (allyContact ? game.Snapshot.Ally is { Health: 16 } : game.Snapshot.SoldierCount == 12),
                "A spider consumed by defender contact must cause contact damage without awarding returned lightning.");
        }
        var bounded = new Fixture(3, 20);
        var boundedGame = bounded.Game;
        Add(boundedGame, "_enemies", "Enemy", 921, .9f, 1.0598f, StrykersRunEnemyKind.ElectricSpider, 40);
        bounded.OneStep();
        Require(boundedGame.Snapshot.Enemies.Count == 0 && boundedGame.Snapshot.Hazards.Count == 0 && boundedGame.Snapshot.Craters.Count == 0,
            "A spider leaving the board alive must not reward boss damage.");
        for (int i = 0; i < 100; i++) Call(boundedGame, "AddEnemy", StrykersRunEnemyKind.ElectricSpider, .24f, .38f);
        for (int i = 0; i < 50; i++) bounded.Attack();
        Invalidate(boundedGame);
        Require(boundedGame.Snapshot.Enemies.Count == StrykersRunGame.MaximumEnemies &&
            boundedGame.Snapshot.Hazards.Count == StrykersRunGame.MaximumHazards,
            "Storm spiders and clouds must share the existing bounded collections.");
        foreach (object cloud in Hazards(boundedGame)) Put(cloud, "Ticks", 131);
        bounded.OneStep();
        Require(boundedGame.Snapshot.Enemies.Count == StrykersRunGame.MaximumEnemies,
            "A simultaneous cloud activation must respect the shared enemy cap.");
    }

    private static void CheckStormCraterLifecycle()
    {
        var fixture = new Fixture(3, 20);
        var game = fixture.Game;
        // This isolated fixture begins at the real end-of-level road distance.
        Put(game, "_distance", 3 * 60 * 8f);
        Put(Boss(game), "AttackRemaining", 10000);
        Add(game, "_enemies", "Enemy", 940, .5f, .5f, StrykersRunEnemyKind.ElectricSpider, 1);
        Call(game, "DamageEnemy", List(game, "_enemies")[0]!, 1);
        Invalidate(game);
        var crater = game.Snapshot.Craters.Single();
        fixture.Advance(3000);
        var lingering = game.Snapshot.Craters.Single();
        Require(lingering.Id == crater.Id && lingering.X == crater.X && lingering.Y == crater.Y && lingering.Age > 2.9f,
            "A spider crater must remain on the same physical ground throughout the stationary boss battle, with active smoke time.");
        game.ClearInput(Epoch.AddMilliseconds(fixture.Milliseconds + 1));
        var frozen = game.Snapshot;
        fixture.Milliseconds += 1000; game.Advance(Epoch.AddMilliseconds(fixture.Milliseconds));
        Require(SameCampaign(frozen, game.Snapshot), "Crater smoke and glow must freeze with missing rail input.");
        fixture.OneStep();
        Put(Boss(game), "Health", 1); Call(game, "DamageBoss", 0); Invalidate(game);
        Require(game.Snapshot.Stage == StrykersRunStage.Intermission && game.Snapshot.Craters.Single().Y == crater.Y,
            "Defeating the storm boss must preserve its scorch marks through the level-clear pause.");
        fixture.Advance(2000);
        Require(game.Snapshot is { Level: 4, Stage: StrykersRunStage.Running, LevelSeconds: 0 } &&
            game.Snapshot.Craters.Single().Y == crater.Y,
            "The next level must retain every visible crater until forward road travel resumes.");
        float initialDistance = game.Snapshot.Distance;
        while (game.Snapshot.Craters.Count > 0)
        {
            var previous = game.Snapshot.Craters.Single();
            fixture.OneStep();
            float expectedY = crater.Y + (game.Snapshot.Distance - initialDistance) * StrykersRunGame.StormCraterScrollScale;
            if (expectedY <= StrykersRunGame.StormCraterCullY)
                Require(game.Snapshot.Craters is [var moved] && moved.Y == expectedY && moved.Y > previous.Y,
                    "A crater must scroll toward the player at the road texture's exact speed, without prematurely disappearing.");
            else Require(game.Snapshot.Craters.Count == 0,
                "A crater and its smoke must release their state once the entire effect has passed below the visible road.");
        }
        Require(List(game, "_stormCraters").Count == 0, "Offscreen craters must be removed from model memory, not merely hidden.");

        fixture = new Fixture(3, 20); game = fixture.Game;
        Add(game, "_enemies", "Enemy", 941, .5f, .5f, StrykersRunEnemyKind.ElectricSpider, 1);
        Call(game, "DamageEnemy", List(game, "_enemies")[0]!, 1);
        game.Reset(Epoch.AddMilliseconds(fixture.Milliseconds + 1));
        Require(game.Snapshot.Craters.Count == 0 && List(game, "_stormCraters").Count == 0,
            "Reset or leaving the board must discard lingering crater state immediately.");
    }

    private static void CheckStationaryStormRisk()
    {
        var fixture = new Fixture(3, 64);
        var game = fixture.Game;
        Put(game, "_fireRemaining", 0f);
        var clouds = new HashSet<int>();
        float started = game.Snapshot.RunSeconds;
        while (game.Snapshot.Stage == StrykersRunStage.Boss && game.Snapshot.Phase != StrykersRunPhase.GameOver &&
            game.Snapshot.RunSeconds - started < 120)
        {
            foreach (var cloud in game.Snapshot.Hazards.Where(hazard => hazard.Kind == StrykersRunHazardKind.StormCloud))
                clouds.Add(cloud.Id);
            fixture.Tick();
        }
        var end = game.Snapshot;
        Require(end.SoldierCount <= 32 && clouds.Count >= 3,
            "A full squad standing still in the centre must suffer meaningful consequences from the new storm encounter.");
        Console.WriteLine($"Storm stationary centre: {end.RunSeconds - started:F1}s, {end.Phase}, " +
            $"{end.SoldierCount} survivors, bossHP={end.Boss?.Health ?? 0}, {clouds.Count} clouds, {end.Craters.Count} spider kills.");
    }
}
