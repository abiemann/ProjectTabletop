using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private readonly Func<DateTimeOffset> _monopolyClock;
    private CanvasRenderTarget? _monopolyPreviewTarget;
    private (long Revision, string Hover, int Feedback, double Aspect)? _monopolyPreviewState;
    public double MonopolyPreviewAspect { get { lock (_gate) return PaintBoardAspect(); } }

    public MonopolySnapshot MonopolyState { get { lock (_gate) return _boardSession.MonopolyState; } }

    public void ShowMonopoly()
    {
        lock (_gate)
        {
            CancelBoardReveal();
            _blackOutput = false;
            _boardSession.ShowMonopoly(_monopolyClock());
            SyncPhotoCopySession();
        }
    }

    public bool TickMonopoly(DateTimeOffset now)
    {
        lock (_gate) return _boardSession.TickMonopoly(now);
    }

    public bool ActivateMonopolyButton(string id)
    {
        lock (_gate)
        {
            if (_boardSession.Screen != BoardScreen.Monopoly || IsBoardRevealActive) return false;
            bool changed = _boardSession.ActivateButton(id, _monopolyClock());
            if (changed) SyncPhotoCopySession();
            return changed;
        }
    }

    public bool ActivateMonopolyAt(double u, double v)
    {
        lock (_gate)
        {
            var button = _boardSession.Buttons.FirstOrDefault(item => item.Enabled && item.Bounds.Contains(u, v));
            return button is not null && ActivateMonopolyButton(button.Id);
        }
    }

    public bool LoadMonopolySave(string json, bool resume = false)
    {
        lock (_gate) return _boardSession.LoadMonopolySave(json, _monopolyClock(), resume);
    }

    public string ExportMonopolySave()
    {
        lock (_gate) return _boardSession.ExportMonopolySave();
    }

    public bool TryGetMonopolySaveRequest(out long requestId, out string json)
    {
        lock (_gate)
        {
            requestId = _boardSession.MonopolySaveRequestId;
            json = string.Empty;
            if (!_boardSession.MonopolySaveRequested) return false;
            json = _boardSession.ExportMonopolySave();
            return true;
        }
    }

    public bool CompleteMonopolySave(long requestId, bool success, string? error = null)
    {
        lock (_gate) return _boardSession.CompleteMonopolySave(requestId, success, _monopolyClock(), error);
    }

    // The laptop view is playable without a camera or projector. Its board UVs
    // and button rectangles are the same ones used by projected gestures.
    public void DrawMonopolyPreview(CanvasDrawingSession ds, float width, float height)
    {
        ds.Clear(Colors.Black);
        if (width <= 0 || height <= 0) return;
        lock (_gate)
        {
            if (_disposed || _boardSession.Screen != BoardScreen.Monopoly) return;
            _boardSession.TickMonopoly(_monopolyClock());
            double aspect = PaintBoardAspect();
            double drawWidth = Math.Min(width, height * aspect);
            double drawHeight = drawWidth / aspect;
            ReserveBoardPixels(ds.Device, drawWidth * ds.Dpi / 96, drawHeight * ds.Dpi / 96);
            if (EnsureBoardRenderTarget(ref _monopolyPreviewTarget, ds.Device)) _monopolyPreviewState = null;
            var state = _boardSession.MonopolyState;
            var hovered = HoveredBoardButtons;
            var feedback = CurrentFingerSelectionFeedback;
            var key = (state.Revision, string.Join(",", hovered), FingerSelectionRenderStep(feedback), aspect);
            if (_monopolyPreviewState != key)
            {
                using var surface = _monopolyPreviewTarget!.CreateDrawingSession();
                surface.Transform = BoardRasterTransform(_monopolyPreviewTarget);
                DrawMonopolyBoard(surface, state, _boardSession.Buttons, hovered, feedback, aspect);
                _monopolyPreviewState = key;
            }
            var rendered = _monopolyPreviewTarget!;
            ds.DrawImage(rendered,
                new Rect((width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight),
                new Rect(0, 0, rendered.SizeInPixels.Width, rendered.SizeInPixels.Height));
        }
    }
}
