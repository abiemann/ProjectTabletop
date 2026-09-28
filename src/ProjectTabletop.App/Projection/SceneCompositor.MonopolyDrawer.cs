using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.Interaction;
using System.Numerics;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    public bool MonopolyDrawerOpen { get { lock (_gate) return _boardSession.MonopolyDrawerOpen; } }

    private float MonopolyDrawerProgress(DateTimeOffset now)
    {
        if (!_boardSession.MonopolyDrawerOpen || _boardSession.MonopolyDrawerOpenedAt is not { } opened) return 1;
        return Math.Clamp((float)((now - opened).TotalMilliseconds /
            BoardSession.MonopolyDrawerOpeningDuration.TotalMilliseconds), 0, 1);
    }

    private bool HasMonopolyDrawerAnimation(DateTimeOffset now) =>
        _boardSession.Screen == BoardScreen.Monopoly && _boardSession.MonopolyDrawerOpen && MonopolyDrawerProgress(now) < 1;

    // Only the brief drawer movement redraws the native board; the settled
    // drawer returns to the ordinary static UI cache.
    private int MonopolyDrawerFrame(DateTimeOffset now) => !_boardSession.MonopolyDrawerOpen ? -1 :
        (int)Math.Floor(MonopolyDrawerProgress(now) * 10);

    public object GetMonopolyDrawerDiagnostics(DateTimeOffset now)
    {
        lock (_gate) return new { open = _boardSession.MonopolyDrawerOpen,
            openedAt = _boardSession.MonopolyDrawerOpenedAt,
            durationMilliseconds = BoardSession.MonopolyDrawerOpeningDuration.TotalMilliseconds,
            progress = MonopolyDrawerProgress(now), animating = HasMonopolyDrawerAnimation(now) };
    }

    private static float MonopolyDrawerSlide(float progress) => 272 * MathF.Pow(1 - progress, 3);

    private static bool IsMonopolyDrawerHandle(BoardButton button) => button.Id is "mp-exit" or "mp-exit-cancel";

    // One mirrored, physically proportioned silhouette supplies both the visible
    // arrow and its camera trigger bounds. Font caret metrics cannot drift apart.
    private static Vector2[] MonopolyDrawerArrowVertices(BoardButton button, double aspect)
    {
        aspect = double.IsFinite(aspect) ? Math.Clamp(aspect, .2, 5) : 1;
        float x = aspect >= 1 ? (float)(1 / aspect) : 1;
        float y = (aspect >= 1 ? 1 : (float)aspect) * (button.Id == "mp-exit" ? -1 : 1);
        var center = new Vector2((float)(button.Bounds.X + button.Bounds.Width / 2) * BoardSurfaceSize,
            (float)(button.Bounds.Y + button.Bounds.Height / 2) * BoardSurfaceSize);
        Vector2[] profile = [new(-22, -6), new(0, 9), new(22, -6),
            new(18.5f, -9), new(0, 3), new(-18.5f, -9)];
        return profile.Select(point => center + new Vector2(point.X * x, point.Y * y)).ToArray();
    }

    private static Rect MonopolyDrawerArrowInk(BoardButton button, double aspect)
    {
        var points = MonopolyDrawerArrowVertices(button, aspect);
        float left = points.Min(point => point.X), top = points.Min(point => point.Y);
        return new(left, top, points.Max(point => point.X) - left, points.Max(point => point.Y) - top);
    }

    private static void DrawMonopolyDrawerArrow(CanvasDrawingSession ds, BoardButton button, double aspect, bool hovered)
    {
        var vertices = MonopolyDrawerArrowVertices(button, aspect);
        using var arrow = CanvasGeometry.CreatePolygon(ds.Device, vertices);
        using var shadow = CanvasGeometry.CreatePolygon(ds.Device,
            vertices.Select(point => point + new Vector2(0, 1.3f)).ToArray());
        ds.FillGeometry(shadow, ThemeColor(2, 18, 10, 180));
        var ink = MonopolyDrawerArrowInk(button, aspect);
        using var gold = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = button.Enabled ? ThemeColor(255, 241, 187) : ThemeColor(143, 158, 139) },
            new() { Position = .45f, Color = button.Enabled ? MonopolyGold : ThemeColor(115, 136, 113) },
            new() { Position = 1, Color = button.Enabled ? ThemeColor(174, 127, 49) : ThemeColor(81, 103, 85) }
        ]) { StartPoint = new((float)ink.X, (float)ink.Y), EndPoint = new((float)ink.X, (float)ink.Bottom) };
        ds.FillGeometry(arrow, gold);
        using var bevel = new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round };
        ds.DrawGeometry(arrow, button.Enabled ? hovered ? MonopolyIvory : ThemeColor(248, 222, 151)
            : ThemeColor(140, 159, 133), .65f, bevel);
    }

    private static void DrawMonopolyDrawer(CanvasDrawingSession ds, MonopolySnapshot game, float slide)
    {
        var previous = ds.Transform;
        ds.Transform = System.Numerics.Matrix3x2.CreateTranslation(0, slide) * previous;
        try
        {
            DrawMonopolyPlaque(ds, new Rect(260, 550, 480, 272), true);
            MonopolyText(ds, game.Phase == MonopolyPhase.Saving ? "SAVING YOUR GAME" : "TABLE OPTIONS",
                new Rect(280, 568, 440, 30), 18, MonopolyGold);
            MonopolyText(ds, game.IsActiveGame ? "Save your game and return to the board menu." : "Return to the board menu.",
                new Rect(280, 606, 440, 22), 14, MonopolyMuted);
            if (game.Phase is MonopolyPhase.ExitConfirmation or MonopolyPhase.Saving)
                MonopolyText(ds, game.Status, new Rect(280, 719, 440, 27), 13, MonopolyMuted, wrap: true);
        }
        finally { ds.Transform = previous; }
    }
}
