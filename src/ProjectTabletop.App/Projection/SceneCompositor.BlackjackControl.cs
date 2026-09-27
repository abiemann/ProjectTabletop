using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private CanvasRenderTarget? _blackjackPreviewTarget;
    private long _blackjackPreviewRevision = -1;
    private string _blackjackPreviewHover = string.Empty;

    public BlackjackSnapshot BlackjackState { get { lock (_gate) return _boardSession.BlackjackState; } }
    public IReadOnlyList<BoardButton> CurrentBoardButtons { get { lock (_gate) return _boardSession.Buttons.ToArray(); } }

    public void ShowBlackjack()
    {
        lock (_gate)
        {
            _blackOutput = false;
            _boardSession.ShowBlackjack();
            SyncPhotoCopySession();
        }
    }

    public bool TickBlackjack(DateTimeOffset now)
    {
        lock (_gate) return _boardSession.TickBlackjack(now);
    }

    public bool ActivateBlackjackButton(string id)
    {
        lock (_gate)
        {
            if (_boardSession.Screen != BoardScreen.Blackjack) return false;
            bool changed = _boardSession.ActivateButton(id, DateTimeOffset.UtcNow);
            if (changed) SyncPhotoCopySession();
            return changed;
        }
    }

    public bool ActivateBlackjackAt(double u, double v)
    {
        lock (_gate)
        {
            var button = _boardSession.Buttons.FirstOrDefault(item => item.Enabled && item.Bounds.Contains(u, v));
            return button is not null && ActivateBlackjackButton(button.Id);
        }
    }

    // This unmapped preview is laptop-only: it never supplies a projector clip
    // or enables hardware. Both click hit testing and rendering use board UVs.
    public void DrawBlackjackPreview(CanvasDrawingSession ds, float width, float height)
    {
        ds.Clear(Colors.Black);
        if (width <= 0 || height <= 0) return;
        lock (_gate)
        {
            if (_disposed || _boardSession.Screen != BoardScreen.Blackjack) return;
            _boardSession.TickBlackjack(DateTimeOffset.UtcNow);
            if (_blackjackPreviewTarget is null || _blackjackPreviewTarget.Device != ds.Device)
            {
                _blackjackPreviewTarget?.Dispose();
                _blackjackPreviewTarget = new CanvasRenderTarget(ds.Device, BoardSurfaceSize, BoardSurfaceSize, 96);
                _blackjackPreviewRevision = -1;
            }
            var game = _boardSession.BlackjackState;
            var hovered = HoveredBoardButtons;
            var hoverKey = string.Join(",", hovered);
            if (_blackjackPreviewRevision != game.Revision || _blackjackPreviewHover != hoverKey)
            {
                using var surface = _blackjackPreviewTarget.CreateDrawingSession();
                DrawBlackjackTable(surface, game, _boardSession.Buttons, hovered);
                _blackjackPreviewRevision = game.Revision;
                _blackjackPreviewHover = hoverKey;
            }
            float size = Math.Min(width, height);
            ds.DrawImage(_blackjackPreviewTarget, new Rect((width - size) / 2, (height - size) / 2, size, size),
                new Rect(0, 0, BoardSurfaceSize, BoardSurfaceSize));
        }
    }
}
