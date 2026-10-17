using Microsoft.Graphics.Canvas;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    public bool PaintDrawerOpen { get { lock (_gate) return _boardSession.PaintDrawerOpen; } }

    public bool TickPaint(DateTimeOffset now)
    {
        lock (_gate) return _boardSession.TickPaint(now);
    }

    public bool ActivatePaintButton(string id)
    {
        lock (_gate)
        {
            if (_boardSession.Screen != BoardScreen.Paint || IsBoardRevealActive || _markerSetup is not null) return false;
            var now = _paintClock();
            _boardSession.PaintSaveEnabled = CanSavePaint;
            bool changed = _boardSession.ActivateButton(id, now);
            if (changed)
            {
                SyncPaintSession();
                NotePaintUserActivity();
                if (id == "paint-save") QueuePaintSaveRequest(now);
            }
            return changed;
        }
    }

    private bool HasPaintDrawerAnimation(DateTimeOffset now)
    {
        _boardSession.TickPaint(now);
        return _boardSession.PaintDrawerOpen && _boardSession.GetPaintDrawerProgress(now) < 1;
    }

    private int PaintDrawerVisualFrame(DateTimeOffset now) => _boardSession.PaintDrawerOpen
        ? 1 + (int)Math.Round(_boardSession.GetPaintDrawerProgress(now) * 100000) : 0;

    private void DrawPaintControls(CanvasDrawingSession ds, IReadOnlyList<BoardButton> buttons,
        IReadOnlyList<string> hovered, IReadOnlyList<BoardFingerSelectionFeedback> feedback, DateTimeOffset now)
    {
        if (buttons.Count == 0) return;
        using var fade = CreateBoardControlsFadeLayer(ds);
        DrawBoardDrawerControls(ds, buttons, hovered, feedback, _boardSession.PaintDrawerOpen,
            (float)_boardSession.GetPaintDrawerProgress(now), PaintBoardAspect(), _holdControlBrightness);
    }

    private bool PaintControlsInMotion => HasPaintDrawerAnimation(_paintClock()) || BoardControlsAnimating ||
        ControlSwipeTraversalActive(_blackjackClock()) || _blackjackClock() < _controlSwipeRearmAt;
}
