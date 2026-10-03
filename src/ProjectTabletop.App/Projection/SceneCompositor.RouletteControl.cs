using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private CanvasRenderTarget? _roulettePreviewTarget;
    private (long Revision, double Aspect, string Hover, int SelectionStep)? _roulettePreviewKey;
    public RouletteSnapshot RouletteState { get { lock (_gate) return _boardSession.RouletteState; } }
    public double RoulettePreviewAspect { get { lock (_gate) return PaintBoardAspect(); } }

    public void ShowRoulette()
    {
        lock (_gate)
        {
            CancelBoardReveal(); _blackOutput = false;
            _boardSession.ShowRoulette(_blackjackClock()); SyncPhotoCopySession();
        }
    }

    public bool ActivateRouletteButton(string id)
    {
        lock (_gate)
        {
            if (_boardSession.Screen != BoardScreen.Roulette || IsBoardRevealActive) return false;
            bool result = _boardSession.ActivateButton(id, _blackjackClock());
            if (result) SyncPhotoCopySession();
            return result;
        }
    }

    public bool ActivateRouletteAt(double u, double v)
    {
        lock (_gate)
        {
            var button = _boardSession.Buttons.FirstOrDefault(item => item.Enabled && item.Bounds.Contains(u, v));
            return button is not null && ActivateRouletteButton(button.Id);
        }
    }

    public void DrawRoulettePreview(CanvasDrawingSession ds, float width, float height)
    {
        ds.Clear(Colors.Black);
        if (width <= 0 || height <= 0) return;
        lock (_gate)
        {
            if (_disposed || _boardSession.Screen != BoardScreen.Roulette) return;
            var now = _blackjackClock(); _boardSession.TickRoulette(now);
            double aspect = PaintBoardAspect();
            double w = Math.Min(width, height * aspect), h = w / aspect;
            ReserveBoardPixels(ds.Device, w * ds.Dpi / 96, h * ds.Dpi / 96);
            if (EnsureBoardRenderTarget(ref _roulettePreviewTarget, ds.Device)) _roulettePreviewKey = null;
            var game = _boardSession.RouletteState;
            var hovered = HoveredBoardButtons;
            var feedback = CurrentFingerSelectionFeedback;
            var key = (game.Revision, aspect, string.Join('|', hovered), FingerSelectionRenderStep(feedback));
            if (_roulettePreviewKey != key)
            {
                using var drawing = _roulettePreviewTarget!.CreateDrawingSession();
                drawing.Transform = BoardRasterTransform(_roulettePreviewTarget);
                DrawRouletteBoard(drawing, game, _boardSession.Buttons, hovered, feedback, aspect);
                _roulettePreviewKey = key;
            }
            var destination = new Rect((width - w) / 2, (height - h) / 2, w, h);
            var rendered = _roulettePreviewTarget!;
            ds.DrawImage(rendered, destination, new Rect(0, 0, rendered.SizeInPixels.Width, rendered.SizeInPixels.Height));
            if (DrawRouletteMotionLayer(ds.Device, now, aspect) is { } motion)
                ds.DrawImage(motion, destination, new Rect(0, 0, motion.SizeInPixels.Width, motion.SizeInPixels.Height));
            if (DrawHoldFeedbackLayer(ds.Device) is { } rim)
                ds.DrawImage(rim, destination, new Rect(0, 0, rim.SizeInPixels.Width, rim.SizeInPixels.Height));
        }
    }

    private void DrawRouletteMenuPreview(CanvasDrawingSession ds, float span)
    {
        // Real roulette geometry gives the new menu card the same jewellery
        // detail as its table, without prerendered letters or a fake wheel.
        ds.Clear(ThemeColor(8, 28, 35));
        var game = new RouletteGame(7).Snapshot;
        DrawRouletteWheel(ds, game, DateTimeOffset.UnixEpoch, new(span * .70f, span * .52f), 1, span / 390);
        DrawRouletteChip(ds, new(span * .35f, span * .80f), span * .08f, 25, 1, false);
        DrawRouletteChip(ds, new(span * .20f, span * .76f), span * .075f, 5, 1, false);
    }
}
