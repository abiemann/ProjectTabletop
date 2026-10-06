using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private readonly Func<DateTimeOffset> _globeClock;
    private CanvasRenderTarget? _globePreviewTarget;
    private GlobeFrameState? _globeRenderedFrame;
    private BoardButton[] _globeRenderedButtons = [];
    private long _globeFrameRenderCount;
    private readonly record struct GlobeFrameState(long Frame, long GlobeRevision, long SessionRevision,
        double Aspect, int HoverMask, int FeedbackStep, bool DrawerOpen, bool Ready, string? Error);
    public double GlobePreviewAspect { get { lock (_gate) return PaintBoardAspect(); } }
    public GlobeSnapshot GlobeState { get { lock (_gate) return _boardSession.GetGlobeSnapshot(_globeClock()); } }

    // Animation may invalidate the visual texture, but never gesture barriers
    // or the camera's stationary control reference. Quantize to display frames.
    private static long GlobeVisualFrame(DateTimeOffset now) => now.UtcTicks / (TimeSpan.TicksPerSecond / 60);

    // The projector and the upright laptop preview sample one native-resolution
    // scene. Only their final placement differs; neither view reruns the Earth
    // shader for an already rendered visual frame.
    private CanvasRenderTarget GetGlobeFrame(CanvasDevice device, DateTimeOffset now, GlobeSnapshot state,
        IReadOnlyList<BoardButton> buttons, IReadOnlyList<string> hovered,
        IReadOnlyList<BoardFingerSelectionFeedback> selectionFeedback, double aspect,
        bool drawerOpen, float drawerProgress)
    {
        var renderer = GetGlobeRenderer(device);
        if (EnsureBoardRenderTarget(ref _globePreviewTarget, device)) _globeRenderedFrame = null;
        int hoverMask = 0;
        for (int index = 0; index < buttons.Count; index++)
            if (hovered.Contains(buttons[index].Id)) hoverMask |= 1 << index;
        var frame = new GlobeFrameState(GlobeVisualFrame(now), state.Revision, _boardSession.Revision,
            aspect, hoverMask, FingerSelectionRenderStep(selectionFeedback), drawerOpen,
            renderer.IsReady, renderer.Error);
        if (_globeRenderedFrame != frame || !_globeRenderedButtons.SequenceEqual(buttons))
        {
            using (var surface = _globePreviewTarget!.CreateDrawingSession())
            {
                surface.Transform = BoardRasterTransform(_globePreviewTarget);
                surface.Clear(Colors.Black);
                DrawGlobeBoard(surface, state, buttons, hovered, selectionFeedback, aspect, drawerOpen, drawerProgress);
            }
            _globeRenderedFrame = frame;
            _globeRenderedButtons = buttons.ToArray();
            _globeFrameRenderCount++;
        }
        return _globePreviewTarget!;
    }

    private void DrawCachedGlobeBoard(CanvasDrawingSession drawing, DateTimeOffset now, GlobeSnapshot state,
        IReadOnlyList<BoardButton> buttons, IReadOnlyList<string> hovered,
        IReadOnlyList<BoardFingerSelectionFeedback> selectionFeedback, double boardAspect = 1,
        bool drawerOpen = false, float drawerProgress = 1)
    {
        var frame = GetGlobeFrame(drawing.Device, now, state, buttons, hovered, selectionFeedback,
            boardAspect, drawerOpen, drawerProgress);
        drawing.DrawImage(frame, new Rect(0, 0, BoardSurfaceSize, BoardSurfaceSize),
            new Rect(0, 0, frame.SizeInPixels.Width, frame.SizeInPixels.Height), 1,
            CanvasImageInterpolation.NearestNeighbor);
    }

#if DEBUG
    internal (long RenderCount, int Width, int Height, CanvasRenderTarget? Target) GetGlobeFrameCacheForVerification()
    {
        lock (_gate) return (_globeFrameRenderCount, (int)(_globePreviewTarget?.SizeInPixels.Width ?? 0),
            (int)(_globePreviewTarget?.SizeInPixels.Height ?? 0), _globePreviewTarget);
    }
#endif

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
            var rendered = GetGlobeFrame(ds.Device, now, _boardSession.GetGlobeSnapshot(now), _boardSession.Buttons,
                HoveredBoardButtons, CurrentFingerSelectionFeedback, aspect,
                _boardSession.GlobeDrawerOpen, GlobeDrawerProgress(now));
            ds.DrawImage(rendered,
                new Rect((width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight),
                new Rect(0, 0, rendered.SizeInPixels.Width, rendered.SizeInPixels.Height));
        }
    }
}
