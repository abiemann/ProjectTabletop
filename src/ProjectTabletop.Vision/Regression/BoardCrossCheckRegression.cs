using ProjectTabletop.Vision;

internal static class BoardCrossCheckRegression
{
    // Geometry from a live 2026-09-29 scan: the cardboard nearly filled the projected
    // light, 10 px below its top edge and 7 px above its bottom edge. The white-lit top and
    // bottom edges snapped toward the light's edges and failed at 8.9 px against 8.1.
    private static readonly BoardDetection Field = new([new(262, 38), new(1800, 34), new(1790, 907), new(265, 881)], 1);
    private static readonly BoardDetection Ambient = new([new(475, 48), new(1550, 48), new(1530, 897), new(475, 875)], 1);
    private const double Tolerance = 8.1, Margin = Tolerance * 2;

    public static void Run()
    {
        // Edges pulled onto the light's boundary are not board movement.
        var snapped = Shift(Ambient, (0, -9), (0, -9), (0, 8), (0, 8));
        double error = BoardDetector.CrossCheckError(Ambient, snapped, Field, Margin, out int boundary);
        Require(error < .5 && boundary == 4,
            $"Edges merged with the light's boundary failed the cross-check: {error:F2} px, {boundary} boundary corners.");

        // Movement along those edges is still caught.
        var moved = Shift(Ambient, (12, -9), (12, -9), (12, 8), (12, 8));
        Require(BoardDetector.CrossCheckError(Ambient, moved, Field, Margin, out _) > Tolerance,
            "A 12 px sideways move hidden at the light's boundary passed the cross-check.");

        // Clear of the boundary, the full disagreement still counts, as before.
        var wideField = new BoardDetection([new(200, 0), new(1880, 0), new(1880, 1000), new(200, 1000)], 1);
        Require(BoardDetector.CrossCheckError(Ambient, snapped, wideField, Margin, out int clear) is > 8.9 and < 9.1 &&
            clear == 0, "A 9 px disagreement clear of the light's boundary was discounted.");

        // A corner close to two light edges is not compared.
        var cornerField = new BoardDetection([new(470, 42), new(1800, 34), new(1790, 907), new(265, 881)], 1);
        var cornerMoved = Shift(Ambient, (10, 10), (0, 0), (0, 0), (0, 0));
        Require(BoardDetector.CrossCheckError(Ambient, cornerMoved, cornerField, Margin, out _) < .5,
            "A corner inside the light's corner was compared across an edge it cannot measure.");
        Console.WriteLine("Board cross-check regression: edges merged with the light's boundary, sideways movement, " +
            "clear-field disagreement and light-corner exclusion passed.");
    }

    private static BoardDetection Shift(BoardDetection board, params (double X, double Y)[] offsets) =>
        board with { Corners = board.Corners.Select((corner, index) =>
            new PixelPoint(corner.X + offsets[index].X, corner.Y + offsets[index].Y)).ToArray() };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
