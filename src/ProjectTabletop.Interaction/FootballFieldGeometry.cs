namespace ProjectTabletop.Interaction;

/// <summary>The painted field and surrounding turf, in the renderer's design units.</summary>
public static class FootballFieldGeometry
{
    public const double Width = 1600, Height = 1000;
    public const double TurfLeft = -110, TurfTop = -64;
    public const double TurfWidth = 1820, TurfHeight = 1128, TurfCornerRadius = 16;

    /// <summary>Marker centres can use the entire grass surface, including behind the
    /// goal lines. Coordinates remain relative to the painted playing rectangle.</summary>
    public static bool ContainsMarker(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return false;
        double px = x * Width, py = y * Height;
        if (px < TurfLeft || px > TurfLeft + TurfWidth || py < TurfTop || py > TurfTop + TurfHeight)
            return false;
        double dx = px - Math.Clamp(px, TurfLeft + TurfCornerRadius, TurfLeft + TurfWidth - TurfCornerRadius);
        double dy = py - Math.Clamp(py, TurfTop + TurfCornerRadius, TurfTop + TurfHeight - TurfCornerRadius);
        return dx * dx + dy * dy <= TurfCornerRadius * TurfCornerRadius;
    }
}
