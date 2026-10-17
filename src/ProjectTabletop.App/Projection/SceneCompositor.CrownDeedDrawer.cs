using Microsoft.Graphics.Canvas;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    public bool CrownDeedDrawerOpen { get { lock (_gate) return _boardSession.CrownDeedDrawerOpen; } }

    private float CrownDeedDrawerProgress(DateTimeOffset now)
    {
        if (!_boardSession.CrownDeedDrawerOpen || _boardSession.CrownDeedDrawerOpenedAt is not { } opened) return 1;
        return Math.Clamp((float)((now - opened).TotalMilliseconds /
            BoardSession.CrownDeedDrawerOpeningDuration.TotalMilliseconds), 0, 1);
    }

    private bool HasCrownDeedDrawerAnimation(DateTimeOffset now) =>
        _boardSession.Screen == BoardScreen.CrownDeed && _boardSession.CrownDeedDrawerOpen && CrownDeedDrawerProgress(now) < 1;

    // Only the brief drawer movement redraws the native board; the settled
    // drawer returns to the ordinary static UI cache.
    private int CrownDeedDrawerFrame(DateTimeOffset now) => !_boardSession.CrownDeedDrawerOpen ? -1 :
        (int)Math.Floor(CrownDeedDrawerProgress(now) * 10);

    public object GetCrownDeedDrawerDiagnostics(DateTimeOffset now)
    {
        lock (_gate) return new { open = _boardSession.CrownDeedDrawerOpen,
            openedAt = _boardSession.CrownDeedDrawerOpenedAt,
            durationMilliseconds = BoardSession.CrownDeedDrawerOpeningDuration.TotalMilliseconds,
            progress = CrownDeedDrawerProgress(now), animating = HasCrownDeedDrawerAnimation(now) };
    }

    private static float CrownDeedDrawerSlide(float progress) => 272 * MathF.Pow(1 - progress, 3);

    private static bool IsCrownDeedDrawerHandle(BoardButton button) => button.Id is "mp-exit" or "mp-exit-cancel";

    // One mirrored, physically proportioned silhouette supplies both the visible
    // arrow and its camera trigger bounds. Font caret metrics cannot drift apart.
    private static Rect CrownDeedDrawerArrowInk(BoardButton button, double aspect) =>
        DrawerArrowInk(button, aspect);

    private static void DrawCrownDeedDrawerArrow(CanvasDrawingSession ds, BoardButton button, double aspect, bool hovered)
    {
        var vertices = DrawerArrowVertices(button, aspect);
        using var arrow = DrawerArrowGeometry(ds.Device, button, vertices, aspect);
        ds.FillGeometry(arrow, button.Enabled ? ThemeColor(0, 0, 0) : ThemeColor(91, 100, 91));
    }

    private static void DrawCrownDeedDrawer(CanvasDrawingSession ds, CrownDeedSnapshot game, float slide)
    {
        var previous = ds.Transform;
        ds.Transform = System.Numerics.Matrix3x2.CreateTranslation(0, slide) * previous;
        try
        {
            DrawCrownDeedPlaque(ds, new Rect(260, 550, 480, 272), true);
            CrownDeedText(ds, game.Phase == CrownDeedPhase.Saving ? "SAVING YOUR GAME" : "TABLE OPTIONS",
                new Rect(280, 568, 440, 30), 18, CrownDeedGold);
            CrownDeedText(ds, game.IsActiveGame ? "Save your game and return to the board menu." : "Return to the board menu.",
                new Rect(280, 606, 440, 22), 14, CrownDeedMuted);
            if (game.Phase is CrownDeedPhase.ExitConfirmation or CrownDeedPhase.Saving)
                CrownDeedText(ds, game.Status, new Rect(280, 719, 440, 27), 13, CrownDeedMuted, wrap: true);
        }
        finally { ds.Transform = previous; }
    }
}
