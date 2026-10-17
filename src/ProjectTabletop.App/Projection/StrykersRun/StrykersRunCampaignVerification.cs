#if DEBUG
using Microsoft.Graphics.Canvas;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection.StrykersRun;

internal sealed record StrykersRunCampaignRenderResult(string[] Images, int RenderedFrames,
    bool DistinctBosses, bool AttackWarnings, bool SkinTransformation, bool BossHealth,
    bool Victory, bool RailProtected, bool ArticulatedThrow, bool BarrelRelease,
    bool AnimatedFlames, bool PausedAttacks, bool StormClouds, bool ElectricSpiders,
    bool ReturnedLightning, bool CompactBossHud, bool PersistentStormCraters);

/// <summary>Native offscreen fixtures, also callable without constructing an app window or camera.</summary>
internal static class StrykersRunCampaignVerification
{
    internal static async Task<StrykersRunCampaignRenderResult> RunAsync(CanvasDevice device, string directory)
    {
        Directory.CreateDirectory(directory);
        using var renderer = new StrykersRunRenderer(device);
        await renderer.EnsureReadyAsync();
        Require(renderer.Ready && renderer.Error is null, "Campaign artwork did not load.");
        using var target = new CanvasRenderTarget(device, 900, 1600, 96);
        var images = new List<string>();
        int frames = 0;
        var baseline = new StrykersRunSnapshot(StrykersRunPhase.Playing, .5f, 9, 2400,
            480, 3, 61, 0, false, "", 1, [], [], [], [])
        { Level = 1, LevelSeconds = 60, Stage = StrykersRunStage.Boss, StageSeconds = 1 };
        byte[]? previousBoss = null, plainBarrel = null;
        foreach (var kind in Enum.GetValues<StrykersRunBossKind>())
        {
            var boss = Boss(kind);
            var state = baseline with { Level = (int)kind + 1, Boss = boss };
            byte[] pixels = await Capture(state, $"level-{(int)kind + 1}-boss");
            if (previousBoss is not null)
                Require(Changed(previousBoss, pixels, new(50, 210, 800, 510)) > 2000,
                    "Successive bosses did not have distinct visible bodies.");
            previousBoss = pixels;
            if (kind == StrykersRunBossKind.BarrelWarden) plainBarrel = pixels;
            byte[] hurt = Draw(state with { Boss = boss with { Health = 60, LeftLegHealth = 30, RightLegHealth = 30 } });
            Require(Changed(pixels, hurt, new(0, 120, 900, 150)) > 50,
                "Boss health did not visibly change.");
            var entering = state with { Boss = boss with { Y = StrykersRunGame.BossEntranceStartY,
                EntranceProgress = 0 } };
            byte[] hintClear = await Capture(entering with { Boss = null }, $"level-{state.Level}-hint-clear");
            byte[] hintPanel = await Capture(entering, $"level-{state.Level}-hint-panel");
            Require(Changed(hintClear, hintPanel, new(65, 216, 770, 28)) == 0,
                $"A boss instruction hint remained below the compact health panel. Compare level-{state.Level}-hint-clear/panel in {directory}.");
            for (int x = 90; x < 810; x++)
            {
                int index = (210 * 900 + x) * 4;
                Require(pixels[index] < 50 && pixels[index + 1] < 45 && pixels[index + 2] < 40,
                    "The boss health meter lost its dark lower padding.");
            }
            await CheckEntrance(state);
        }

        var barrel = baseline with { Boss = Boss(StrykersRunBossKind.BarrelWarden) };
        await CheckBarrelThrow(barrel);
        await Capture(barrel with { Hazards = [Hazard(StrykersRunHazardKind.Barrel, .38f, .32f, .38f, .78f, .12f, 0, 3, .065f)] }, "barrel-in-flight");
        await Capture(barrel with { Hazards = [Hazard(StrykersRunHazardKind.Barrel, .5f, .45f, .5f, .78f, .94f, 0, 3, .065f)] }, "barrel-rolling");
        await Capture(barrel with { Hazards = [Hazard(StrykersRunHazardKind.Barrel, .5f, .75f, .5f, .78f, 2.82f, 0, 3, .065f)] }, "barrel-fuse-ending");
        await Capture(barrel with { Hazards = [Hazard(StrykersRunHazardKind.BarrelBlast, .5f, .78f, .5f, .78f, .16f, 0, .45f, .14f)] }, "barrel-explosion");
        var explosion = Hazard(StrykersRunHazardKind.BarrelBlast, .5f, .78f, .5f, .78f, 0, 0, .45f, .14f);
        (float Age, string Name)[] explosionPhases =
        [
            (0, "ignition"), (.04f, "004"), (.10f, "010"),
            (.20f, "020"), (.34f, "034"), (.44f, "044")
        ];
        byte[]? previousExplosion = null;
        var blastBounds = new Rect(0, 600, 900, 840);
        foreach (var phase in explosionPhases)
        {
            byte[] pixels = await Capture(barrel with { Hazards = [explosion with { Age = phase.Age }] },
                "barrel-explosion-" + phase.Name);
            Require(Changed(plainBarrel!, pixels, blastBounds) > 500,
                "A barrel explosion phase was not visible.");
            if (previousExplosion is not null)
                Require(Changed(previousExplosion, pixels, blastBounds) > 500,
                    "Successive barrel explosion phases did not visibly evolve.");
            previousExplosion = pixels;
        }
        // Exercise foreground clipping at both side edges and the protected rail.
        foreach (float x in new[] { .04f, .96f })
        {
            byte[] pixels = await Capture(barrel with { Hazards = [explosion with
                { X = x, TargetX = x, Y = .88f, TargetY = .88f, Age = .20f }] },
                x < .5f ? "barrel-explosion-left-edge" : "barrel-explosion-right-edge");
            Require(Changed(plainBarrel!, pixels, blastBounds) > 500,
                "A barrel explosion disappeared at the field edge.");
        }
        byte[] expiredExplosion = await Capture(barrel with { Hazards = [explosion with { Age = .45f }] },
            "barrel-explosion-expired");
        Require(Changed(plainBarrel!, expiredExplosion, new(0, 0, 900, 1600)) == 0,
            "An expired barrel explosion left a visible effect or damage-radius outline.");
        var commanded = Enumerable.Range(0, 8).Select(i => new StrykersRunEnemySnapshot(200 + i,
            .20f + (i % 4) * .065f, .44f + i / 4 * .085f, i == 5 ? StrykersRunEnemyKind.Brute : StrykersRunEnemyKind.Runner,
            6, 6, i * .12f)).ToArray();
        await Capture(baseline with { Level = 2, Boss = Boss(StrykersRunBossKind.SwarmCommander), Enemies = commanded }, "commander-minion-wave");

        var storm = baseline with { Level = 3, Boss = Boss(StrykersRunBossKind.StormRevenant) };
        await CheckStorm(storm);

        var skin = baseline with { Level = 4, Boss = Boss(StrykersRunBossKind.SkinWalker) };
        var tongue = Hazard(StrykersRunHazardKind.Tongue, .5f, .30f, .35f, .78f, .42f, .75f, 1.05f, .018f);
        byte[] tongueWarning = await Capture(skin with { Hazards = [tongue] }, "tongue-warning");
        byte[] tongueStrike = await Capture(skin with { Hazards = [tongue with { Age = .90f }] }, "tongue-strike");
        Require(Changed(tongueWarning, tongueStrike, new(0, 230, 900, 1200)) > 250, "Tongue warning and strike looked identical.");
        var fungal = skin with { Boss = skin.Boss!.Value with { Health = 90, Phase = 2, PhaseSeconds = .45f } };
        await Capture(fungal, "skin-shedding");
        byte[] transformed = await Capture(fungal with { Boss = fungal.Boss!.Value with { PhaseSeconds = 2 } }, "fungal-form");
        Require(Changed(Draw(skin), transformed, new(50, 210, 800, 510)) > 2000, "The half-health fungal transformation was not visible.");
        var gas = Hazard(StrykersRunHazardKind.PoisonGas, .58f, .70f, .60f, .78f, 2.48f, .8f, 4.5f, .09f);
        await Capture(fungal with { Boss = fungal.Boss!.Value with { PhaseSeconds = 3 }, Hazards = [gas] }, "fungal-poison-cloud");

        var giant = baseline with { Level = 5, Boss = Boss(StrykersRunBossKind.Colossus) };
        var stomp = Hazard(StrykersRunHazardKind.Stomp, .36f, .78f, .36f, .78f, .65f, 1.15f, 1.55f, .12f);
        byte[] swirl = await Capture(giant with { Hazards = [stomp] }, "colossus-stomp-warning");
        byte[] impact = await Capture(giant with { Hazards = [stomp with { Age = 1.27f }] }, "colossus-stomp-impact");
        Require(Changed(swirl, impact, new(0, 230, 900, 1200)) > 500, "The foot stomp did not visibly follow its warning.");
        await Capture(giant with { Boss = giant.Boss!.Value with { Health = 90, LeftLegHealth = 0, RightLegHealth = 90 } }, "colossus-one-leg-defeated");
        var victory = baseline with { Level = 5, Stage = StrykersRunStage.Complete, Boss = null,
            Phase = StrykersRunPhase.Victory, PhaseSeconds = 2.4f, Banner = "CAMPAIGN COMPLETE" };
        byte[] won = await Capture(victory, "campaign-victory");
        Require(Changed(plainBarrel!, won, new(0, 230, 900, 1000)) > 2000, "Victory did not receive its own presentation.");
        await Capture(baseline with { Level = 1, LevelSeconds = 60, Stage = StrykersRunStage.Intermission,
            StageSeconds = .8f, Banner = "LEVEL 1 CLEAR · survivors advance" }, "level-clear");
        return new(images.ToArray(), frames, true, true, true, true, true, true, true, true, true, true,
            true, true, true, true, true);

        async Task CheckStorm(StrykersRunSnapshot standing)
        {
            byte[] clear = Draw(standing);
            var cloud = Hazard(StrykersRunHazardKind.StormCloud, .5f, .38f, .5f, 1.04f,
                .45f, .75f, 4.35f, .085f);
            var protectedHud = new Rect(0, 0, 900, 215);
            var combat = new Rect(110, 235, 680, 1190);
            byte[] charging = await Capture(standing with { Hazards = [cloud] }, "storm-cloud-charge");
            Require(Changed(clear, charging, combat) > 500, "The summoned storm cloud did not appear.");
            byte[] rain = await Capture(standing with { Hazards = [cloud with { Age = 1.1f }] }, "storm-cloud-rain");
            Require(Changed(charging, rain, combat) > 500, "The charged cloud did not begin raining lightning.");
            byte[]? previousLane = null;
            foreach (float lane in new[] { .24f, .76f, .5f })
            {
                byte[] pixels = await Capture(standing with { Hazards = [cloud with
                    { X = lane, TargetX = lane, Y = .60f, Age = 2 }] }, $"storm-cloud-lane-{(int)(lane * 100)}");
                Require(Changed(clear, pixels, protectedHud) == 0, "Storm clouds obscured the boss HUD.");
                if (previousLane is not null)
                    Require(Changed(previousLane, pixels, combat) > 2000, "Storm clouds did not occupy distinct lanes.");
                previousLane = pixels;
            }
            byte[] near = await Capture(standing with { Hazards = [cloud with { Y = .83f, Age = 3.2f }] },
                "storm-cloud-near-player");
            Require(Changed(rain, near, combat) > 2000, "A storm cloud did not visibly travel toward the player.");

            var spider = new StrykersRunEnemySnapshot(501, .5f, .66f, StrykersRunEnemyKind.ElectricSpider, 32, 32, 0);
            byte[] fullSpider = await Capture(standing with { Enemies = [spider] }, "electric-spider-full-health");
            Require(Changed(clear, fullSpider, combat) > 1200, "The electric spider was not visible.");
            byte[] hurtSpider = await Capture(standing with { Enemies = [spider with { Health = 12 }] }, "electric-spider-hurt");
            Require(Changed(fullSpider, hurtSpider, combat) > 100, "The spider's HP bar did not visibly lose health.");
            byte[]? previousWalk = null;
            for (int frame = 0; frame < 8; frame++)
            {
                byte[] walking = await Capture(standing with { Enemies = [spider with { AnimationPhase = frame / 8f }] },
                    $"electric-spider-walk-{frame:00}");
                if (previousWalk is not null)
                    Require(Changed(previousWalk, walking, new(300, 830, 300, 300)) > 350,
                        "The electric spider's walking legs did not visibly change.");
                previousWalk = walking;
            }
            var returning = Hazard(StrykersRunHazardKind.ReturnLightning, .69f, .67f,
                StrykersRunGame.BossX, StrykersRunGame.BossY, .08f, .65f, .90f, .02f);
            byte[]? previousReturn = null;
            foreach (float age in new[] { .08f, .25f, .44f, .60f, .68f, .82f })
            {
                byte[] pixels = await Capture(standing with { Hazards = [returning with { Age = age }] },
                    $"storm-return-{(int)(age * 100):00}");
                Require(Changed(clear, pixels, combat) > 150, "The returned lightning or its impact was not visible.");
                Require(Changed(clear, pixels, protectedHud) == 0, "Returned lightning obscured the boss HUD.");
                if (previousReturn is not null)
                    Require(Changed(previousReturn, pixels, combat) > 250,
                        "Returned lightning did not advance from the defeated spider toward the boss.");
                previousReturn = pixels;
            }
            await Capture(standing with { Hazards = [cloud with { X = .24f, TargetX = .24f, Y = .68f, Age = 2.7f },
                returning with { Age = .29f }], Enemies = [spider with { X = .76f, Health = 21, AnimationPhase = .43f }] },
                "storm-full-battle");
            var paused = standing with { Phase = StrykersRunPhase.WaitingForInput,
                Enemies = [spider], Hazards = [cloud with { Y = .57f, Age = 1.8f },
                    returning with { X = .5f, Age = .25f }] };
            Require(Changed(Draw(paused, 13), Draw(paused, 42), new(265, 230, 370, 1000)) == 0,
                "Storm clouds, spider legs or returned lightning advanced while input was paused.");

            var crater = new StrykersRunCraterSnapshot(501, .5f, .64f, 1);
            byte[] freshCrater = await Capture(standing with { Craters = [crater] }, "storm-crater-fresh");
            byte[] oldCrater = await Capture(standing with { Craters = [crater with { Age = 180 }] }, "storm-crater-persistent");
            Require(Changed(clear, oldCrater, combat) > 1500,
                "The charged crater disappeared instead of remaining on the road.");
            Require(Changed(freshCrater, oldCrater, combat) > 150,
                "The crater's smoke and electric rim did not animate.");
            byte[] scrolledCrater = await Capture(standing with { Craters = [crater with { Y = .74f }] },
                "storm-crater-scrolled-forward");
            Require(Changed(freshCrater, scrolledCrater, combat) > 1500,
                "The crater did not visibly move forward with the road.");
            await Capture(standing with { Craters = [crater with { Y = .92f }] }, "storm-crater-crossing-rail");
            // At the exact cutoff the renderer still draws the crater: these
            // frames verify that the complete textured footprint and every
            // smoke phase have physically passed below the protected rail.
            for (int variant = 0; variant < 4; variant++)
            for (int phase = 0; phase < 12; phase++)
                Require(Changed(clear, Draw(standing with { Craters = [crater with
                    { Id = 500 + variant, Y = StrykersRunGame.StormCraterCullY, Age = phase / 3.6f }] }),
                    new(0, 0, 900, 1600)) == 0,
                    "A crater or smoke phase remained onscreen at the model's culling threshold.");
            Require(Changed(clear, Draw(standing with { Craters = [crater with { Y = 1.061f }] }),
                new(0, 0, 900, 1600)) == 0, "An offscreen crater or its smoke remained visible.");
            var pausedCrater = paused with { Craters = [crater] };
            Require(Changed(Draw(pausedCrater, 13), Draw(pausedCrater, 42), new(265, 230, 370, 1000)) == 0,
                "The crater's smoke or charge advanced while the run was paused.");
        }

        async Task CheckBarrelThrow(StrykersRunSnapshot standing)
        {
            byte[] idle = Draw(standing);
            byte[]? previous = null;
            var arms = new Rect(265, 230, 370, 230);
            var protectedHud = new Rect(0, 0, 900, 214);
            float release = StrykersRunGame.BarrelThrowReleaseProgress;
            for (int frame = 0; frame < 8; frame++)
            {
                float progress = frame < 4 ? (frame + .25f) / 4 * release
                    : release + (frame - 4 + .25f) / 4 * (1 - release);
                var state = standing with { Boss = standing.Boss!.Value with { BarrelThrowProgress = progress } };
                byte[] pose = await Capture(state, $"warden-throw-{frame:00}");
                if (previous is not null)
                    Require(Changed(previous, pose, arms) > 350,
                        "The Warden's next throw pose did not visibly articulate the arms.");
                Require(Changed(idle, pose, protectedHud) == 0,
                    "The throwing arms or held barrel obscured the stationary boss HUD.");
                previous = pose;
            }

            var released = standing with { Boss = standing.Boss!.Value with { BarrelThrowProgress = release } };
            var projectile = Hazard(StrykersRunHazardKind.Barrel, .18f, StrykersRunGame.BossY,
                .18f, StrykersRunGame.SquadY, 0, 0, 3, .065f);
            byte[] leftRelease = await Capture(released with { Hazards = [projectile] }, "warden-release-left");
            byte[] rightRelease = await Capture(released with { Hazards = [projectile with { X = .82f, TargetX = .82f }] },
                "warden-release-right");
            Require(Changed(leftRelease, rightRelease, new(0, 0, 900, 1600)) == 0,
                "A newly thrown barrel teleported to the aimed lane instead of leaving the Warden's hands.");
            Require(Changed(Draw(released), leftRelease, arms) > 250,
                "The released barrel was not visible at the Warden's hands.");
            Require(Changed(idle, leftRelease, protectedHud) == 0,
                "The released barrel or flames obscured the stationary boss HUD.");
            await Capture(standing with { Boss = standing.Boss!.Value with { BarrelThrowProgress = release - .001f } },
                "warden-last-held-barrel");
            foreach (float age in new[] { .08f, .18f })
                await Capture(standing with
                {
                    Boss = standing.Boss!.Value with { BarrelThrowProgress = release + age },
                    Hazards = [projectile with { Age = age,
                        Y = StrykersRunGame.BossY + (StrykersRunGame.SquadY - StrykersRunGame.BossY) * age / 2.35f }]
                }, $"warden-release-{(int)(age * 100):000}");

            var rolling = projectile with { X = .5f, TargetX = .5f, Y = .53f, Age = .65f };
            byte[]? previousFlame = null;
            for (int frame = 0; frame < 8; frame++)
            {
                byte[] flame = await Capture(standing with { Hazards = [rolling with { Age = rolling.Age + frame / 13f }] },
                    $"barrel-flames-{frame:00}");
                if (previousFlame is not null)
                    Require(Changed(previousFlame, flame, new(340, 650, 220, 220)) > 250,
                        "The burning barrel's flame silhouettes did not evolve.");
                Require(Changed(idle, flame, protectedHud) == 0,
                    "The barrel flame loop obscured the stationary boss HUD.");
                previousFlame = flame;
            }

            var paused = released with { Phase = StrykersRunPhase.WaitingForInput, Hazards = [rolling] };
            byte[] pauseBefore = Draw(paused, 13), pauseAfter = Draw(paused, 42);
            Require(Changed(pauseBefore, pauseAfter, new(265, 230, 370, 670)) == 0,
                "The Warden's arms, projectile or flames advanced while the simulation was paused.");
        }

        async Task CheckEntrance(StrykersRunSnapshot standing)
        {
            byte[]? firstEntrance = null;
            int previousBottom = -1;
            var bodyBounds = new Rect(120, 248, 660, 452);
            var roadBounds = new Rect(400, 760, 100, 180);
            foreach (float progress in new[] { 0f, .35f, .70f, 1f })
            {
                float seconds = progress * StrykersRunGame.BossEntranceSeconds;
                var state = standing with
                {
                    Stage = progress < 1 ? StrykersRunStage.BossEntrance : StrykersRunStage.Boss,
                    StageSeconds = progress < 1 ? seconds : 0,
                    RunSeconds = standing.RunSeconds + seconds,
                    Boss = standing.Boss!.Value with
                    {
                        Y = StrykersRunGame.BossEntranceStartY +
                            (StrykersRunGame.BossY - StrykersRunGame.BossEntranceStartY) * progress,
                        EntranceProgress = progress, AnimationPhase = seconds % 1,
                        PhaseSeconds = 0, AttackSeconds = 0
                    }
                };
                byte[] empty = Draw(state with { Boss = null });
                byte[] pixels = await Capture(state,
                    $"level-{standing.Level}-boss-entrance-{(int)(progress * 100):000}");
                int changed = Changed(empty, pixels, bodyBounds);
                int bottom = ChangedBottom(empty, pixels, bodyBounds);
                if (progress == 0)
                    Require(changed == 0, "A boss body or shadow appeared before entering below the HUD.");
                else
                {
                    Require(changed > 500, "An entering boss was not visible below the HUD.");
                    Require(bottom > previousBottom + 12, "A boss did not visibly advance down the road during its entrance.");
                }
                if (firstEntrance is not null)
                    // Native compositing can round one channel by one level even
                    // on a stationary road; real travel changes its texture edges.
                    Require(Changed(firstEntrance, pixels, roadBounds, channelTolerance: 1) == 0,
                        "The road moved during a boss entrance.");
                firstEntrance ??= pixels;
                previousBottom = bottom;
            }
        }

        byte[] Draw(StrykersRunSnapshot state, double elapsedSeconds = 13)
        {
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));
                renderer.Draw(drawing, new(0, 0, 900, 1600), state, elapsedSeconds);
            }
            frames++;
            var pixels = target.GetPixelBytes();
            for (int i = 1442 * 900 * 4; i < pixels.Length; i += 4)
                Require(pixels[i] == 0 && pixels[i + 1] == 0 && pixels[i + 2] == 0,
                    "A boss, attack or ending obscured the protected input strip.");
            return pixels;
        }
        async Task<byte[]> Capture(StrykersRunSnapshot state, string name)
        {
            byte[] pixels = Draw(state);
            string path = Path.Combine(directory, name + ".png");
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png).AsTask();
            images.Add(path);
            return pixels;
        }
    }

    private static StrykersRunBossSnapshot Boss(StrykersRunBossKind kind) => new(kind, .5f, .30f,
        200, 200, 1, 2, .3f, .3f, kind == StrykersRunBossKind.Colossus ? 100 : 0,
        kind == StrykersRunBossKind.Colossus ? 100 : 0);
    private static StrykersRunHazardSnapshot Hazard(StrykersRunHazardKind kind, float x, float y,
        float targetX, float targetY, float age, float warning, float duration, float radius) =>
        new(100, kind, x, y, targetX, targetY, age, warning, duration, radius);
    private static int Changed(byte[] a, byte[] b, Rect bounds, int channelTolerance = 0)
    {
        int count = 0;
        for (int y = (int)bounds.Y; y < (int)bounds.Bottom; y++)
        for (int x = (int)bounds.X; x < (int)bounds.Right; x++)
        {
            int i = (y * 900 + x) * 4;
            if (Math.Abs(a[i] - b[i]) > channelTolerance ||
                Math.Abs(a[i + 1] - b[i + 1]) > channelTolerance ||
                Math.Abs(a[i + 2] - b[i + 2]) > channelTolerance) count++;
        }
        return count;
    }
    private static int ChangedBottom(byte[] a, byte[] b, Rect bounds)
    {
        for (int y = (int)bounds.Bottom - 1; y >= (int)bounds.Y; y--)
        {
            int changed = 0;
            for (int x = (int)bounds.X; x < (int)bounds.Right; x++)
            {
                int i = (y * 900 + x) * 4;
                if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) changed++;
            }
            // Ignore isolated antialiasing pixels when measuring the visible lower edge.
            if (changed >= 20) return y;
        }
        return -1;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
#endif
