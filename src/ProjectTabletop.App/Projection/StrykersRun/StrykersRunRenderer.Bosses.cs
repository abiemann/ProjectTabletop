using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection.StrykersRun;

internal sealed partial class StrykersRunRenderer
{
    private readonly CanvasTextFormat _bossTitle = TextFormat(29);
    private readonly CanvasTextFormat _bossHint = TextFormat(22);
    private readonly CanvasTextFormat _victoryTitle = TextFormat(65);
    private int _bossHealth = -1, _bossMaximumHealth = -1;
    private string _bossHealthLabel = string.Empty;
    private static readonly string[] BossNames =
        ["BARREL WARDEN", "SWARM COMMANDER", "STORM REVENANT", "SKIN-WALKER", "COLOSSUS"];
    private static readonly string[] BossHints =
        [string.Empty, "BREAK THROUGH HIS MINION WAVES", "MOVE WHEN THE LIGHTNING LOCKS ON",
         "WATCH THE TONGUE", "SHOOT THE LEGS · AVOID THE RED SWIRLS"];

    private static int BossCell(StrykersRunBossSnapshot boss) => boss.Kind switch
    {
        StrykersRunBossKind.BarrelWarden => 0,
        StrykersRunBossKind.SwarmCommander => 1,
        StrykersRunBossKind.StormRevenant => 2,
        StrykersRunBossKind.SkinWalker => boss.Phase == 2 ? 4 : 3,
        _ => 5
    };

    private static Color BossColor(StrykersRunBossSnapshot boss) => boss.Kind switch
    {
        StrykersRunBossKind.BarrelWarden => Ink(255, 163, 62),
        StrykersRunBossKind.SwarmCommander => Ink(244, 186, 95),
        StrykersRunBossKind.StormRevenant => Ink(118, 212, 255),
        StrykersRunBossKind.SkinWalker => boss.Phase == 2 ? Ink(156, 245, 78) : Ink(238, 126, 156),
        _ => Ink(255, 100, 70)
    };

    private void DrawCampaignHud(CanvasDrawingSession ds, StrykersRunSnapshot state)
    {
        if (state.Stage == StrykersRunStage.Running)
        {
            ds.FillRectangle(new Rect(36, 124, Width - 72, 4), Ink(23, 43, 50));
            ds.FillRectangle(new Rect(36, 124, (Width - 72) * Math.Clamp(state.LevelSeconds / StrykersRunGame.LevelDurationSeconds, 0, 1), 4), Ink(102, 223, 234));
        }
        if (state.Boss is { } boss)
        {
            if (_bossHealth != boss.Health || _bossMaximumHealth != boss.MaxHealth)
            {
                _bossHealth = boss.Health; _bossMaximumHealth = boss.MaxHealth;
                _bossHealthLabel = $"{boss.Health:N0} / {boss.MaxHealth:N0}";
            }
            int index = Math.Clamp(state.Level - 1, 0, 4);
            Color accent = BossColor(boss);
            string hint = boss.Kind == StrykersRunBossKind.SkinWalker && boss.Phase == 2
                ? "KEEP CLEAR OF THE POISON CLOUDS" : BossHints[index];
            ds.FillRoundedRectangle(new Rect(55, 139, 790, hint.Length > 0 ? 105 : 75), 9, 9, Ink(10, 17, 23, 242));
            Text(ds, boss.Kind == StrykersRunBossKind.SkinWalker && boss.Phase == 2 ? "FUNGAL HORROR" : BossNames[index],
                new Rect(75, 141, 750, 35), accent, _bossTitle);
            ds.FillRoundedRectangle(new Rect(79, 180, 742, 23), 5, 5, Ink(58, 34, 39));
            float health = HealthFraction(boss.Health, boss.MaxHealth);
            if (health > 0) ds.FillRoundedRectangle(new Rect(79, 180, 742 * health, 23), 5, 5, accent);
            Text(ds, _bossHealthLabel, new Rect(80, 178, 740, 26), Ink(255, 255, 236), _bossHint);
            if (hint.Length > 0)
                Text(ds, hint, new Rect(74, 207, 752, 30), Ink(211, 224, 216), _bossHint);
        }
        else if (state.Stage == StrykersRunStage.Intermission)
        {
            ds.FillRoundedRectangle(new Rect(119, 411, 662, 121), 10, 10, Ink(11, 35, 34, 241));
            ds.DrawRoundedRectangle(new Rect(119, 411, 662, 121), 10, 10, Ink(124, 249, 202), 2);
            Text(ds, "BOSS DEFEATED", new Rect(131, 420, 638, 58), Ink(160, 255, 214), _title);
            Text(ds, "SURVIVORS ADVANCE · NEXT LEVEL", new Rect(131, 482, 638, 37), Ink(239, 230, 196), _bossHint);
        }
    }

    private static Rect BossWorldClip(StrykersRunBossSnapshot boss)
    {
        float top = boss.Kind == StrykersRunBossKind.BarrelWarden ? 214 : 244;
        return new(0, top, Width, RailTop - top);
    }

    private static void DrawBossShadow(CanvasDrawingSession ds, StrykersRunBossSnapshot boss)
    {
        using var hudClip = ds.CreateLayer(1, BossWorldClip(boss));
        if (boss.Kind == StrykersRunBossKind.Colossus)
        {
            for (int leg = 0; leg < 2; leg++)
            {
                float x = leg == 0 ? StrykersRunGame.BossLeftLegX : StrykersRunGame.BossRightLegX;
                ColossusEntranceStep(boss.EntranceProgress, leg, out float y, out float lift, out float scale);
                var foot = Point(x, y);
                float shadowScale = scale * (1 - lift / 240);
                ds.FillEllipse(foot.X, foot.Y, 94 * shadowScale, 24 * shadowScale, Ink(0, 0, 0, 176));
            }
            return;
        }
        var point = Point(boss.X, boss.Y);
        float depth = DepthScale(boss.Y);
        ds.FillEllipse(point.X + 15 * depth, point.Y, 125 * depth, 27 * depth, Ink(0, 0, 0, 161));
    }

    private void DrawBoss(CanvasDrawingSession ds, StrykersRunSnapshot state)
    {
        if (state.Boss is not { } boss) return;
        // Enter from behind the HUD, keeping even the giant's thighs clear of
        // stationary text. The same boundary remains after arrival.
        using var hudClip = ds.CreateLayer(1, BossWorldClip(boss));
        if (boss.Kind == StrykersRunBossKind.Colossus) { DrawColossus(ds, state, boss); return; }
        var point = Point(boss.X, boss.Y);
        Rect source = _bossCells[BossCell(boss)];
        float depth = DepthScale(boss.Y), clock = boss.AnimationPhase * MathF.Tau;
        float breath = MathF.Sin(clock) * .014f;
        float size = (boss.Kind == StrykersRunBossKind.BarrelWarden ? 335 : 325) * depth;
        float width = size * (float)(source.Width / source.Height);
        float lean = MathF.Sin(clock * .5f) * .017f;
        if (boss.Kind == StrykersRunBossKind.SkinWalker && boss.Phase == 2)
            DrawShedSkin(ds, boss, point, depth);
        using (var batch = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear))
        {
            float walking = Math.Clamp((1 - boss.EntranceProgress) / .12f, 0, 1);
            if (walking > 0)
            {
                float cycle = boss.EntranceProgress * 4;
                int frame = (int)(cycle * 4) % 4;
                // The carrier's neutral passing pose connects its two strides.
                if (boss.Kind == StrykersRunBossKind.BarrelWarden)
                    frame = frame switch { 2 => 3, 3 => 1, _ => frame };
                Rect walk = _bossWalkCells[(int)boss.Kind * 4 + frame];
                float step = MathF.Sin(cycle * MathF.Tau);
                Vector2 walkingPoint = point + new Vector2(step * 4, -Math.Abs(step) * 5) * depth * walking;
                DrawBossPose(batch, _images!.Image("boss-walk.png")!, walk, walkingPoint,
                    size, 0, step * .018f * walking, walking);
            }
            if (walking < 1)
            {
                if (boss.Kind == StrykersRunBossKind.BarrelWarden)
                    DrawWardenThrowPose(batch, boss, point, size, 1 - walking);
                else
                    DrawBossPose(batch, _images!.Image("bosses.png")!, source, point, size, breath, lean, 1 - walking);
            }
        }
        if (boss.Kind == StrykersRunBossKind.StormRevenant)
        {
            float charge = 0;
            foreach (var hazard in state.Hazards)
                if (hazard.Kind == StrykersRunHazardKind.Lightning && hazard.IsWarning)
                    charge = Math.Max(charge, hazard.Age / Math.Max(.01f, hazard.WarningSeconds));
            for (int arc = 0; arc < 5; arc++)
            {
                float angle = clock * 2.1f + arc * MathF.Tau / 5;
                Vector2 center = point - new Vector2(0, size * .53f);
                Vector2 from = center + new Vector2(MathF.Cos(angle) * width * .31f, MathF.Sin(angle) * size * .3f);
                Vector2 to = center + new Vector2(MathF.Cos(angle + .74f) * width * .39f, MathF.Sin(angle + .74f) * size * .37f);
                DrawBolt(ds, from, to, arc * 37 + (int)(clock * 17), .5f + charge * 1.4f,
                    Ink(108, 203, 255, (byte)(75 + charge * 175)));
            }
        }
        if (boss.Kind == StrykersRunBossKind.SwarmCommander && boss.EntranceProgress >= 1)
        {
            float pulse = Fraction(boss.AttackSeconds * .8f);
            var center = point - new Vector2(0, size * .48f);
            float radius = (18 + pulse * 49) * depth;
            ds.DrawEllipse(center.X, center.Y, radius, radius * .6f, Ink(255, 187, 71, (byte)((1 - pulse) * 156)), 2);
        }
    }

    private static void DrawBossPose(CanvasSpriteBatch batch, CanvasBitmap bitmap, Rect source,
        Vector2 point, float size, float breath, float lean, float opacity)
    {
        float scale = size / (float)source.Height;
        var transform = Matrix3x2.CreateTranslation(-(float)source.Width / 2, -(float)source.Height) *
            Matrix3x2.CreateScale(scale, scale * (1 + breath)) *
            Matrix3x2.CreateRotation(lean) * Matrix3x2.CreateTranslation(point);
        batch.DrawFromSpriteSheet(bitmap, transform, source, new Vector4(1, 1, 1, opacity));
    }

    // Full atlas cells share a body scale and planted-foot anchor. In particular,
    // the overhead pose must not shrink just because its raised arms are taller.
    // Measured in the 1774 x 887 atlas, relative to each integer source cell.
    // Its second row has more bottom padding; keep the actual boots on the road.
    private const float WardenReadyHeight = 283;
    private static readonly Vector2[] WardenFootAnchors =
        [new(234, 402), new(259, 402), new(225, 402), new(207.5f, 403),
         new(235, 337), new(247.5f, 336), new(215, 336), new(213.5f, 336)];
    private static readonly Vector2 WardenReleaseAnchor = new(188, 279);
    private const float WardenHeldBarrelHeight = 98;

    private void DrawWardenThrowPose(CanvasSpriteBatch batch, StrykersRunBossSnapshot boss,
        Vector2 point, float size, float opacity)
    {
        float progress = boss.BarrelThrowProgress;
        float release = StrykersRunGame.BarrelThrowReleaseProgress;
        int frame = progress < 0 ? 0 : progress < release
            ? Math.Min(3, (int)(progress / release * 4))
            : 4 + Math.Min(3, (int)((progress - release) / (1 - release) * 4));
        Rect source = _bossThrowCells[frame];
        float scale = size / WardenReadyHeight;
        var transform = Matrix3x2.CreateTranslation(-WardenFootAnchors[frame]) *
            Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(point);
        // One opaque articulated pose at a time: blending bodies doubles arms
        // and can leave a second barrel visible after the projectile is released.
        batch.DrawFromSpriteSheet(_bossThrow!, transform, source, new Vector4(1, 1, 1, opacity));
    }

    private static void ColossusEntranceStep(float progress, int leg, out float y, out float lift, out float scale)
    {
        float steps = Math.Clamp(progress, 0, 1) * 4;
        float stride = Math.Clamp(steps % 2 - leg, 0, 1);
        float travel = (MathF.Floor(steps / 2) + stride * stride * (3 - 2 * stride)) * .5f;
        y = StrykersRunGame.BossEntranceStartY +
            (StrykersRunGame.BossLegY - StrykersRunGame.BossEntranceStartY) * travel;
        scale = .68f + travel * .32f;
        lift = MathF.Sin(stride * MathF.PI) * 64 * scale;
    }

    private void DrawShedSkin(CanvasDrawingSession ds, StrykersRunBossSnapshot boss, Vector2 point, float depth)
    {
        float age = boss.PhaseSeconds;
        if (age >= 4) return;
        Rect skin = _bossCells[3];
        float fall = Math.Clamp(age / .95f, 0, 1);
        float fade = Math.Clamp((4 - age) / 1.2f, 0, 1);
        using var pieces = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear);
        for (int piece = 0; piece < 6; piece++)
        {
            int column = piece % 2, row = piece / 2;
            var source = new Rect(skin.X + column * skin.Width / 2, skin.Y + row * skin.Height / 3,
                skin.Width / 2, skin.Height / 3);
            float size = 325 * depth, width = size * (float)(skin.Width / skin.Height);
            float targetX = (column == 0 ? -1 : 1) * (26 + row * 21) * depth;
            float x = point.X + (column == 0 ? -width * .25f : width * .25f) + targetX * fall;
            float startY = point.Y - size + (row + .5f) * size / 3;
            float y = startY + (point.Y + (row - 1) * 11 * depth - startY) * fall * fall;
            var transform = Matrix3x2.CreateTranslation(-(float)source.Width / 2, -(float)source.Height / 2) *
                Matrix3x2.CreateScale(width / (float)skin.Width, size / (float)skin.Height * (1 - fall * .76f)) *
                Matrix3x2.CreateRotation((column == 0 ? -1 : 1) * fall * (.4f + row * .13f)) *
                Matrix3x2.CreateTranslation(x, y);
            pieces.DrawFromSpriteSheet(_images!.Image("bosses.png")!, transform, source,
                new Vector4(.65f, .55f, .48f, fade));
        }
    }

    private void DrawColossus(CanvasDrawingSession ds, StrykersRunSnapshot state, StrykersRunBossSnapshot boss)
    {
        Rect source = _bossCells[5];
        for (int leg = 0; leg < 2; leg++)
        {
            float x = leg == 0 ? StrykersRunGame.BossLeftLegX : StrykersRunGame.BossRightLegX;
            int health = leg == 0 ? boss.LeftLegHealth : boss.RightLegHealth;
            ColossusEntranceStep(boss.EntranceProgress, leg, out float y, out float lift, out float scale);
            var point = Point(x, y);
            bool stomping = false;
            foreach (var hazard in state.Hazards)
                if (hazard.Kind == StrykersRunHazardKind.Stomp &&
                    StrykersRunFormation.StompingLeg(boss.LeftLegHealth, boss.RightLegHealth, hazard.TargetX) == leg)
                {
                    lift = hazard.IsWarning ? Math.Clamp(hazard.Age / Math.Max(.01f, hazard.WarningSeconds), 0, 1) * 78 : 0;
                    stomping = !hazard.IsWarning;
                }
            var cell = new Rect(source.X + leg * source.Width / 2, source.Y, source.Width / 2, source.Height);
            // The thighs continue beyond the viewport: only enormous legs are seen.
            float height = 654 * scale, width = Math.Min(264 * scale, height * (float)(cell.Width / cell.Height));
            if (health <= 0) height *= .78f;
            if (!stomping)
                using (var batch = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear))
                    batch.DrawFromSpriteSheet(_images!.Image("bosses.png")!,
                        new Rect(point.X - width / 2, point.Y - height - lift, width, height), cell,
                        health > 0 ? Vector4.One : new Vector4(.46f, .42f, .40f, 1));
            if (health <= 0 || stomping || boss.EntranceProgress < 1) continue;
            float fraction = HealthFraction(health, Math.Max(1, boss.MaxHealth / 2));
            ds.FillRoundedRectangle(new Rect(point.X - 79, point.Y + 17, 158, 12), 4, 4, Ink(52, 22, 20));
            ds.FillRoundedRectangle(new Rect(point.X - 79, point.Y + 17, 158 * fraction, 12), 4, 4, Ink(255, 124, 68));
            Text(ds, "SHOOT", new Rect(point.X - 78, point.Y + 30, 156, 26), Ink(255, 202, 161), _bossHint);
        }
    }

    private static void DrawBossGround(CanvasDrawingSession ds, StrykersRunSnapshot state)
    {
        foreach (var hazard in state.Hazards)
        {
            if (hazard.Kind == StrykersRunHazardKind.Stomp)
            {
                float progress = Math.Clamp(hazard.Age / Math.Max(.01f, hazard.WarningSeconds), 0, 1);
                float alpha = hazard.IsWarning ? .60f + .35f * progress : Math.Clamp(1 - (hazard.Age - hazard.WarningSeconds) / .7f, 0, 1);
                DrawWorldRing(ds, hazard.TargetX, hazard.TargetY, hazard.Radius, Ink(255, 58, 44, (byte)(alpha * 245)), 4);
                for (int arm = 0; arm < 3; arm++)
                {
                    Vector2 previous = Point(hazard.TargetX, hazard.TargetY);
                    for (int step = 1; step <= 22; step++)
                    {
                        float t = step / 22f, angle = arm * MathF.Tau / 3 + t * 5 - hazard.Age * 4;
                        var next = Point(hazard.TargetX + MathF.Cos(angle) * hazard.Radius * t,
                            hazard.TargetY + MathF.Sin(angle) * hazard.Radius * t);
                        ds.DrawLine(previous, next, Ink(255, (byte)(38 + progress * 60), 32, (byte)(alpha * 225)), 3 + t * 4);
                        previous = next;
                    }
                }
            }
            else if (hazard.Kind is StrykersRunHazardKind.Lightning or StrykersRunHazardKind.Tongue && hazard.IsWarning)
            {
                Color color = hazard.Kind == StrykersRunHazardKind.Lightning ? Ink(122, 210, 255, 154) : Ink(250, 102, 149, 142);
                Vector2 from = Point(hazard.X, hazard.Y), to = Point(hazard.TargetX, hazard.TargetY);
                for (int dash = 0; dash < 18; dash++)
                    ds.DrawLine(Vector2.Lerp(from, to, dash / 18f), Vector2.Lerp(from, to, (dash + .58f) / 18f), color, 3);
                Vector2 startOffset = new(hazard.Radius * Width * DepthScale(hazard.Y), 0);
                Vector2 endOffset = new(hazard.Radius * Width * DepthScale(hazard.TargetY), 0);
                ds.DrawLine(from - startOffset, to - endOffset, color, 1.4f);
                ds.DrawLine(from + startOffset, to + endOffset, color, 1.4f);
            }
            else if (hazard.Kind == StrykersRunHazardKind.PoisonGas && hazard.IsWarning)
                DrawWorldRing(ds, hazard.TargetX, hazard.TargetY, hazard.Radius, Ink(154, 236, 62, 164), 3);
        }
    }

    private static void DrawWorldRing(CanvasDrawingSession ds, float x, float y, float radius, Color color, float thickness)
    {
        Vector2 previous = Point(x + radius, y);
        for (int segment = 1; segment <= 48; segment++)
        {
            float angle = segment * MathF.Tau / 48;
            Vector2 next = Point(x + MathF.Cos(angle) * radius, y + MathF.Sin(angle) * radius);
            ds.DrawLine(previous, next, color, thickness);
            previous = next;
        }
    }

    private void DrawBossHazards(CanvasDrawingSession ds, StrykersRunSnapshot state)
    {
        foreach (var hazard in state.Hazards)
        {
            switch (hazard.Kind)
            {
                case StrykersRunHazardKind.Barrel: DrawBurningBarrel(ds, hazard); break;
                case StrykersRunHazardKind.BarrelBlast: DrawBarrelExplosion(ds, hazard); break;
                case StrykersRunHazardKind.Lightning:
                    if (!hazard.IsWarning)
                        DrawBolt(ds, Point(hazard.X, hazard.Y) - new Vector2(0, 125 * DepthScale(hazard.Y)),
                            Point(hazard.TargetX, hazard.TargetY), hazard.Id * 71 + (int)(hazard.Age * 35), 5.5f, Ink(162, 226, 255));
                    break;
                case StrykersRunHazardKind.Tongue: DrawTongue(ds, hazard); break;
                case StrykersRunHazardKind.PoisonGas: DrawPoisonGas(ds, hazard); break;
                case StrykersRunHazardKind.Stomp:
                    if (!hazard.IsWarning) DrawStomp(ds, hazard, state.Boss);
                    break;
            }
        }
    }

    private void DrawBurningBarrel(CanvasDrawingSession ds, StrykersRunHazardSnapshot hazard)
    {
        using var hudClip = ds.CreateLayer(1, new Rect(0, 214, Width, RailTop - 214));
        float thrown = Math.Clamp(hazard.Age / .30f, 0, 1);
        float depth = DepthScale(hazard.Y), bossScale = 335 * DepthScale(StrykersRunGame.BossY) / WardenReadyHeight;
        var releaseOffset = (WardenReleaseAnchor - WardenFootAnchors[3]) * bossScale;
        Vector2 release = Point(StrykersRunGame.BossX, StrykersRunGame.BossY) + releaseOffset;
        float releaseSize = WardenHeldBarrelHeight * bossScale;
        float size = releaseSize + (72 * depth - releaseSize) * thrown;
        Vector2 ground = Point(hazard.X, hazard.Y);
        Vector2 center = Vector2.Lerp(release, ground - new Vector2(0, size * .5f), thrown);
        center.Y -= MathF.Sin(thrown * MathF.PI) * 45 * depth;
        ds.FillEllipse(center.X + 6, ground.Y + 8, size * .70f, size * .25f, Ink(0, 0, 0, 161));
        using (var flames = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear))
        {
            float fireClock = hazard.Age * 13 + Noise(hazard.Id * 73) * _barrelFlameCells.Length;
            float fireGrowth = Math.Clamp(hazard.Age / .25f, 0, 1);
            fireGrowth = fireGrowth * fireGrowth * (3 - 2 * fireGrowth);
            // The carried fire stays compact at the hands, then billows as the
            // drum leaves him. Grow the flame silhouette rather than fading it.
            DrawBarrelFlame(flames, center + new Vector2(0, size * .35f),
                size * (1.05f + .80f * fireGrowth), size * (.90f + 1.60f * fireGrowth), fireClock, .94f);
            // The atlas rotates the drum around its cylinder, keeping its
            // silhouette and lighting stable instead of spinning a flat card.
            int frame = (int)(Math.Max(0, hazard.Age - .30f) * 6) % _barrelCells.Length;
            Rect source = _barrelCells[frame];
            float scale = size / _barrelFrameHeight;
            var transform = Matrix3x2.CreateTranslation(-(float)source.Width / 2, -(float)source.Height / 2) *
                Matrix3x2.CreateScale(scale) * Matrix3x2.CreateRotation((1 - thrown) * -.45f) *
                Matrix3x2.CreateTranslation(center);
            flames.DrawFromSpriteSheet(_barrels!, transform, source, Vector4.One);
            // Smaller, independently phased tongues curl over both rims while
            // leaving the detailed metal drum readable between them.
            DrawBarrelFlame(flames, center + new Vector2(-size * .43f, size * .34f),
                size * (.32f + .12f * fireGrowth), size * (.52f + .34f * fireGrowth), fireClock + 2.7f, .84f);
            DrawBarrelFlame(flames, center + new Vector2(size * .43f, size * .12f),
                size * (.29f + .09f * fireGrowth), size * (.44f + .29f * fireGrowth), fireClock + 5.1f, .78f);
            for (int spark = 0; spark < 6; spark++)
            {
                float age = Fraction(hazard.Age * (1.2f + spark * .11f) + spark * .173f);
                float x = center.X + MathF.Sin(spark * 2.4f + age * 4) * (14 + age * (6 + fireGrowth * 16)) * depth;
                float y = center.Y - size * .24f - age * (26 + fireGrowth * 78) * depth;
                float width = (3.8f - age * 2.2f) * depth;
                flames.DrawFromSpriteSheet(_particles!, new Rect(x, y, width, width * 2.7f), GlowCell,
                    new Vector4(1, .68f, .20f, (1 - age) * .95f));
            }
        }
    }

    private void DrawBarrelFlame(CanvasSpriteBatch batch, Vector2 basePoint, float width, float height,
        float clock, float opacity)
    {
        int frame = (int)clock % _barrelFlameCells.Length;
        float blend = Fraction(clock);
        var destination = new Rect(basePoint.X - width * .5f, basePoint.Y - height, width, height);
        // Adjacent flame silhouettes blend at a fixed base, unlike stretching
        // glow particles into a uniform column. Both layers retain real alpha.
        batch.DrawFromSpriteSheet(_barrelFlames!, destination, _barrelFlameCells[frame],
            new Vector4(1, 1, 1, opacity * (1 - blend)));
        batch.DrawFromSpriteSheet(_barrelFlames!, destination, _barrelFlameCells[(frame + 1) % _barrelFlameCells.Length],
            new Vector4(1, 1, 1, opacity * blend));
    }

    private void DrawBarrelExplosion(CanvasDrawingSession ds, StrykersRunHazardSnapshot hazard)
    {
        float progress = Math.Clamp(hazard.Age / Math.Max(.01f, hazard.Duration), 0, 1);
        if (progress >= 1) return;
        var center = Point(hazard.X, hazard.Y);
        float depth = DepthScale(hazard.Y), radius = hazard.Radius * Width * depth;
        using var blast = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear);
        float phase = progress * (_explosionCells.Length - 1);
        int frame = Math.Min((int)phase, _explosionCells.Length - 2);
        float blend = phase - frame;
        float fade = Math.Clamp((1 - progress) / .23f, 0, 1);
        float size = radius * 3.4f * (.78f + .22f * Math.Clamp(progress / .12f, 0, 1));
        var destination = new Rect(center.X - size * .5f,
            center.Y + radius * .30f - size * .9f - progress * 24 * depth, size, size);
        // Cross-fade detailed ignition, fire and soot frames at a stable world
        // anchor. All resources are loaded once; only bounded sprite draws run here.
        for (int layer = 0; layer < 2; layer++)
        {
            float opacity = (layer == 0 ? 1 - blend : blend) * fade;
            if (opacity > 0)
                blast.DrawFromSpriteSheet(_images!.Image("explosion.png")!, destination,
                    _explosionCells[frame + layer], new Vector4(1, 1, 1, opacity));
        }
        float age = hazard.Age;
        Rect barrel = _barrelCells[0];
        for (int shard = 0; shard < 6; shard++)
        {
            float seed = Noise(hazard.Id * 31 + shard * 17);
            var source = new Rect(barrel.X + barrel.Width * (.25f + shard % 3 * .13f),
                barrel.Y + barrel.Height * (.3f + shard / 3 * .17f), barrel.Width * .12f, barrel.Height * .12f);
            float scale = (8 + seed * 9) * depth / (float)source.Width;
            var point = center + new Vector2((seed - .5f) * age * 760,
                -age * (190 + seed * 180) + age * age * 720) * depth;
            var transform = Matrix3x2.CreateTranslation(-(float)source.Width / 2, -(float)source.Height / 2) *
                Matrix3x2.CreateScale(scale) * Matrix3x2.CreateRotation(seed * 6 + age * (seed - .5f) * 24) *
                Matrix3x2.CreateTranslation(point);
            blast.DrawFromSpriteSheet(_images!.Image("barrels.png")!, transform, source,
                new Vector4(.75f, .65f, .55f, fade));
        }
        for (int spark = 0; spark < 16; spark++)
        {
            float seed = Noise(hazard.Id + spark * 43), angle = spark * 2.39996f;
            float travel = age * (190 + seed * 540) * depth;
            var point = center + new Vector2(MathF.Cos(angle) * travel,
                MathF.Sin(angle) * travel * .55f - age * 120 * depth);
            var transform = Matrix3x2.CreateTranslation(-(float)GlowCell.Width / 2, -(float)GlowCell.Height / 2) *
                Matrix3x2.CreateScale((2 + seed * 2) * depth / (float)GlowCell.Width,
                    (8 + seed * 15) * depth / (float)GlowCell.Height) *
                Matrix3x2.CreateRotation(angle - MathF.PI / 2) * Matrix3x2.CreateTranslation(point);
            blast.DrawFromSpriteSheet(_particles!, transform, GlowCell,
                new Vector4(1, .57f + seed * .3f, .13f, (1 - progress) * fade));
        }
    }

    private static void DrawBolt(CanvasDrawingSession ds, Vector2 from, Vector2 to, int seed, float width, Color color)
    {
        Vector2 direction = to - from;
        float length = direction.Length();
        if (length < 1) return;
        Vector2 normal = new(-direction.Y / length, direction.X / length), previous = from;
        for (int segment = 1; segment <= 14; segment++)
        {
            float t = segment / 14f;
            var next = Vector2.Lerp(from, to, t) + normal * ((Noise(seed + segment * 43) - .5f) *
                Math.Min(46, length * .21f) * MathF.Sin(t * MathF.PI));
            ds.DrawLine(previous, next, Color.FromArgb((byte)(color.A * .24f), color.R, color.G, color.B), width * 4);
            ds.DrawLine(previous, next, color, width);
            ds.DrawLine(previous, next, Ink(234, 252, 255, color.A), Math.Max(.7f, width * .27f));
            previous = next;
        }
    }

    private static void DrawTongue(CanvasDrawingSession ds, StrykersRunHazardSnapshot hazard)
    {
        if (hazard.IsWarning) return;
        Vector2 from = Point(hazard.X, hazard.Y) - new Vector2(0, 265 * DepthScale(hazard.Y));
        Vector2 to = Point(hazard.TargetX, hazard.TargetY), direction = to - from;
        float length = direction.Length();
        if (length < 1) return;
        Vector2 normal = new(-direction.Y / length, direction.X / length), previous = from;
        for (int segment = 1; segment <= 20; segment++)
        {
            float t = segment / 20f;
            var next = Vector2.Lerp(from, to, t) + normal * (MathF.Sin(t * 10 - hazard.Age * 15) * 12 * MathF.Sin(t * MathF.PI));
            float thickness = (1 - t * .58f) * 19;
            ds.DrawLine(previous, next, Ink(57, 14, 34), thickness + 5);
            ds.DrawLine(previous, next, Ink(199, 73, 119), thickness);
            ds.DrawLine(previous - new Vector2(2, 0), next - new Vector2(2, 0), Ink(255, 162, 179), thickness * .24f);
            previous = next;
        }
        ds.FillEllipse(to.X, to.Y, 13, 18, Ink(231, 119, 149));
    }

    private void DrawPoisonGas(CanvasDrawingSession ds, StrykersRunHazardSnapshot hazard)
    {
        if (hazard.IsWarning) return;
        var center = Point(hazard.X, hazard.Y);
        float travel = Math.Clamp((hazard.Age - hazard.WarningSeconds) / 2, 0, 1);
        center.Y -= (1 - travel) * 254 * DepthScale(StrykersRunGame.BossY);
        float depth = DepthScale(hazard.Y), radius = hazard.Radius * Width * depth;
        float fade = Math.Clamp((hazard.Duration - hazard.Age) / .55f, 0, 1);
        using var cloud = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear);
        for (int puff = 0; puff < 15; puff++)
        {
            float angle = puff * 2.39996f + hazard.Age * .36f;
            float spread = radius * MathF.Sqrt(puff / 15f) * .75f;
            Vector2 position = center + new Vector2(MathF.Cos(angle) * spread, MathF.Sin(angle) * spread * 1.8f);
            float size = radius * (.87f + MathF.Sin(hazard.Age * 2 + puff) * .16f);
            cloud.DrawFromSpriteSheet(_particles!, new Rect(position.X - size, position.Y - size * 1.16f, size * 2, size * 2),
                SmokeCell, new Vector4(.34f + (puff % 3) * .15f, .67f + (puff % 2) * .15f, .07f, fade * .42f));
            if (puff % 3 != 0) continue;
            float drift = Fraction(hazard.Age * .6f + puff * .13f);
            cloud.DrawFromSpriteSheet(_particles!, new Rect(position.X, position.Y - drift * 95, 10, 15), GlowCell,
                new Vector4(.67f, 1, .16f, fade * (1 - drift)));
        }
    }

    private void DrawStomp(CanvasDrawingSession ds, StrykersRunHazardSnapshot hazard, StrykersRunBossSnapshot? boss)
    {
        float age = hazard.Age - hazard.WarningSeconds;
        float fade = Math.Clamp(1 - age / .72f, 0, 1);
        Vector2 center = Point(hazard.TargetX, hazard.TargetY);
        Rect titan = _bossCells[5];
        int leg = StrykersRunFormation.StompingLeg(boss?.LeftLegHealth ?? 1, boss?.RightLegHealth ?? 1, hazard.TargetX);
        var source = new Rect(titan.X + leg * titan.Width / 2, titan.Y, titan.Width / 2, titan.Height);
        float footY = StrykersRunFormation.StompFootY(hazard.TargetY, age);
        Vector2 footPoint = Point(hazard.TargetX, footY);
        float width = 280 * DepthScale(footY);
        double split = source.Height * .56;
        var upper = new Rect(source.X, source.Y, source.Width, split);
        var lower = new Rect(source.X, source.Y + split, source.Width, source.Height - split);
        float lowerHeight = width * (float)(lower.Height / lower.Width), joinY = footPoint.Y - lowerHeight;
        // Keep the ankle and toes at their original aspect ratio. Only the
        // upper leg extends off-screen, avoiding a floating, square thigh cut.
        using (var foot = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear))
        {
            var tint = new Vector4(1, .94f, .88f, fade);
            foot.DrawFromSpriteSheet(_images!.Image("bosses.png")!,
                new Rect(footPoint.X - width / 2, -80, width, joinY + 80), upper, tint);
            foot.DrawFromSpriteSheet(_images.Image("bosses.png")!,
                new Rect(footPoint.X - width / 2, joinY, width, lowerHeight), lower, tint);
        }
        DrawWorldRing(ds, hazard.TargetX, hazard.TargetY, hazard.Radius * (1 + age * 1.4f), Ink(255, 173, 112, (byte)(fade * 230)), 6 * fade + 1);
        using var debris = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear);
        for (int shard = 0; shard < 18; shard++)
        {
            float angle = shard * 2.39996f, speed = 110 + Noise(shard + hazard.Id) * 170;
            var point = center + new Vector2(MathF.Cos(angle) * age * speed, MathF.Sin(angle) * age * speed - age * 115 + age * age * 280);
            float size = 10 + Noise(shard * 37) * 16;
            debris.DrawFromSpriteSheet(_particles!, new Rect(point.X, point.Y, size, size * .64f), ShardCell,
                new Vector4(.72f, .58f, .43f, fade));
        }
    }

    private void DrawVictory(CanvasDrawingSession ds, StrykersRunSnapshot state)
    {
        float seconds = state.PhaseSeconds;
        using (var embers = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear))
            for (int spark = 0; spark < 42; spark++)
            {
                float age = Fraction(seconds * .21f + Noise(spark * 31));
                float x = Width * (.08f + Noise(spark * 17) * .84f);
                float y = 1010 - age * 740;
                float size = 3 + Noise(spark) * 6;
                embers.DrawFromSpriteSheet(_particles!, new Rect(x, y, size, size * 3), GlowCell,
                    new Vector4(.62f, 1, .76f, MathF.Sin(age * MathF.PI) * .9f));
            }
        ds.FillRoundedRectangle(new Rect(69, 483, 762, 225), 12, 12, Ink(11, 31, 33, 241));
        ds.DrawRoundedRectangle(new Rect(69, 483, 762, 225), 12, 12, Ink(128, 251, 198), 3);
        Text(ds, "CITY LIBERATED", new Rect(82, 493, 736, 88), Ink(171, 255, 204), _victoryTitle);
        Text(ds, "ALL FIVE BOSSES DEFEATED", new Rect(91, 591, 718, 43), Ink(249, 224, 172), _bossTitle);
        Text(ds, "STRYKER AND HIS SURVIVORS STAND TALL", new Rect(91, 644, 718, 35), Ink(207, 227, 221), _bossHint);
    }
}
