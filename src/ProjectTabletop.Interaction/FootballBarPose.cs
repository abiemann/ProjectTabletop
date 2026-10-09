using System.Numerics;

namespace ProjectTabletop.Interaction;

/// <summary>Fits a kicker just ahead of a measured bar in physical pitch units.</summary>
public static class FootballBarPose
{
    public const float Clearance = .006f;

    public static bool TryResolve(int player, Vector2 center, Vector2 end1, Vector2 end2,
        Vector2 side1, Vector2 side2, float? previousHeading, out Vector2 position, out float heading)
    {
        position = default;
        heading = 0;
        if (player is < 0 or > 1 || !Finite(center) || !Finite(end1) || !Finite(end2) ||
            !Finite(side1) || !Finite(side2)) return false;
        var axis = end2 - end1;
        if (axis.LengthSquared() < .000001f) return false;
        axis = Vector2.Normalize(axis);
        var forward = new Vector2(-axis.Y, axis.X);
        float opponent = player == 0 ? 1 : -1;
        // A rectangle has no marked front. Its pitch half supplies that side.
        // At the exact side-on position retain the last side to avoid a 180-degree
        // flip caused by a pixel of contour noise.
        if (MathF.Abs(forward.X) < .06f && previousHeading is { } previous && float.IsFinite(previous))
        {
            if (Vector2.Dot(forward, new(MathF.Cos(previous), MathF.Sin(previous))) < 0) forward = -forward;
        }
        else if (forward.X * opponent < 0 ||
            MathF.Abs(forward.X) < .00001f && forward.Y * opponent < 0) forward = -forward;
        float halfThickness = MathF.Abs(Vector2.Dot(side2 - side1, forward)) / 2;
        if (!float.IsFinite(halfThickness) || halfThickness > .15f) return false;
        position = center + forward * (halfThickness + FootballGame.KickerRadius + Clearance);
        heading = MathF.Atan2(forward.Y, forward.X);
        return true;
    }

    private static bool Finite(Vector2 point) => float.IsFinite(point.X) && float.IsFinite(point.Y);
}
