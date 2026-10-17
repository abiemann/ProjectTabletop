using System.Numerics;
using ComputeSharp;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection.StrykersRun;

internal sealed partial class StrykersRunRenderer
{
    private static readonly Rect GlowCell = new(0, 0, 96, 96);
    private static readonly Rect SmokeCell = new(96, 0, 128, 128);
    private static readonly Rect ShardCell = new(224, 0, 32, 32);
    private static readonly Rect HealthCell = new(0, 101, 96, 20);

    private CanvasRenderTarget CreateParticles()
    {
        var target = new CanvasRenderTarget(_device, 256, 128, 96);
        try
        {
            using var ds = target.CreateDrawingSession();
            ds.Clear(Ink(0, 0, 0, 0));
            using var glow = new CanvasRadialGradientBrush(_device,
            [
                new() { Position = 0, Color = Ink(255, 255, 255) },
                new() { Position = .18f, Color = Ink(255, 255, 255, 213) },
                new() { Position = .48f, Color = Ink(255, 255, 255, 61) },
                new() { Position = 1, Color = Ink(255, 255, 255, 0) }
            ]) { Center = new(48, 48), RadiusX = 47, RadiusY = 47 };
            ds.FillCircle(48, 48, 47, glow);
            using (ds.CreateLayer(1, SmokeCell))
            {
                for (int i = 0; i < 11; i++)
                {
                    float angle = i * 2.39996f, radius = 14 + Noise(i + 41) * 19;
                    var center = new Vector2(160 + MathF.Cos(angle) * 25, 64 + MathF.Sin(angle) * 22);
                    glow.Center = center; glow.RadiusX = radius; glow.RadiusY = radius * .83f;
                    glow.Opacity = .65f;
                    ds.FillEllipse(center, radius, radius * .83f, glow);
                }
            }
            ds.FillRectangle(new Rect(230, 7, 18, 15), Ink(255, 255, 255));
            ds.FillRectangle(new Rect(226, 12, 24, 10), Ink(228, 228, 228));
            ds.DrawLine(230, 8, 247, 8, Ink(255, 255, 255), 2);
            ds.FillRoundedRectangle(new Rect(1, 102, 94, 18), 4, 4, Ink(255, 255, 255));
            return target;
        }
        catch { target.Dispose(); throw; }
    }

    private void DrawStreet(CanvasDrawingSession ds, float distance, float clock)
    {
        DrawCityBackground(ds, distance);
        var road = _images!.Image("road.png")!;
        _road.ConstantBuffer = new StrykersRunRoadShader(new Float2((float)road.Size.Width, (float)road.Size.Height), distance);
        ds.DrawImage(_road, new Rect(0, 0, Width, Height), new Rect(0, 0, road.Size.Width, road.Size.Height));

        using var batch = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear);
        for (int index = 0; index < 18; index++)
        {
            float y = Fraction(index * .173f + distance * .009f);
            float x = index % 2 == 0 ? .045f : .955f;
            Vector2 point = Point(x, y);
            float scale = DepthScale(y), flicker = .72f + .28f * MathF.Sin(clock * 8 + index * 1.8f);
            if (index % 3 == 0)
            {
                float size = (45 + index % 4 * 8) * scale;
                batch.DrawFromSpriteSheet(_particles!, new Rect(point.X - size, point.Y - size, size * 2, size * 2),
                    GlowCell, new Vector4(1, .32f, .08f, .25f * flicker));
                for (int tongue = 0; tongue < 3; tongue++)
                {
                    float phase = Fraction(clock * (.9f + tongue * .21f) + index * .23f + tongue * .31f);
                    float rise = phase * 72 * scale;
                    float w = (12 + 16 * MathF.Sin(phase * MathF.PI)) * scale;
                    float h = (28 + 32 * MathF.Sin(phase * MathF.PI)) * scale;
                    float drift = MathF.Sin(phase * 8 + index) * 9 * scale;
                    batch.DrawFromSpriteSheet(_particles!, new Rect(point.X + drift - w / 2, point.Y - rise - h, w, h),
                        GlowCell, new Vector4(1, .38f + phase * .2f, .07f, (1 - phase) * .9f));
                }
            }
            float smokeAge = Fraction(clock * .14f + index * .21f);
            float smokeSize = (80 + smokeAge * 115) * scale;
            batch.DrawFromSpriteSheet(_particles!, new Rect(point.X - smokeSize / 2 + smokeAge * 30 * scale,
                point.Y - smokeAge * 140 * scale - smokeSize / 2, smokeSize, smokeSize), SmokeCell,
                new Vector4(.44f, .51f, .56f, MathF.Sin(smokeAge * MathF.PI) * .35f));
        }
        for (int i = 0; i < 24; i++)
        {
            float age = Fraction(clock * .19f + Noise(i * 31));
            float worldX = i % 2 == 0 ? .045f : .955f;
            float y = Fraction(Noise(i * 13) + distance * .009f) - age * .09f;
            Vector2 point = Point(worldX + MathF.Sin(age * 6 + i) * .008f, y);
            float radius = (3 + Noise(i) * 4) * DepthScale(y);
            batch.DrawFromSpriteSheet(_particles!, new Rect(point.X - radius, point.Y - radius * 2,
                radius * 2, radius * 4), GlowCell, new Vector4(1, .49f, .16f, 1 - age));
        }
    }

    private static void DrawGateShadow(CanvasDrawingSession ds, StrykersRunGateSnapshot gate)
    {
        float depth = DepthScale(gate.Y);
        Vector2 point = Point(gate.X, gate.Y);
        float width = gate.Width * Width * depth;
        ds.FillRoundedRectangle(new Rect(point.X - width / 2 + 8 * depth, point.Y - 3, width, 23 * depth),
            6, 6, Ink(0, 0, 0, 123));
    }

    private void DrawGate(CanvasDrawingSession ds, StrykersRunGateSnapshot gate, float squash)
    {
        float depth = DepthScale(gate.Y);
        Vector2 point = Point(gate.X, gate.Y);
        float width = gate.Width * Width * depth, height = 111 * depth * squash;
        float left = point.X - width / 2, top = point.Y - height;
        bool harmful = gate.Kind == StrykersRunGateKind.Subtract;
        var color = harmful ? Ink(255, 83, 70) : Ink(65, 221, 255);
        // One closed coloured frame keeps the corners joined without dark supports outside it.
        ds.FillRoundedRectangle(new Rect(left, top, width, height), 8 * depth, 8 * depth,
            harmful ? Ink(67, 20, 23, 231) : Ink(13, 56, 74, 233));
        ds.DrawRoundedRectangle(new Rect(left, top, width, height), 8 * depth, 8 * depth, color, 3 * depth);
        var key = (gate.Kind, gate.Value);
        if (!_gateLabels.TryGetValue(key, out string? label))
        {
            if (_gateLabels.Count >= 128) _gateLabels.Clear();
            label = gate.Kind switch { StrykersRunGateKind.Subtract => "−", StrykersRunGateKind.Multiply => "×", _ => "+" } + gate.Value;
            _gateLabels.Add(key, label);
        }
        _gateText.FontSize = 64 * depth * Math.Max(.1f, squash);
        Text(ds, label, new Rect(left + 9, top, width - 18, height), Ink(242, 252, 249), _gateText);
    }

    private static void DrawBullets(CanvasDrawingSession ds, StrykersRunSnapshot state)
    {
        if (state.Phase == StrykersRunPhase.GameOver && state.GameOverSeconds >= WallImpactSeconds) return;
        foreach (var bullet in state.Bullets)
        {
            Vector2 front = Point(bullet.X, bullet.Y), tail = Point(bullet.X, bullet.Y + .024f);
            float size = DepthScale(bullet.Y);
            ds.DrawLine(front, tail, Ink(255, 191, 76, 65), 9 * size);
            ds.DrawLine(front, tail, Ink(255, 217, 111, 195), 3.5f * size);
            ds.DrawLine(front, Vector2.Lerp(front, tail, .52f), Ink(255, 255, 228), 1.5f * size);
        }
    }

    private void DrawEffects(CanvasDrawingSession ds, StrykersRunSnapshot state)
    {
        DrawAllyShockwaves(ds, state);
        using var batch = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear);
        foreach (var effect in state.Effects)
        {
            Vector2 center = Point(effect.X, effect.Y);
            float depth = DepthScale(effect.Y), age = Math.Max(0, effect.Age) +
                (state.Phase == StrykersRunPhase.GameOver ? state.GameOverSeconds : 0);
            bool boost = effect.Kind == StrykersRunEffectKind.GateBoost;
            bool penalty = effect.Kind is StrykersRunEffectKind.GatePenalty or StrykersRunEffectKind.SoldierLost;
            bool muzzle = effect.Kind == StrykersRunEffectKind.MuzzleFlash;
            bool defeat = effect.Kind == StrykersRunEffectKind.Defeat;
            bool allyAttack = effect.Kind == StrykersRunEffectKind.AllyAttack;
            bool allyLost = effect.Kind == StrykersRunEffectKind.AllyLost;
            bool opened = effect.Kind is StrykersRunEffectKind.AllyUnlocked or StrykersRunEffectKind.SupplyOpened;
            float duration = muzzle ? .10f : allyLost ? 1.1f : opened ? .8f : allyAttack ? .55f : defeat ? .60f : .42f;
            float progress = age / duration;
            if (progress >= 1) continue;
            Vector4 color = allyAttack || effect.Kind == StrykersRunEffectKind.AllyUnlocked ? new(.24f, 1, .72f, 1) :
                allyLost ? new(.47f, .68f, .64f, 1) : boost ? new(.23f, .92f, 1, 1) :
                penalty ? new(1, .29f, .15f, 1) : new(1, .72f, .22f, 1);
            float size = (muzzle ? 42 : opened || allyAttack ? 170 : defeat || allyLost ? 125 : 72) * depth;
            float burst = (1 - progress) * (muzzle ? 1 : .7f);
            batch.DrawFromSpriteSheet(_particles!, new Rect(center.X - size / 2, center.Y - size / 2, size, size),
                GlowCell, color * new Vector4(1, 1, 1, burst));
            int pieces = muzzle ? 4 : defeat || opened || allyLost ? 12 : 8;
            for (int i = 0; i < pieces; i++)
            {
                float angle = Noise(effect.Seed + i * 37) * MathF.Tau;
                float speed = 45 + Noise(effect.Seed + i * 53) * (defeat || opened || allyAttack ? 180 : 90);
                Vector2 travel = new(MathF.Cos(angle) * speed * age, MathF.Sin(angle) * speed * age - age * 40 + age * age * 180);
                Vector2 point = center + travel * depth;
                float shardSize = (muzzle ? 9 : opened ? 31 : 13) * depth * (1 - progress * .5f);
                var transform = Matrix3x2.CreateTranslation(-16, -16) * Matrix3x2.CreateScale(shardSize / 32, shardSize / 32 * (opened ? .5f : 1)) *
                    Matrix3x2.CreateRotation(angle + age * 6) * Matrix3x2.CreateTranslation(point);
                Vector4 debris = opened && i % 2 == 0 ? new(.58f, .69f, .65f, 1) : color;
                batch.DrawFromSpriteSheet(_particles!, transform, ShardCell, debris * new Vector4(1, 1, 1, 1 - progress));
            }
            if (allyLost)
            {
                Rect source = _allyCells[3];
                float height = (144 - Math.Min(age * 220, 84)) * depth;
                float width = 194 * depth;
                batch.DrawFromSpriteSheet(_images!.Image("ally.png")!,
                    new Rect(center.X - width / 2, center.Y - height, width, height), source,
                    new Vector4(.55f, .65f, .62f, Math.Clamp((1.1f - age) / .35f, 0, 1)));
            }
            if (defeat || allyLost || opened)
            {
                float smoke = (70 + age * 115) * depth;
                batch.DrawFromSpriteSheet(_particles!, new Rect(center.X - smoke / 2, center.Y - smoke / 2 - age * 35,
                    smoke, smoke), SmokeCell, new Vector4(.51f, .48f, .41f, (1 - progress) * .7f));
            }
        }
    }
}
