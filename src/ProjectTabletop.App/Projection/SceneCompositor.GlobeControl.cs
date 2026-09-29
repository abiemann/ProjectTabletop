using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private readonly Func<DateTimeOffset> _globeClock;
    private CanvasRenderTarget? _globePreviewTarget;
    public double GlobePreviewAspect { get { lock (_gate) return PaintBoardAspect(); } }
    public GlobeSnapshot GlobeState { get { lock (_gate) return _boardSession.GetGlobeSnapshot(_globeClock()); } }

    // Animation may invalidate the visual texture, but never gesture barriers
    // or the camera's stationary control reference. Quantize to display frames.
    private static long GlobeVisualFrame(DateTimeOffset now) => now.UtcTicks / (TimeSpan.TicksPerSecond / 60);

    public void ShowGlobe()
    {
        lock (_gate)
        {
            CancelBoardReveal();
            _blackOutput = false;
            _boardSession.ShowGlobe(_globeClock());
            SyncPhotoCopySession();
        }
    }

    public bool ActivateGlobeButton(string id)
    {
        lock (_gate)
        {
            if (_boardSession.Screen != BoardScreen.Globe || IsBoardRevealActive) return false;
            bool changed = _boardSession.ActivateButton(id, _globeClock());
            if (changed) SyncPhotoCopySession();
            return changed;
        }
    }

    public bool ActivateGlobeAt(double u, double v)
    {
        lock (_gate)
        {
            var button = _boardSession.Buttons.FirstOrDefault(item => item.Enabled && item.Bounds.Contains(u, v));
            return button is not null && ActivateGlobeButton(button.Id);
        }
    }

    public void DrawGlobePreview(CanvasDrawingSession ds, float width, float height)
    {
        ds.Clear(Colors.Black);
        if (width <= 0 || height <= 0) return;
        lock (_gate)
        {
            if (_disposed || _boardSession.Screen != BoardScreen.Globe) return;
            var now = _globeClock();
            _boardSession.TickGlobe(now);
            double aspect = PaintBoardAspect();
            double drawWidth = Math.Min(width, height * aspect), drawHeight = drawWidth / aspect;
            ReserveBoardPixels(ds.Device, drawWidth * ds.Dpi / 96, drawHeight * ds.Dpi / 96);
            EnsureBoardRenderTarget(ref _globePreviewTarget, ds.Device);
            using (var surface = _globePreviewTarget!.CreateDrawingSession())
            {
                surface.Transform = BoardRasterTransform(_globePreviewTarget);
                DrawGlobeBoard(surface, GlobeState, _boardSession.Buttons,
                    HoveredBoardButtons, CurrentFingerSelectionFeedback, aspect,
                    drawerOpen: _boardSession.GlobeDrawerOpen, drawerProgress: GlobeDrawerProgress(now));
            }
            var rendered = _globePreviewTarget;
            ds.DrawImage(rendered,
                new Rect((width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight),
                new Rect(0, 0, rendered.SizeInPixels.Width, rendered.SizeInPixels.Height));
        }
    }
}
