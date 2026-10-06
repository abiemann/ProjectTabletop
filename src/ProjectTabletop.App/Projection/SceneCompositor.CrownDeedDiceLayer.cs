using Microsoft.Graphics.Canvas;
using Microsoft.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private CanvasRenderTarget? _crownDeedDiceTarget;
    private (long Revision, long Frame, double Aspect)? _crownDeedDiceLayerState;

    // Dice redraw independently of the expensive board texture. Settled dice
    // reuse the same native overlay on both the projector and laptop preview.
    private CanvasRenderTarget? DrawCrownDeedDiceLayer(CanvasDevice device, DateTimeOffset now, double aspect)
    {
        if (!HasCrownDeedDicePresentation(now)) return null;
        if (EnsureBoardRenderTarget(ref _crownDeedDiceTarget, device)) _crownDeedDiceLayerState = null;
        long frame = HasCrownDeedDiceAnimation(now) ? now.UtcTicks / (TimeSpan.TicksPerSecond / 60) : long.MaxValue;
        var key = (CrownDeedDicePresentationRevision, frame, aspect);
        if (_crownDeedDiceLayerState != key)
        {
            using var drawing = _crownDeedDiceTarget!.CreateDrawingSession();
            drawing.Transform = BoardRasterTransform(_crownDeedDiceTarget);
            drawing.Clear(Colors.Transparent);
            DrawCrownDeedDiceAnimation(drawing, now, aspect);
            _crownDeedDiceLayerState = key;
        }
        return _crownDeedDiceTarget;
    }

    private void DisposeCrownDeedDiceLayer()
    {
        _crownDeedDiceTarget?.Dispose();
        _crownDeedDiceTarget = null;
        _crownDeedDiceLayerState = null;
    }
}
