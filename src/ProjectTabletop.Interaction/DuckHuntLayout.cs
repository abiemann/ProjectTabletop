namespace ProjectTabletop.Interaction;

/// <summary>Duck geometry shared by the projected artwork and hit testing. Coordinates
/// are relative to the duck centre, with vertical distances scaled by the board aspect.</summary>
public static class DuckHuntLayout
{
    public static readonly BoardRect ExitBounds = new(.20, .87, .155, .12);
    public static readonly BoardRect MicrophoneBounds = new(.365, .87, .155, .12);
    public const int MicrophonesPerPage = 6;
    public static BoardRect MicrophoneChoiceBounds(int index) => new(.365 + (index % 2) * .295,
        .425 + (index / 2) * .145, .28, .12);
    public static readonly BoardRect Body = new(-.155, -.045, .27, .164);
    public static readonly BoardRect Neck = new(.035, -.083, .064, .136);
    public static readonly BoardRect Head = new(.048, -.117, .094, .104);
    public static readonly BoardAim[] Bill = [new(.127, -.074), new(.195, -.055), new(.137, -.034)];
    public static readonly BoardAim[] Tail = [new(-.105, .015), new(-.185, -.04), new(-.143, .079)];

    public static bool ContainsDuck(BoardAim point, DuckHuntSnapshot duck, double boardAspect = 1)
    {
        if (!duck.DuckVisible || !double.IsFinite(point.U) || !double.IsFinite(point.V) ||
            !double.IsFinite(boardAspect) || boardAspect <= 0) return false;
        double x = point.U - duck.U, y = (point.V - duck.V) / boardAspect;
        double cosine = Math.Cos(duck.Rotation), sine = Math.Sin(duck.Rotation);
        var local = new BoardAim(x * cosine + y * sine, -x * sine + y * cosine);
        return EllipseContains(Body, local) || EllipseContains(Neck, local) || EllipseContains(Head, local) ||
            TriangleContains(Bill, local) || TriangleContains(Tail, local);
    }

    private static bool EllipseContains(BoardRect ellipse, BoardAim point)
    {
        double x = (point.U - ellipse.X - ellipse.Width / 2) / (ellipse.Width / 2);
        double y = (point.V - ellipse.Y - ellipse.Height / 2) / (ellipse.Height / 2);
        return x * x + y * y <= 1;
    }

    private static bool TriangleContains(BoardAim[] triangle, BoardAim point)
    {
        static double Side(BoardAim a, BoardAim b, BoardAim p) =>
            (p.U - b.U) * (a.V - b.V) - (a.U - b.U) * (p.V - b.V);
        double first = Side(triangle[0], triangle[1], point), second = Side(triangle[1], triangle[2], point),
            third = Side(triangle[2], triangle[0], point);
        return !((first < 0 || second < 0 || third < 0) && (first > 0 || second > 0 || third > 0));
    }
}
