using System.Numerics;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection.StrykersRun;

internal sealed partial class StrykersRunRenderer
{
    private void DrawGameOver(CanvasDrawingSession ds, float seconds)
    {
        seconds = Math.Min(seconds, StrykersRunGame.GameOverWallSeconds);
        var wall = _images!.Image("game-over-wall.png")!;
        float scale = Math.Min(1020 / (float)wall.Size.Width, 600 / (float)wall.Size.Height);
        float width = (float)wall.Size.Width * scale, height = (float)wall.Size.Height * scale;
        float finalY = RailTop - 21 - height;
        float fall = Math.Clamp((seconds - .15f) / .70f, 0, 1);
        float top = -height - 40 + (finalY + height + 40) * fall * fall;
        float age = seconds - WallImpactSeconds;
        if (age >= 0) top = finalY - MathF.Abs(MathF.Sin(age * 18)) * 13 * MathF.Exp(-age * 8);
        if (seconds < .15f) return;
        using (var batch = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear))
        {
            float shadow = Math.Clamp(fall * fall, 0, 1);
            batch.DrawFromSpriteSheet(_particles!, new Rect(Width / 2 - width * .55f,
                RailTop - 122, width * 1.1f, 185), SmokeCell, new Vector4(0, 0, 0, shadow * .86f));
        }
        var before = ds.Transform;
        float angle = age < 0 ? -.045f * (1 - fall) : MathF.Sin(age * 16) * .008f * MathF.Exp(-age * 9);
        ds.Transform = Matrix3x2.CreateRotation(angle, new Vector2(Width / 2, top + height / 2)) * before;
        try { ds.DrawImage(wall, new Rect((Width - width) / 2, top, width, height)); }
        finally { ds.Transform = before; }
        if (age < 0) return;

        using var debris = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear);
        for (int i = 0; i < 90; i++)
        {
            float launchX = .08f + Noise(i * 37 + 1) * .84f;
            Vector2 origin = Point(launchX, .83f + Noise(i * 17) * .035f);
            float vx = (Noise(i * 23 + 7) - .5f) * 630;
            float vy = -100 - Noise(i * 29) * 370;
            float landingY = RailTop - 5 - Noise(i * 11) * 50;
            float landingTime = (-vy + MathF.Sqrt(vy * vy + 2040 * Math.Max(0, landingY - origin.Y))) / 1020;
            float flight = Math.Min(age, landingTime);
            float ground = Math.Min(landingY, origin.Y + vy * flight + 510 * flight * flight);
            Vector2 position = new(origin.X + vx * flight, ground);
            float size = 8 + Noise(i * 59) * 28;
            var transform = Matrix3x2.CreateTranslation(-16, -16) * Matrix3x2.CreateScale(size / 32, size / 43) *
                Matrix3x2.CreateRotation(i + flight * (Noise(i + 4) - .5f) * 13) * Matrix3x2.CreateTranslation(position);
            float shade = .52f + Noise(i * 71) * .4f;
            debris.DrawFromSpriteSheet(_particles!, transform, ShardCell, new Vector4(shade, shade * .71f, shade * .53f, 1));
        }
        for (int i = 0; i < 24; i++)
        {
            float life = 1.25f + Noise(i * 19) * .55f;
            float progress = age / life;
            if (progress >= 1) continue;
            Vector2 origin = Point(.08f + Noise(i * 31) * .84f, .80f + Noise(i) * .06f);
            float size = 95 + age * (100 + Noise(i * 41) * 90);
            Vector2 point = origin + new Vector2((Noise(i + 51) - .5f) * age * 130, -age * (40 + Noise(i) * 95));
            debris.DrawFromSpriteSheet(_particles!, new Rect(point.X - size / 2, point.Y - size / 2, size, size),
                SmokeCell, new Vector4(.72f, .64f, .50f, MathF.Sin(MathF.PI * Math.Clamp(progress + .13f, 0, 1)) * .58f));
        }
    }
}
