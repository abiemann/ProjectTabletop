namespace ProjectTabletop.Interaction;

/// <summary>The rendered clay and the shot share one banked physical ellipse.
/// Rotation is in physical image space, independent of board aspect ratio.</summary>
public static class DuckHuntClayLayout
{
    public static BoardRect DiscBounds(DuckHuntClayTarget clay, double boardAspect)
    {
        double width = Math.Min(.125, .15 / boardAspect) * clay.Scale;
        double height = width * boardAspect * clay.FaceRatio;
        return new(clay.U - width / 2, clay.V - height / 2, width, height);
    }

    public static bool ContainsClay(BoardAim point, DuckHuntClayTarget clay, double boardAspect = 1)
    {
        if (!clay.Visible || clay.Hit || !double.IsFinite(point.U) || !double.IsFinite(point.V) ||
            !double.IsFinite(boardAspect) || boardAspect <= 0) return false;
        var bounds = DiscBounds(clay, boardAspect);
        double x = (point.U - clay.U) * boardAspect, y = point.V - clay.V;
        double sine = Math.Sin(clay.BankAngle), cosine = Math.Cos(clay.BankAngle);
        double u = (x * cosine + y * sine) / (bounds.Width * boardAspect / 2);
        double v = (-x * sine + y * cosine) / (bounds.Height / 2);
        return u * u + v * v <= 1;
    }
}
