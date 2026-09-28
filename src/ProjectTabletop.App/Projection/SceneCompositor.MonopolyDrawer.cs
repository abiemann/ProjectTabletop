using Microsoft.Graphics.Canvas;
using ProjectTabletop.Interaction;
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
