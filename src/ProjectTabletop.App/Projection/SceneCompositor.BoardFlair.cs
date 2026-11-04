using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.Calibration;
using ProjectTabletop.Vision;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // As soon as the white scan knows the cardboard corners,
    // four orange arrow tips fly from the board's centre and land flush in them as
    // brackets, and test-card strips run along the edges; they stay through the
    // registration dots. Without a previous alignment to place them before the
    // dots, the same flight plays after registration instead.
    private static readonly TimeSpan BoardFlairDuration = TimeSpan.FromMilliseconds(1400);
    // Corners already shown during setup only settle onto the measured edges.
    private static readonly TimeSpan SettledFlairDuration = TimeSpan.FromMilliseconds(400);
    private Vector2[]? _setupFlairCorners;
    private DateTimeOffset _setupFlairStartedAt;
    private bool _revealFlairAnimated = true;

    private TimeSpan CurrentFlairDuration => _revealFlairAnimated ? BoardFlairDuration : SettledFlairDuration;

    /// <summary>Shows the corner flair on the setup white field at approximate projector corners.</summary>
    public void ShowSetupCorners(IReadOnlyList<Vector2> projectorCorners)
    {
        ArgumentNullException.ThrowIfNull(projectorCorners);
        if (projectorCorners.Count != 4 || projectorCorners.Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y)))
            throw new ArgumentException("Setup corners need four finite projector points.", nameof(projectorCorners));
        lock (_gate)
        {
            if (!_boardSetup) return;
            _setupFlairCorners = projectorCorners.ToArray();
            _setupFlairStartedAt = _boardRevealClock();
        }
    }

    private void DrawSetupFlair(CanvasDrawingSession ds, Rect output)
    {
        if (_setupFlairCorners is not { } projector) return;
        Homography board;
        try
        {
            board = Homography.FromFourPoints([new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                projector.Select(point => new Point2(point.X, point.Y)).ToArray());
        }
        catch (ArgumentException) { return; }
        Vector2 Out(Point2 point) => new((float)(output.X + point.X * output.Width), (float)(output.Y + point.Y * output.Height));
        var corners = projector.Select(point => Out(new(point.X, point.Y))).ToArray();
        var centre = corners.Aggregate(Vector2.Zero, (sum, point) => sum + point) / 4;
        DrawFlair(ds, corners, point => Out(board.Transform(point)), centre,
            (_boardRevealClock() - _setupFlairStartedAt).TotalMilliseconds, 1);
    }
    // The camera preview's corner brackets use the same orange.
    private static readonly Color FlairOrange = Color.FromArgb(255, 255, 131, 31);
    private const double ArrowFlightMilliseconds = 650, ArrowStaggerMilliseconds = 60;
    private const double StripStartMilliseconds = 450, StripSweepMilliseconds = 550;

    private void DrawBoardFlair(CanvasDrawingSession ds, Rect output, double milliseconds, float opacity)
    {
        if (_boardMediaClip is not { } clip || _boardSurfaceMap is not { } surface) return;
        Vector2 Out(Point2 point) => new((float)(output.X + point.X * output.Width), (float)(output.Y + point.Y * output.Height));
        // Corners shown during setup are already landed; they only move onto the measured edges.
        DrawFlair(ds, clip.Corners.Select(Out).ToArray(), point => Out(surface.Transform(point)),
            Out(new(_boardRevealCenter.X, _boardRevealCenter.Y)), _revealFlairAnimated ? milliseconds : 1e9, opacity);
    }

    private static void DrawFlair(CanvasDrawingSession ds, Vector2[] corners, Func<Point2, Vector2> board,
        Vector2 start, double milliseconds, float opacity)
    {
        if (opacity <= 0 || milliseconds <= 0) return;
        using var boardGeometry = CanvasGeometry.CreatePolygon(ds.Device, corners);
        using var layer = ds.CreateLayer(opacity, boardGeometry);
        DrawTestStrips(ds, board, milliseconds);
        DrawCornerArrows(ds, start, corners, milliseconds);
    }

    private static void DrawCornerArrows(CanvasDrawingSession ds, Vector2 start, Vector2[] corners, double milliseconds)
    {
        float shortSide = Enumerable.Range(0, 4).Min(index => Vector2.Distance(corners[index], corners[(index + 1) % 4]));
        float length = shortSide * .11f, thickness = shortSide * .022f;
        for (int index = 0; index < 4; index++)
        {
            double t = (milliseconds - index * ArrowStaggerMilliseconds) / ArrowFlightMilliseconds;
            if (t <= 0) continue;
            float progress = 1 - MathF.Pow(1 - (float)Math.Min(t, 1), 3);
            Vector2 corner = corners[index];
            Vector2 toPrevious = Vector2.Normalize(corners[(index + 3) % 4] - corner);
            Vector2 toNext = Vector2.Normalize(corners[(index + 1) % 4] - corner);
            float sine = MathF.Abs(toPrevious.X * toNext.Y - toPrevious.Y * toNext.X);
            if (!float.IsFinite(sine) || sine < .2f) continue;
            // Only the arrow tip flies: its apex travels to the corner and its arms
            // follow the two board edges, so on landing it sits flush as a bracket.
            Vector2 apex = Vector2.Lerp(start, corner, progress);
            float scale = .45f + .55f * progress, arm = length * scale, inset = thickness * scale / sine;
            Vector2 inner = apex + (toPrevious + toNext) * inset;
            Vector2[] bracket =
            [
                apex, apex + toPrevious * arm, apex + toPrevious * arm + toNext * inset, inner,
                apex + toNext * arm + toPrevious * inset, apex + toNext * arm
            ];
            using var shape = CanvasGeometry.CreatePolygon(ds.Device, bracket);
            ds.FillGeometry(shape, FlairOrange);
        }
    }

    private static void DrawTestStrips(CanvasDrawingSession ds, Func<Point2, Vector2> board, double milliseconds)
    {
        for (int edge = 0; edge < 4; edge++)
        {
            double sweep = Math.Clamp((milliseconds - StripStartMilliseconds - edge * 50) / StripSweepMilliseconds, 0, 1);
            if (sweep <= 0) continue;
            sweep = 1 - Math.Pow(1 - sweep, 3);
            foreach (var segment in BoardCalibrationTestCard.Segments)
            {
                if (segment.Start >= sweep) continue;
                double end = Math.Min(segment.End, sweep);
                Vector2[] quad =
                [
                    board(StripPoint(edge, segment.Start, segment.Near)), board(StripPoint(edge, end, segment.Near)),
                    board(StripPoint(edge, end, segment.Far)), board(StripPoint(edge, segment.Start, segment.Far))
                ];
                using var shape = CanvasGeometry.CreatePolygon(ds.Device, quad);
                ds.FillGeometry(shape, Color.FromArgb(255, segment.Red, segment.Green, segment.Blue));
            }
        }
    }

    private static Point2 StripPoint(int edge, double along, double across)
    {
        var point = BoardCalibrationTestCard.Point(edge, along, across);
        return new(point.X, point.Y);
    }
}
