using Microsoft.Graphics.Canvas;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // A single clock and opacity apply to the entire control group. Input uses
    // BoardSession.Buttons; these presentation controls remain while fading out.
    private IReadOnlyList<BoardButton> BoardControlsForRendering
    {
        get
        {
            if (_markerSetup is not null) return [];
            var now = _blackjackClock();
            _boardSession.TickControlVisibility(now);
            SyncGlobeSaveState();
            return _boardSession.GetVisibleButtons(now);
        }
    }

    private float BoardControlsOpacity => (float)_boardSession.GetButtonsOpacity(_blackjackClock());

    private int BoardControlsVisualStep => (int)Math.Round(BoardControlsOpacity * 1000);

    private bool BoardControlsAnimating
    {
        get
        {
            var now = _blackjackClock();
            _boardSession.TickControlVisibility(now);
            return _boardSession.ControlVisibilityAnimating(now);
        }
    }

    private CanvasActiveLayer? CreateBoardControlsFadeLayer(CanvasDrawingSession drawing)
    {
        float opacity = BoardControlsOpacity;
        // The normal resting frame adds no layer or offscreen copy. During the
        // brief transition only the controls are composited, never the world.
        // A draw may have retained its controls just before the fade ended;
        // zero must still composite them invisibly instead of flashing them.
        return opacity < 1 ? drawing.CreateLayer(opacity) : null;
    }
}
