using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private readonly Func<DateTimeOffset> _crownDeedClock;
    private CanvasRenderTarget? _crownDeedPreviewTarget;
    private (long Revision, long DiceRevision, long SessionRevision, int DrawerFrame, long EntranceRevision,
        int EntranceFrame, long DevelopmentFrame, string Hover, int Feedback, double Aspect)? _crownDeedPreviewState;
    public double CrownDeedPreviewAspect { get { lock (_gate) return PaintBoardAspect(); } }

    public CrownDeedSnapshot CrownDeedState { get { lock (_gate) return _boardSession.CrownDeedState; } }

    public void ShowCrownDeed()
    {
        lock (_gate)
        {
            CancelBoardReveal();
            CancelCrownDeedDiceAnimation();
            _blackOutput = false;
            _boardSession.ShowCrownDeed(_crownDeedClock());
            SyncPhotoCopySession();
        }
    }

    public bool TickCrownDeed(DateTimeOffset now)
    {
        lock (_gate) return GetCrownDeedEntranceFrame(now)?.Active != true && _boardSession.TickCrownDeed(now);
    }

    public bool ActivateCrownDeedButton(string id)
    {
        lock (_gate)
        {
            if (_boardSession.Screen != BoardScreen.CrownDeed || IsBoardRevealActive || BlockBoardArtworkInput() || CrownDeedEntranceActive) return false;
            bool changed = _boardSession.ActivateButton(id, _crownDeedClock());
            if (changed) SyncPhotoCopySession();
            return changed;
        }
    }

    public bool ActivateCrownDeedAt(double u, double v)
    {
        lock (_gate)
        {
            var button = _boardSession.Buttons.FirstOrDefault(item => item.Enabled && item.Bounds.Contains(u, v));
            return button is not null && ActivateCrownDeedButton(button.Id);
        }
    }

    public bool LoadCrownDeedSave(string json, bool resume = false)
    {
        lock (_gate)
        {
            bool loaded = _boardSession.LoadCrownDeedSave(json, _crownDeedClock(), resume);
            if (loaded)
            {
                CancelCrownDeedDiceAnimation();
                if (resume) CancelCrownDeedEntrance();
            }
            return loaded;
        }
    }

    public string ExportCrownDeedSave()
    {
        lock (_gate) return _boardSession.ExportCrownDeedSave();
    }

    public bool TryGetCrownDeedSaveRequest(out long requestId, out string json)
    {
        lock (_gate)
        {
            requestId = _boardSession.CrownDeedSaveRequestId;
            json = string.Empty;
            if (!_boardSession.CrownDeedSaveRequested) return false;
            json = _boardSession.ExportCrownDeedSave();
            return true;
        }
    }

    public bool CompleteCrownDeedSave(long requestId, bool success, string? error = null)
    {
        lock (_gate) return _boardSession.CompleteCrownDeedSave(requestId, success, _crownDeedClock(), error);
    }

    // The laptop view is playable without a camera or projector. Its board UVs
    // and button rectangles are the same ones used by projected gestures.
    public void DrawCrownDeedPreview(CanvasDrawingSession ds, float width, float height)
    {
        ds.Clear(Colors.Black);
        if (width <= 0 || height <= 0) return;
        lock (_gate)
        {
            if (_disposed || _boardSession.Screen != BoardScreen.CrownDeed) return;
            if (!PrepareBoardArtwork(ds.Device)) { DrawArtworkLoading(ds, new Rect(0, 0, width, height)); return; }
            var now = _crownDeedClock();
            var entrance = GetCrownDeedEntranceFrame(now);
            if (entrance?.Active != true) _boardSession.TickCrownDeed(now);
            double aspect = PaintBoardAspect();
            double drawWidth = Math.Min(width, height * aspect);
            double drawHeight = drawWidth / aspect;
            ReserveBoardPixels(ds.Device, drawWidth * ds.Dpi / 96, drawHeight * ds.Dpi / 96);
            if (EnsureBoardRenderTarget(ref _crownDeedPreviewTarget, ds.Device)) _crownDeedPreviewState = null;
            var state = CrownDeedPresentedState(now);
            bool dicePresented = HasCrownDeedDicePresentation(now);
            var hovered = HoveredBoardButtons;
            var feedback = CurrentFingerSelectionFeedback;
            var key = (state.Revision, CrownDeedDicePresentationRevision, _boardSession.Revision,
                CrownDeedDrawerFrame(now), CrownDeedEntranceRevision, CrownDeedEntranceRenderFrame(entrance), CrownDeedDevelopmentRenderFrame(now), string.Join(",", hovered),
                FingerSelectionRenderStep(feedback), aspect);
            if (_crownDeedPreviewState != key)
            {
                using var surface = _crownDeedPreviewTarget!.CreateDrawingSession();
                surface.Transform = BoardRasterTransform(_crownDeedPreviewTarget);
                DrawCrownDeedBoard(surface, state, _boardSession.Buttons, hovered, feedback, aspect,
                    hideDiceDisplay: dicePresented, rolling: HasCrownDeedDiceAnimation(now),
                    drawerOpen: _boardSession.CrownDeedDrawerOpen, drawerProgress: CrownDeedDrawerProgress(now), entrance: entrance);
                _crownDeedPreviewState = key;
            }
            var rendered = _crownDeedPreviewTarget!;
            ds.DrawImage(rendered,
                new Rect((width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight),
                new Rect(0, 0, rendered.SizeInPixels.Width, rendered.SizeInPixels.Height));
            if (DrawCrownDeedDiceLayer(ds.Device, now, aspect) is { } diceLayer)
                ds.DrawImage(diceLayer,
                    new Rect((width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight),
                    new Rect(0, 0, diceLayer.SizeInPixels.Width, diceLayer.SizeInPixels.Height));
        }
    }
}
