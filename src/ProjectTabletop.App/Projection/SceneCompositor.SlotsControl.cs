using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private CanvasRenderTarget? _slotsPreviewTarget;
    private (long Revision, double Aspect)? _slotsPreviewKey;
    public SlotSnapshot SlotsState { get { lock (_gate) return _boardSession.SlotsState; } }
    public double SlotsPreviewAspect { get { lock (_gate) return PaintBoardAspect(); } }

    public void ShowSlots()
    {
        lock (_gate)
        {
            CancelBoardReveal();
            _blackOutput = false;
            _boardSession.ShowSlots(_blackjackClock());
            SyncPhotoCopySession();
        }
    }

    public void ShowSettings()
    {
        lock (_gate)
        {
            CancelBoardReveal();
            _blackOutput = false;
            _boardSession.ShowSettings(_blackjackClock());
            SyncPhotoCopySession();
        }
    }

    /// <summary>Makes the next spin land Dragonfire Respins, free spins or the Treasure Vault.</summary>
    public void DemonstrateSlots(SlotDemo demo)
    {
        lock (_gate) _boardSession.DemonstrateSlots(demo);
    }

    public bool TickSlots(DateTimeOffset now)
    {
        lock (_gate) return _boardSession.TickSlots(now);
    }

    public bool ActivateSlotsButton(string id)
    {
        lock (_gate)
        {
            if (_boardSession.Screen != BoardScreen.Slots || IsBoardRevealActive || BlockBoardArtworkInput()) return false;
            bool changed = _boardSession.ActivateButton(id, _blackjackClock());
            if (changed) SyncPhotoCopySession();
            return changed;
        }
    }

    public bool ActivateSlotsAt(double u, double v)
    {
        lock (_gate)
        {
            var button = _boardSession.Buttons.FirstOrDefault(item => item.Enabled && item.Bounds.Contains(u, v));
            return button is not null && ActivateSlotsButton(button.Id);
        }
    }

    // Laptop-only preview in board UVs, at the projector's board aspect.
    public void DrawSlotsPreview(CanvasDrawingSession ds, float width, float height)
    {
        ds.Clear(Colors.Black);
        if (width <= 0 || height <= 0) return;
        lock (_gate)
        {
            if (_disposed || _boardSession.Screen != BoardScreen.Slots) return;
            if (!PrepareBoardArtwork(ds.Device)) { DrawArtworkLoading(ds, new Rect(0, 0, width, height)); return; }
            var now = _blackjackClock();
            _boardSession.TickSlots(now);
            double aspect = PaintBoardAspect();
            double drawWidth = Math.Min(width, height * aspect), drawHeight = drawWidth / aspect;
            ReserveBoardPixels(ds.Device, drawWidth * ds.Dpi / 96, drawHeight * ds.Dpi / 96);
            if (EnsureBoardRenderTarget(ref _slotsPreviewTarget, ds.Device)) _slotsPreviewKey = null;
            var game = _boardSession.SlotsState;
            var key = (game.Revision, aspect);
            if (_slotsPreviewKey != key)
            {
                using var surface = _slotsPreviewTarget!.CreateDrawingSession();
                surface.Transform = BoardRasterTransform(_slotsPreviewTarget);
                DrawSlotsMachine(surface, game, _boardSession.Buttons, aspect);
                _slotsPreviewKey = key;
            }
            var rendered = _slotsPreviewTarget!;
            var destination = new Rect((width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight);
            ds.DrawImage(rendered, destination,
                new Rect(0, 0, rendered.SizeInPixels.Width, rendered.SizeInPixels.Height));
            // Use the same cached cabinet and live effect layer as the projector;
            // the laptop preview must not rebuild all its ornament every frame.
            if (DrawSlotsReelLayer(ds.Device, now, aspect) is { } reels)
                ds.DrawImage(reels, destination, new Rect(0, 0, reels.SizeInPixels.Width, reels.SizeInPixels.Height));
            if (DrawHoldFeedbackLayer(ds.Device) is { } rims)
                ds.DrawImage(rims, destination, new Rect(0, 0, rims.SizeInPixels.Width, rims.SizeInPixels.Height));
        }
    }
}
