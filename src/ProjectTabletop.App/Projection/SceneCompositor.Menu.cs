using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private int MenuVisualFrame(DateTimeOffset now) => _boardSession.Screen == BoardScreen.Menu
        ? (int)Math.Round(_boardSession.GetMenuScrollOffset(now) * 100000) * 2 + (_boardSession.MenuScrolling ? 1 : 0) : -1;

    private static bool IsMenuScrollHandle(BoardButton button) => button.Id is "menu-scroll-down" or "menu-scroll-up";

    // Reuse the exact Globe handle silhouette, including its physical aspect
    // correction, for both the visible arrow and camera caption-ink bounds.
    private static BoardButton MenuArrowAppearance(BoardButton button) =>
        button with { Id = button.Id == "menu-scroll-up" ? "globe-drawer-open" : "globe-drawer-close" };

    private void DrawMenuSurface(CanvasDrawingSession ds, DateTimeOffset now, bool handsFresh,
        CanvasTextFormat heading, CanvasTextFormat label, CanvasTextFormat small,
        IReadOnlyList<BoardFingerSelectionFeedback> selectionFeedback)
    {
        ds.DrawText("PROJECT TABLETOP", 80, 51, AppPalette.MutedText, small);
        ds.DrawText("07  /  BOARDS", 470, 54, AppPalette.AccentSecondary, small);
        ds.DrawText("Choose a board", 76, 99, AppPalette.Text, heading);
        var viewport = BoardSession.MenuCardViewport;
        using (ds.CreateLayer(1, new Rect(viewport.X * BoardSurfaceSize, viewport.Y * BoardSurfaceSize,
            viewport.Width * BoardSurfaceSize, viewport.Height * BoardSurfaceSize)))
        {
            foreach (var button in _boardSession.GetMenuCards(now))
            {
                if (button.Bounds.Y >= viewport.Y + viewport.Height ||
                    button.Bounds.Y + button.Bounds.Height + .01 <= viewport.Y) continue;
                DrawMenuButton(ds, button, !_boardSession.MenuScrolling && handsFresh &&
                    _boardSession.HoveredButtonIds.Contains(button.Id), label, small, selectionFeedback);
            }
        }
        foreach (var button in _boardSession.Buttons)
        {
            bool hovered = button.Enabled && handsFresh && _boardSession.HoveredButtonIds.Contains(button.Id);
            if (button.Id == "settings") DrawSettingsCogButton(ds, button, hovered, selectionFeedback);
            else if (IsMenuScrollHandle(button))
                DrawBoardDrawerHandle(ds, MenuArrowAppearance(button), hovered, PaintBoardAspect());
        }
        ds.DrawText("Aim with four fingers together.", 80, 907, AppPalette.MutedText, small);
        ds.DrawText("Move your index sideways to open.", 80, 937, AppPalette.MutedText, small);
    }
}
