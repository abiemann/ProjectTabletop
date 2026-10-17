using System.Numerics;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection.StrykersRun;

internal sealed partial class StrykersRunRenderer
{
    // Body registration is measured once from the 1536 x 1024 painted atlas.
    // Its second row has different padding; aligning the eyes prevents a hop
    // between the two rows while retaining the changing eight-leg silhouette.
    private static readonly Vector2[] ElectricSpiderEyes =
        [new(201.79f, 280.81f), new(195.17f, 277.13f), new(192.70f, 278.45f), new(192.39f, 280.08f),
         new(199.42f, 249.16f), new(196.95f, 248.06f), new(190.45f, 252.45f), new(194.14f, 249.30f)];

    private void DrawStormCloud(CanvasDrawingSession ds, StrykersRunHazardSnapshot hazard)
    {
        using var hudClip = ds.CreateLayer(1, BossWorldClip);
        Vector2 ground = Point(hazard.X, hazard.Y);
        float depth = DepthScale(hazard.Y), clock = hazard.Age;
        float charge = Math.Clamp(clock / Math.Max(.01f, hazard.WarningSeconds), 0, 1);
        float growth = .68f + .32f * charge * charge * (3 - 2 * charge);
        float width = 330 * depth * growth;
        float height = width * (float)(_stormCloudCells[0].Height / _stormCloudCells[0].Width);
        Vector2 center = ground - new Vector2(0, (154 + MathF.Sin(clock * 2.6f) * 5) * depth);
        // Each filament lands inside the gameplay footprint. Small forks and
        // contact sparks show the dangerous ground without drawing an AoE disc.
        int tick = (int)(clock * 16);
        if (!hazard.IsWarning)
            for (int bolt = 0; bolt < 5; bolt++)
            {
                float pulse = Fraction(clock * 7 + bolt * .173f);
                if (pulse > .80f) continue;
                float lateral = (Noise(hazard.Id * 53 + bolt * 37 + tick / 3) - .5f) * hazard.Radius * 1.65f;
                float forward = (Noise(bolt * 41 + tick / 2) - .5f) * hazard.Radius * .85f;
                Vector2 landing = Point(hazard.X + lateral, hazard.Y + forward);
                Vector2 source = center + new Vector2(lateral * Width * depth * .75f, height * .15f);
                int seed = hazard.Id * 139 + tick * 73 + bolt * 17;
                byte alpha = (byte)(160 + (1 - pulse) * 90);
                DrawBolt(ds, source, landing, seed, (1.45f + (1 - pulse) * 1.1f) * depth,
                    Ink(107, 200, 255, alpha), 7);
                Vector2 fork = Vector2.Lerp(source, landing, .53f);
                Vector2 forkEnd = landing + new Vector2((bolt % 2 == 0 ? -1 : 1) * 18, -13) * depth;
                DrawBolt(ds, fork, forkEnd, seed + 61, .9f * depth, Ink(161, 223, 255, alpha), 4);
                DrawElectricContact(ds, landing, 12 * depth, seed, 1 - pulse * .7f);
            }

        using (var cloud = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear))
        {
            float phase = clock * 8;
            int frame = (int)phase % _stormCloudCells.Length;
            float blend = Fraction(phase);
            var destination = new Rect(center.X - width / 2, center.Y - height * .56f, width, height);
            cloud.DrawFromSpriteSheet(_stormCloud!, destination, _stormCloudCells[frame],
                new Vector4(1, 1, 1, 1 - blend));
            cloud.DrawFromSpriteSheet(_stormCloud!, destination, _stormCloudCells[(frame + 1) % _stormCloudCells.Length],
                new Vector4(1, 1, 1, blend));
        }
        if (hazard.IsWarning)
            for (int arc = 0; arc < 3; arc++)
            {
                Vector2 from = center + new Vector2((arc - 1) * width * .19f, height * .02f);
                Vector2 to = from + new Vector2(width * .15f, height * .13f);
                DrawBolt(ds, from, to, hazard.Id + tick * 19 + arc * 47, .8f + charge,
                    Ink(132, 212, 255, (byte)(90 + charge * 155)), 5);
            }
    }

    private static void DrawStormCloudShadow(CanvasDrawingSession ds, StrykersRunHazardSnapshot hazard)
    {
        Vector2 ground = Point(hazard.X, hazard.Y);
        float depth = DepthScale(hazard.Y);
        ds.FillEllipse(ground.X + 8 * depth, ground.Y + 12 * depth, 101 * depth, 32 * depth, Ink(0, 8, 16, 69));
    }

    private void DrawStormCraters(CanvasDrawingSession ds, StrykersRunSnapshot state, float presentationDistance)
    {
        if (state.Craters.Count == 0) return;
        using var clip = ds.CreateLayer(1, BossWorldClip);
        float scroll = (presentationDistance - state.Distance) * StrykersRunGame.StormCraterScrollScale;
        foreach (var crater in state.Craters)
        {
            float y = crater.Y + scroll;
            if (y > StrykersRunGame.StormCraterCullY) continue;
            Vector2 point = Point(crater.X, y);
            float depth = DepthScale(y), size = 145 * depth;
            Rect source = _stormCraterCells[crater.Id % _stormCraterCells.Length];
            float height = size * (float)(source.Height / source.Width);
            using (var ground = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear))
                ground.DrawFromSpriteSheet(_stormCraters!, new Rect(point.X - size / 2,
                    point.Y - height * .5f, size, height), source);
            for (int arc = 0; arc < 3; arc++)
            {
                float phase = crater.Age * 1.7f + arc * 2.1f + crater.Id;
                float pulse = .5f + .5f * MathF.Sin(phase * 3.1f);
                float angle = phase * .26f;
                Vector2 from = point + new Vector2(MathF.Cos(angle) * size * .34f, MathF.Sin(angle) * height * .27f);
                Vector2 to = point + new Vector2(MathF.Cos(angle + .54f) * size * .40f,
                    MathF.Sin(angle + .54f) * height * .32f - 5 * depth);
                DrawBolt(ds, from, to, crater.Id * 47 + arc * 79 + (int)(crater.Age * 8),
                    .8f * depth, Ink(120, 215, 255, (byte)(70 + pulse * 150)), 4);
            }
            // Reuse the detailed dissipating smoke artwork, with independent
            // slow puffs rooted in the crater. The damaged road itself never fades.
            using var smoke = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear);
            for (int puff = 0; puff < 3; puff++)
            {
                float age = Fraction(crater.Age * .30f + puff / 3f + Noise(crater.Id * 17));
                float rise = age * 102 * depth;
                float drift = MathF.Sin(crater.Id + age * 3 + puff) * age * 20 * depth;
                float smokeSize = (66 + age * 53) * depth;
                float opacity = MathF.Sin(age * MathF.PI) * .72f;
                var destination = new Rect(point.X + drift - smokeSize / 2,
                    point.Y - rise - smokeSize * .78f, smokeSize, smokeSize);
                smoke.DrawFromSpriteSheet(_images!.Image("explosion.png")!, destination,
                    _explosionCells[6], new Vector4(.85f, 1, 1, opacity * (1 - age)));
                smoke.DrawFromSpriteSheet(_images.Image("explosion.png")!, destination,
                    _explosionCells[7], new Vector4(.85f, 1, 1, opacity * age));
            }
        }
    }

    private static void DrawReturningLightning(CanvasDrawingSession ds, StrykersRunHazardSnapshot hazard)
    {
        using var hudClip = ds.CreateLayer(1, BossWorldClip);
        Vector2 origin = Point(hazard.X, hazard.Y) - new Vector2(0, 70 * DepthScale(hazard.Y));
        Vector2 target = Point(hazard.TargetX, hazard.TargetY) - new Vector2(0, 175 * DepthScale(hazard.TargetY));
        float flight = Math.Max(.01f, hazard.WarningSeconds);
        float phase = Math.Clamp(hazard.Age / flight, 0, 1);
        int tick = (int)(hazard.Age * 30);
        if (hazard.Age < flight)
        {
            for (int bolt = 0; bolt < 18; bolt++)
            {
                float seed = Noise(hazard.Id * 71 + bolt * 43);
                float delay = bolt % 6 * .024f;
                float t = (hazard.Age - delay) / (flight - delay);
                if (t < 0 || t >= 1) continue;
                float spread = (seed - .5f) * 310;
                Vector2 Arc(float progress) => Vector2.Lerp(origin, target, progress) +
                    new Vector2(MathF.Sin(progress * MathF.PI) * spread,
                        -MathF.Sin(progress * MathF.PI) * (26 + seed * 54));
                Vector2 front = Arc(t), back = Arc(Math.Max(0, t - .095f - seed * .04f));
                DrawBolt(ds, back, front, hazard.Id + bolt * 61 + tick * 17,
                    1.35f + seed * .85f, Ink(123, 216, 255), 5);
                if (bolt % 3 == 0)
                    DrawBolt(ds, Vector2.Lerp(back, front, .45f),
                        front + new Vector2((seed - .5f) * 22, 9),
                        bolt * 23 + tick, .85f, Ink(196, 239, 255, 218), 3);
            }
            if (phase < .35f)
                DrawElectricContact(ds, origin, 37 * (1 - phase), hazard.Id + tick, 1 - phase * 2);
        }
        else
        {
            float fade = Math.Clamp(1 - (hazard.Age - flight) / Math.Max(.01f, hazard.Duration - flight), 0, 1);
            DrawElectricContact(ds, target, 68 * (.65f + .35f * fade), hazard.Id + tick * 31, fade);
            for (int arc = 0; arc < 8; arc++)
            {
                float angle = arc * MathF.Tau / 8 + hazard.Id;
                Vector2 end = target + new Vector2(MathF.Cos(angle) * 86, MathF.Sin(angle) * 63);
                DrawBolt(ds, target, end, tick * 43 + arc * 17, 1.8f * fade,
                    Ink(122, 208, 255, (byte)(fade * 235)), 5);
            }
        }
    }

    private static void DrawElectricContact(CanvasDrawingSession ds, Vector2 point, float size, int seed, float opacity)
    {
        byte alpha = (byte)(Math.Clamp(opacity, 0, 1) * 235);
        for (int ray = 0; ray < 5; ray++)
        {
            float angle = ray * 2.39996f + Noise(seed) * MathF.Tau;
            Vector2 tip = point + new Vector2(MathF.Cos(angle), MathF.Sin(angle) * .65f) * size;
            ds.DrawLine(point, tip, Ink(99, 188, 255, (byte)(alpha * .42f)), 4);
            ds.DrawLine(point, tip, Ink(215, 245, 255, alpha), 1.1f);
        }
        ds.FillEllipse(point.X, point.Y, 2.5f, 1.8f, Ink(239, 253, 255, alpha));
    }
}
