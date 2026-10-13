using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection.StrykersRun;

internal sealed partial class StrykersRunRenderer
{
    private readonly CanvasTextFormat _objectiveTitle = TextFormat(22);
    private readonly CanvasTextFormat _objectiveCount = TextFormat(43);
    private readonly CanvasTextFormat _allyStatus = TextFormat(21);
    private readonly string[] _lockLabels = Enumerable.Range(0, 100).Select(value => value.ToString()).ToArray();
    private int _allyHealth = -1, _allyMaximumHealth = -1;
    private string _allyHealthLabel = string.Empty;

    private static void DrawObjectiveShadow(CanvasDrawingSession ds, StrykersRunObjectiveSnapshot objective)
    {
        var point = Point(objective.X, objective.Y);
        float depth = DepthScale(objective.Y);
        ds.FillEllipse(point.X + 10 * depth, point.Y + 4 * depth,
            (objective.Kind == StrykersRunObjectiveKind.Rescue ? 117 : 111) * depth, 24 * depth, Ink(0, 0, 0, 150));
    }

    private void DrawObjective(CanvasDrawingSession ds, StrykersRunObjectiveSnapshot objective, float squash)
    {
        float depth = DepthScale(objective.Y);
        var before = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(depth, depth * squash) *
            Matrix3x2.CreateTranslation(Point(objective.X, objective.Y)) * before;
        try
        {
            if (objective.Kind == StrykersRunObjectiveKind.Rescue) DrawRescuePod(ds, objective);
            else DrawReinforcementCache(ds, objective);
        }
        finally { ds.Transform = before; }
    }

    private void DrawRescuePod(CanvasDrawingSession ds, StrykersRunObjectiveSnapshot objective)
    {
        // The ally is visibly contained behind actual steel ribs and glass. Its
        // original sprite supplies a natural silhouette before it is released.
        ds.FillRoundedRectangle(new Rect(-110, -278, 220, 278), 16, 16, Ink(18, 29, 36));
        ds.FillRoundedRectangle(new Rect(-101, -269, 202, 262), 12, 12, Ink(67, 85, 91));
        ds.FillRoundedRectangle(new Rect(-87, -242, 174, 164), 12, 12, Ink(8, 36, 40));
        var source = _allyCells[0];
        float width = 163 * (float)(source.Width / source.Height);
        using (var captive = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear))
            captive.DrawFromSpriteSheet(_images!.Image("ally.png")!, new Rect(-width / 2, -242, width, 163), source,
                new Vector4(.30f, .62f, .57f, .88f));
        for (int rib = 0; rib < 4; rib++)
        {
            float x = -57 + rib * 38;
            ds.DrawLine(x, -241, x, -80, Ink(7, 14, 19), 12);
            ds.DrawLine(x - 2, -240, x - 2, -81, Ink(119, 159, 163), 3);
        }
        ds.DrawRoundedRectangle(new Rect(-87, -242, 174, 164), 12, 12, Ink(102, 231, 210), 3);
        float health = HealthFraction(objective.Health, objective.MaxHealth);
        int cracks = health >= 1 ? 0 : Math.Clamp((int)((1 - health) * 6) + 1, 1, 6);
        for (int crack = 0; crack < cracks; crack++)
        {
            float x = -66 + (crack * 47 % 133), y = -219 + (crack * 29 % 85);
            var color = Ink(179, 255, 236, 210);
            ds.DrawLine(x, y, x + 14, y + 20, color, 2);
            ds.DrawLine(x + 14, y + 20, x + 3, y + 37, color, 2);
            ds.DrawLine(x + 14, y + 20, x + 25, y + 24, color, 1.5f);
        }
        ds.FillRectangle(new Rect(-88, -267, 176, 22), Ink(22, 38, 45));
        Text(ds, "BULWARK", new Rect(-87, -269, 174, 25), Ink(154, 247, 227), _objectiveTitle);
        DrawLockPlate(ds, objective, -76, "FREE ALLY");
        for (int side = -1; side <= 1; side += 2)
        {
            ds.FillRectangle(new Rect(side < 0 ? -110 : 94, -251, 16, 218), Ink(29, 45, 51));
            ds.DrawLine(side * 103, -238, side * 103, -65, Ink(147, 184, 188), 3);
            ds.FillCircle(side * 101, -256, 4, Ink(193, 225, 218));
            ds.FillCircle(side * 101, -25, 4, Ink(193, 225, 218));
        }
        ds.FillRectangle(new Rect(-83, -13, 166, 6), Ink(11, 23, 25));
        ds.FillRectangle(new Rect(-83, -13, 166 * health, 6), Ink(104, 245, 188));
    }

    private void DrawReinforcementCache(CanvasDrawingSession ds, StrykersRunObjectiveSnapshot objective)
    {
        ds.FillRoundedRectangle(new Rect(-108, -150, 216, 150), 9, 9, Ink(27, 34, 39));
        ds.FillRoundedRectangle(new Rect(-100, -141, 200, 130), 5, 5, Ink(91, 101, 96));
        ds.FillRectangle(new Rect(-100, -140, 200, 35), Ink(129, 140, 126));
        ds.DrawLine(-98, -141, 98, -141, Ink(221, 230, 197), 4);
        ds.DrawLine(-101, -106, 101, -106, Ink(25, 35, 37), 5);
        ds.FillRectangle(new Rect(-80, -137, 19, 37), Ink(239, 166, 57));
        ds.FillRectangle(new Rect(61, -137, 19, 37), Ink(239, 166, 57));
        Text(ds, "+6 TROOPS", new Rect(-94, -136, 188, 33), Ink(255, 238, 186), _objectiveTitle);
        DrawLockPlate(ds, objective, -101, "BREAK LOCK");
        for (int side = -1; side <= 1; side += 2)
        {
            ds.FillRectangle(new Rect(side < 0 ? -108 : 88, -98, 20, 89), Ink(39, 48, 47));
            ds.DrawLine(side * 100, -87, side * 100, -20, Ink(172, 188, 171), 3);
            ds.FillCircle(side * 98, -140, 4, Ink(238, 229, 187));
        }
        ds.FillRectangle(new Rect(-76, -12, 152, 5), Ink(13, 23, 25));
        ds.FillRectangle(new Rect(-76, -12, 152 * HealthFraction(objective.Health, objective.MaxHealth), 5), Ink(248, 190, 74));
    }

    private void DrawLockPlate(CanvasDrawingSession ds, StrykersRunObjectiveSnapshot objective, float top, string caption)
    {
        ds.FillRoundedRectangle(new Rect(-86, top, 172, 61), 5, 5, Ink(10, 21, 27));
        ds.DrawRoundedRectangle(new Rect(-86, top, 172, 61), 5, 5, Ink(178, 212, 193), 2);
        _objectiveTitle.FontSize = 17;
        Text(ds, caption, new Rect(-80, top + 1, 160, 20), Ink(164, 198, 188), _objectiveTitle);
        _objectiveTitle.FontSize = 22;
        Text(ds, _lockLabels[Math.Clamp(objective.Health, 0, _lockLabels.Length - 1)],
            new Rect(-74, top + 17, 148, 42), Ink(255, 234, 158), _objectiveCount);
    }

    private void DrawAllyStatus(CanvasDrawingSession ds, StrykersRunSnapshot state)
    {
        if (state.Phase == StrykersRunPhase.GameOver) return;
        if (state.Ally is { Health: > 0 } ally)
        {
            if (_allyHealth != ally.Health || _allyMaximumHealth != ally.MaxHealth)
            {
                _allyHealth = ally.Health; _allyMaximumHealth = ally.MaxHealth;
                _allyHealthLabel = $"BULWARK  {ally.Health}/{ally.MaxHealth} HP";
            }
            var point = Point(ally.X, ally.Y);
            float depth = DepthScale(ally.Y), barWidth = 207 * depth;
            float left = point.X - barWidth / 2, top = point.Y - 254 * depth;
            ds.FillRoundedRectangle(new Rect(left - 7, top - 3, barWidth + 14, 47 * depth), 5, 5, Ink(8, 25, 28, 236));
            _allyStatus.FontSize = 20 * depth;
            Text(ds, _allyHealthLabel, new Rect(left - 3, top, barWidth + 6, 25 * depth), Ink(170, 255, 219), _allyStatus);
            ds.FillRectangle(new Rect(left, top + 29 * depth, barWidth, 9 * depth), Ink(40, 69, 65));
            ds.FillRectangle(new Rect(left, top + 29 * depth, barWidth * HealthFraction(ally.Health, ally.MaxHealth), 9 * depth),
                ally.Health * 4 <= ally.MaxHealth ? Ink(255, 183, 73) : Ink(71, 240, 166));
        }
        foreach (var effect in state.Effects)
        {
            string? label = effect.Kind switch
            {
                StrykersRunEffectKind.AllyUnlocked => "BULWARK FREED · ALLY ACTIVE",
                StrykersRunEffectKind.AllyLost => "BULWARK DOWN",
                StrykersRunEffectKind.SupplyOpened => "+6 REINFORCEMENTS",
                _ => null
            };
            float lifetime = effect.Kind == StrykersRunEffectKind.AllyLost ? 1.1f : 1.4f;
            if (label is null || effect.Age >= lifetime) continue;
            byte alpha = (byte)(255 * Math.Clamp((lifetime - effect.Age) / .25f, 0, 1));
            float top = 139 + Math.Max(0, .18f - effect.Age) * 45;
            ds.FillRoundedRectangle(new Rect(116, top, 668, 46), 7, 7, Ink(10, 28, 34, (byte)(alpha * .88f)));
            _allyStatus.FontSize = 28;
            Text(ds, label, new Rect(120, top, 660, 46), effect.Kind == StrykersRunEffectKind.AllyLost
                ? Ink(255, 182, 97, alpha) : Ink(139, 255, 221, alpha), _allyStatus);
            break;
        }
    }

    private static void DrawAllyShockwaves(CanvasDrawingSession ds, StrykersRunSnapshot state)
    {
        foreach (var effect in state.Effects)
        {
            if (effect.Kind != StrykersRunEffectKind.AllyAttack) continue;
            float age = effect.Age + (state.Phase == StrykersRunPhase.GameOver ? state.GameOverSeconds : 0);
            if (age is < 0 or >= .55f) continue;
            var point = Point(effect.X, effect.Y);
            float depth = DepthScale(effect.Y), progress = age / .55f;
            float radius = (23 + progress * 157) * depth;
            byte alpha = (byte)((1 - progress) * 224);
            ds.DrawEllipse(point.X, point.Y - 9 * depth, radius, radius * .35f, Ink(116, 255, 207, alpha),
                (8 - progress * 6) * depth);
            ds.DrawEllipse(point.X, point.Y - 9 * depth, radius * .76f, radius * .23f, Ink(223, 255, 213, (byte)(alpha * .65f)),
                2 * depth);
            for (int crack = 0; crack < 7; crack++)
            {
                float angle = MathF.PI + crack * MathF.PI / 6;
                var end = point + new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius * .45f);
                var middle = Vector2.Lerp(point, end, .62f) + new Vector2((crack % 2 == 0 ? 5 : -5) * depth, -6 * depth);
                ds.DrawLine(Vector2.Lerp(point, end, .30f), middle, Ink(181, 252, 207, alpha), 2 * depth);
                ds.DrawLine(middle, end, Ink(110, 232, 174, alpha), 2 * depth);
            }
        }
    }

    private static float HealthFraction(int health, int maximum) => maximum > 0 ? Math.Clamp(health / (float)maximum, 0, 1) : 0;
}
