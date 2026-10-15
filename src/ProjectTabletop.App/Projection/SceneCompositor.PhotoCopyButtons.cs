using Microsoft.Graphics.Canvas;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private void DrawPhotoCopyControls(CanvasDrawingSession ds, IReadOnlyList<BoardButton> buttons,
        IReadOnlyList<string> hovered, IReadOnlyList<BoardFingerSelectionFeedback> feedback, DateTimeOffset now)
    {
        if (buttons.Count == 0) return;
        using var fade = CreateBoardControlsFadeLayer(ds);
        DrawBoardDrawerControls(ds, buttons, hovered, feedback, _boardSession.PhotoCopyDrawerOpen,
            (float)_boardSession.GetPhotoCopyDrawerProgress(now), PaintBoardAspect(), _holdControlBrightness);
    }
}
