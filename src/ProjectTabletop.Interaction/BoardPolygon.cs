namespace ProjectTabletop.Interaction;

internal static class BoardPolygon
{
    public static bool Contains(ReadOnlySpan<BoardAim> outline, BoardAim point)
    {
        bool inside = false;
        for (int i = 0, j = outline.Length - 1; i < outline.Length; j = i++)
        {
            var a = outline[i]; var b = outline[j];
            if ((a.V > point.V) != (b.V > point.V) &&
                point.U < (b.U - a.U) * (point.V - a.V) / (b.V - a.V) + a.U) inside = !inside;
        }
        return inside;
    }
}
