using Microsoft.Graphics.Canvas;
using Microsoft.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private CanvasRenderTarget? _monopolyDiceTarget;
    private (long Revision, long Frame, double Aspect)? _monopolyDiceLayerState;

    // Dice redraw independently of the expensive board texture. Settled dice
    // reuse the same native overlay on both the projector and laptop preview.
    private CanvasRenderTarget? DrawMonopolyDiceLayer(CanvasDevice device, DateTimeOffset now, double aspect)
    {
        if (!HasMonopolyDicePresentation(now)) return null;
        if (EnsureBoardRenderTarget(ref _monopolyDiceTarget, device)) _monopolyDiceLayerState = null;
        long frame = HasMonopolyDiceAnimation(now) ? now.UtcTicks / (TimeSpan.TicksPerSecond / 60) : long.MaxValue;
        var key = (MonopolyDicePresentationRevision, frame, aspect);
        if (_monopolyDiceLayerState != key)
        {
            using var drawing = _monopolyDiceTarget!.CreateDrawingSession();
            drawing.Transform = BoardRasterTransform(_monopolyDiceTarget);
            drawing.Clear(Colors.Transparent);
            DrawMonopolyDiceAnimation(drawing, now, aspect);
            _monopolyDiceLayerState = key;
        }
        return _monopolyDiceTarget;
    }

    private void DisposeMonopolyDiceLayer()
    {
        _monopolyDiceTarget?.Dispose();
        _monopolyDiceTarget = null;
        _monopolyDiceLayerState = null;
    }
}
