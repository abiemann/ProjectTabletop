using System.Numerics;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // The rig is expressed in square physical units. Body artwork is registered
    // to its bottom centre; head artwork is registered to the neck attachment.
    // These are the only art-registration values that mouth effects also use.
    private static readonly Rect SlotDragonBodyRect = new(-42, -70, 84, 70);
    private static readonly Rect SlotDragonHeadRect = new(-25, -42, 50, 50);
    private static readonly Vector2 SlotDragonHeadPivot = new(0, -60);
    private static readonly Vector2 SlotDragonHeadMouth = new(0, -2.9375f);

    private static readonly float[] SlotDragonYawAngles = [-65, -32, 0, 32, 65];
    private readonly record struct SlotDragonHeadRegistration(Rect Source, Vector2 Neck, Vector2 Mouth, float Scale);
    // Master-pixel landmarks in the 1619x971 authored atlas. Register the lower
    // gold neck, not the changing silhouette or mouth, so a swivel stays seated.
    // One physical scale preserves the anatomy across all fifteen poses.
    private static readonly SlotDragonHeadRegistration[] SlotDragonSwivelRegistrations =
    [
        new(new(0, 15, 324, 310), new(205, 278), new(118, 248), .15f),
        new(new(324, 15, 324, 310), new(515, 280), new(451, 250), .15f),
        new(new(648, 15, 324, 310), new(810, 282), new(810, 260), .15f),
        new(new(972, 15, 324, 310), new(1118, 280), new(1167, 252), .15f),
        new(new(1296, 15, 323, 310), new(1415, 278), new(1504, 249), .15f),
        new(new(0, 325, 324, 310), new(205, 589), new(118, 558), .15f),
        new(new(324, 325, 324, 310), new(516, 591), new(451, 560), .15f),
        new(new(648, 325, 324, 310), new(810, 595), new(810, 566), .15f),
        new(new(972, 325, 324, 310), new(1118, 590), new(1168, 559), .15f),
        new(new(1296, 325, 323, 310), new(1415, 588), new(1504, 558), .15f),
        new(new(0, 635, 324, 310), new(206, 896), new(118, 868), .15f),
        new(new(324, 635, 324, 310), new(518, 896), new(452, 866), .15f),
        new(new(648, 635, 324, 310), new(810, 899), new(810, 876), .15f),
        new(new(972, 635, 324, 310), new(1118, 898), new(1165, 871), .15f),
        new(new(1296, 635, 323, 310), new(1415, 896), new(1504, 867), .15f)
    ];

    private readonly record struct SlotDragonAttack(int Index, SlotEffect Power, SlotPosition Cell,
        DateTimeOffset PhaseStartedAt, DateTimeOffset HatchedAt, float PhaseTime,
        float Launch, float Flight, float Impact, float End, Vector2 Target);

    private readonly record struct SlotDragonPose(Matrix3x2 BodyTransform, Matrix3x2 HeadTransform,
        Vector2 Mouth, Vector2 Forward, float Emergence, float Roar, float AimWeight, float Yaw, int TargetFrame = 2);

    /// <summary>Pure presentation of the power and target already chosen by the game.</summary>
    private static bool TrySlotDragonAttack(SlotSnapshot game, DateTimeOffset now, SlotLayout layout,
        out SlotDragonAttack attack)
    {
        attack = default;
        if (game.Phase != SlotPhase.RespinEffect || !game.InRespins || game.EffectCell is not { } cell
            || cell.Reel is < 0 or >= SlotGame.Reels || cell.Row is < 0 or >= SlotGame.Rows
            || !game.IsActiveRow(cell.Row) || now < game.PhaseStartedAt) return false;
        int index = Array.FindIndex(SlotDragonPowers, item => item.Power == game.Effect);
        if (index < 0) return false;
        var hatch = game.DragonHatches.FirstOrDefault(item => item.Power == game.Effect);
        if (hatch is null || hatch.HatchedAt > now) return false;
        var symbol = game.Cell(cell.Reel, cell.Row).Symbol;
        if (symbol != SlotDragonPowers[index].Egg && symbol != SlotSymbol.EggRainbow) return false;

        bool firstHatch = hatch.HatchedAt == game.PhaseStartedAt;
        float launch = firstHatch ? .82f : .52f;
        float flight = firstHatch ? .30f : .50f;
        float impact = launch + flight;
        float rowCount = game.RowCount, firstRow = game.FirstRow;
        if (game.Effect == SlotEffect.Expand)
        {
            // The target belongs to the grid at impact, not its temporary
            // location while the original three rows are opening out.
            float reveal = (float)Ease(Math.Min(1, impact / SlotGame.RespinEffectDuration.TotalSeconds / .6));
            rowCount = 3 + 2 * reveal;
            firstRow = 1 - reveal;
        }
        var target = Center(layout.Cell(cell.Reel, cell.Row, firstRow, rowCount));
        target.X *= layout.Aspect;
        attack = new(index, game.Effect, cell, game.PhaseStartedAt, hatch.HatchedAt,
            (float)(now - game.PhaseStartedAt).TotalSeconds, launch, flight, impact, impact + .28f, target);
        return true;
    }

    /// <summary>
    /// Shared body/head placement and muzzle registration, including queries at
    /// the exact launch instant. Rendering order cannot change the result.
    /// </summary>
    private static SlotDragonPose SlotDragonPoseAt(int index, DateTimeOffset hatchedAt,
        DateTimeOffset now, float clock, SlotLayout layout, SlotDragonAttack? attack = null)
    {
        float age = (float)(now - hatchedAt).TotalSeconds;
        float emergence = (float)Ease(Math.Clamp((age - .3f) / .8f, 0, 1));
        float breathing = MathF.Sin(clock * 2.4f + index * 2.1f);
        float bottom = 234 + (1 - emergence) * 34;
        var center = SlotDragonCenter(index, layout.Aspect);
        var body = Matrix3x2.CreateScale(1 - .004f * breathing, 1 + .013f * breathing)
            * Matrix3x2.CreateRotation(.008f * MathF.Sin(clock * 1.2f + index), new Vector2(0, -7))
            * Matrix3x2.CreateTranslation(center.X * layout.Aspect, bottom);

        // The neck remains upright. Perspective changes live in the authored
        // yaw frames, never in an attack-dependent screen-plane rotation.
        var head = Matrix3x2.CreateTranslation(SlotDragonHeadPivot) * body;
        float aim = 0, roar = 0, yaw = 0;
        int targetFrame = 2;
        Vector2? target = null;
        if (attack is { } action && action.Index == index)
        {
            float time = (float)(now - action.PhaseStartedAt).TotalSeconds;
            float charge = action.Launch - .18f;
            float recover = 1 - SlotDragonPoseEase((time - action.Impact - .06f) / (action.End - action.Impact - .06f));
            aim = SlotDragonPoseEase((time - charge + .28f) / .28f) * recover;
            roar = SlotDragonPoseEase((time - charge + .18f) / .18f) * recover;
            target = action.Target;
            // Choose the final painting from a fixed, fully emerged neck, so
            // breathing or crossing a frame midpoint during the turn cannot
            // change the source that its transient blend is settling toward.
            var nominalNeck = new Vector2(center.X * layout.Aspect, 234 + SlotDragonHeadPivot.Y);
            var nominalDirection = action.Target - nominalNeck;
            float targetYaw = MathF.Atan2(nominalDirection.X, nominalDirection.Y) * 180 / MathF.PI;
            var targetFrames = SlotDragonYawFrames(targetYaw);
            targetFrame = targetFrames.Blend < .5f ? targetFrames.Left : targetFrames.Right;
            for (int step = 0; step < 4; step++)
            {
                var localMouth = Vector2.Lerp(SlotDragonHeadMouth, SlotDragonSwivelMouth(index, yaw), roar);
                var toTarget = action.Target - Vector2.Transform(localMouth, head);
                float desired = MathF.Atan2(toTarget.X, toTarget.Y) * 180 / MathF.PI;
                yaw = Math.Clamp(desired, SlotDragonYawAngles[0], SlotDragonYawAngles[^1]) * aim;
            }
        }

        var mouth = Vector2.Transform(Vector2.Lerp(SlotDragonHeadMouth,
            SlotDragonSwivelMouth(index, yaw), roar), head);
        var forward = target is { } point ? Vector2.Normalize(point - mouth)
            : Vector2.Normalize(Vector2.TransformNormal(Vector2.UnitY, head));
        return new(body, head, mouth, forward, emergence, roar, aim, yaw, targetFrame);
    }

    private static (int Left, int Right, float Blend) SlotDragonYawFrames(float yaw)
    {
        yaw = Math.Clamp(yaw, SlotDragonYawAngles[0], SlotDragonYawAngles[^1]);
        for (int index = 0; index < SlotDragonYawAngles.Length - 1; index++)
        {
            if (yaw > SlotDragonYawAngles[index + 1]) continue;
            float blend = (yaw - SlotDragonYawAngles[index])
                / (SlotDragonYawAngles[index + 1] - SlotDragonYawAngles[index]);
            if (blend <= .0001f) return (index, index, 0);
            if (blend >= .9999f) return (index + 1, index + 1, 0);
            return (index, index + 1, blend);
        }
        return (4, 4, 0);
    }

    private static Rect SlotDragonSwivelBox(int index, int frame)
    {
        var registration = SlotDragonSwivelRegistrations[index * 5 + frame];
        return new((registration.Source.X - registration.Neck.X) * registration.Scale,
            (registration.Source.Y - registration.Neck.Y) * registration.Scale,
            registration.Source.Width * registration.Scale, registration.Source.Height * registration.Scale);
    }

    private static Vector2 SlotDragonSwivelMouth(int index, float yaw)
    {
        var frames = SlotDragonYawFrames(yaw);
        var left = SlotDragonSwivelRegistrations[index * 5 + frames.Left];
        var right = SlotDragonSwivelRegistrations[index * 5 + frames.Right];
        return Vector2.Lerp((left.Mouth - left.Neck) * left.Scale,
            (right.Mouth - right.Neck) * right.Scale, frames.Blend);
    }

    private static float SlotDragonPoseEase(float value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * (3 - 2 * value);
    }
}
