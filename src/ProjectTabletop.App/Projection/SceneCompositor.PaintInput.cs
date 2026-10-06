using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private static readonly HandTrackingBounds PaintInputBounds = new(.01, .01, .98, .98);
    private static readonly BoardRect PaintTitleBounds = new(.06, .018, .10, .028);
    private static readonly BoardRect PaintStatusBounds = new(.35, .88, .30, .065);
    private HandTrackingBounds? _paintButtonLightIgnoreRegion;
    private DateTimeOffset _paintButtonLightIgnoreUntil;

    // Paint may spread beneath every overlay. Only new physical disturbances
    // inside the floating controls are excluded, so operating Save does not paint.
    private HandTrackingBounds[] PaintIgnoredRegions()
    {
        var regions = _boardSession.Buttons
        .Select(button => button.Bounds).Append(PaintTitleBounds).Append(PaintStatusBounds)
        .Select(bounds =>
        {
            // The floating chrome extends beyond its hit box (6px shadow and
            // hover glow). Include camera blur and roughly two analysis samples
            // of registration tolerance so these known edges cannot seed paint.
            const double margin = .02;
            double left = Math.Max(0, bounds.X - margin), top = Math.Max(0, bounds.Y - margin);
            return new HandTrackingBounds(left, top, Math.Min(1, bounds.X + bounds.Width + margin) - left,
                Math.Min(1, bounds.Y + bounds.Height + margin) - top);
        }).ToList();
        if (_paintButtonLightIgnoreRegion is { } light && _paintClock() <= _paintButtonLightIgnoreUntil)
            regions.Add(light);
        return regions.ToArray();
    }

    private void RetainPaintButtonLightExclusion()
    {
        if (_boardSession.Screen != BoardScreen.Paint || _acquisitionLight is not { } light ||
            _boardSurfaceMap is null) return;
        var perimeter = Enumerable.Range(0, 24).Select(index =>
        {
            double angle = index * Math.PI / 12;
            return _boardSurfaceMap.InverseTransform(new(light.Center.X +
                light.Radius / _displayAspect * Math.Cos(angle), light.Center.Y + light.Radius * Math.Sin(angle)));
        }).ToArray();
        double left = Math.Max(0, perimeter.Min(point => point.X) - .02);
        double top = Math.Max(0, perimeter.Min(point => point.Y) - .02);
        double right = Math.Min(1, perimeter.Max(point => point.X) + .02);
        double bottom = Math.Min(1, perimeter.Max(point => point.Y) + .02);
        _paintButtonLightIgnoreRegion = new(left, top, right - left, bottom - top);
        // The delayed webcam may still show our light after it has switched off.
        _paintButtonLightIgnoreUntil = _paintClock().AddMilliseconds(1000);
    }
    private readonly List<PaintExpectedFrame> _paintExpectedFrames = [];
    private CanvasRenderTarget? _paintReferenceTarget;
    private Homography? _paintReferenceCameraMap, _paintReferenceSurfaceMap;
    private long _paintReferenceNavigation = -1, _paintReferenceRevision;
    private bool _paintReferenceActive;
    private string? _paintReferenceError;
    private DateTimeOffset _paintInputReadyAfter;
    private bool _paintInputAvailable;
    private BoardSurfaceState? _paintReferenceRenderedState;
    private CanvasRenderTarget? _paintReferenceSource;
    private byte[]? _paintReferencePixels;
    private long _paintReferenceReadbacks;

    private bool PaintInputReady => !_disposed && _boardSession.Screen == BoardScreen.Paint &&
        !_blackOutput && !_boardSetup && !IsBoardRevealActive && _calibrationTarget < 0 &&
        _boardMediaClip is not null && _boardCameraMap is not null && _boardSurfaceMap is not null;

    // Camera lifecycle owns availability. A laptop-only drawing or a paused
    // tracker can still display and save Paint without downloading reference
    // pixels that no camera consumer can use.
    internal void SetPaintInputAvailable(bool available)
    {
        lock (_gate)
        {
            if (_paintInputAvailable == available) return;
            _paintInputAvailable = available;
            SyncPaintReference();
        }
    }

    private void ClearPaintReferencePixels()
    {
        _paintReferencePixels = null;
        _paintReferenceSource = null;
        _paintReferenceRenderedState = null;
    }

    private void SyncPaintReference()
    {
        SyncPaintSession();
        bool active = PaintInputReady && _paintInputAvailable;
        if (_paintReferenceActive == active && _paintReferenceNavigation == _boardSession.Revision &&
            ReferenceEquals(_paintReferenceCameraMap, _boardCameraMap) &&
            ReferenceEquals(_paintReferenceSurfaceMap, _boardSurfaceMap)) return;
        _paintReferenceActive = active;
        _paintReferenceNavigation = _boardSession.Revision;
        _paintReferenceCameraMap = _boardCameraMap;
        _paintReferenceSurfaceMap = _boardSurfaceMap;
        _paintReferenceRevision++;
        _paintExpectedFrames.Clear();
        ClearPaintReferencePixels();
        _paintReferenceError = null;
        // When entering/clearing Paint, the webcam can still show the white
        // calibration screen or the previous painting. Build reference history
        // before accepting obstructions, including exposure/readout settling.
        _paintInputReadyAfter = _paintClock().AddMilliseconds(900);
    }

    // Bounded copies of frames submitted to the projector, not the current camera.
    // Comparison can select the older render that matches the webcam's latency.
    private void CapturePaintExpectedFrame(CanvasDevice device, DateTimeOffset now)
    {
        SyncPaintReference();
        if (!_paintReferenceActive || _boardApplicationTarget is null ||
            _paintExpectedFrames.LastOrDefault() is { } latest &&
            now - latest.PresentedAt < TimeSpan.FromMilliseconds(125)) return;
        try
        {
            if (_paintReferenceTarget is null || _paintReferenceTarget.Device != device)
            {
                _paintReferenceTarget?.Dispose();
                _paintReferenceTarget = new(device, 512, 512, 96);
                ClearPaintReferencePixels();
            }
            // Keep each presentation timestamp for webcam-latency matching,
            // but reuse immutable pixels while the actual rendered scene is
            // unchanged. The tracker only reads these arrays.
            if (_paintReferencePixels is null || _paintReferenceSource != _boardApplicationTarget ||
                _paintReferenceRenderedState != _renderedBoardState)
            {
                var pixels = _boardApplicationTarget.SizeInPixels;
                using (var drawing = _paintReferenceTarget.CreateDrawingSession())
                {
                    drawing.Clear(Colors.Black);
                    drawing.DrawImage(_boardApplicationTarget, new Rect(0, 0, 512, 512),
                        new Rect(0, 0, pixels.Width, pixels.Height), 1, CanvasImageInterpolation.HighQualityCubic);
                }
                _paintReferencePixels = _paintReferenceTarget.GetPixelBytes();
                _paintReferenceRenderedState = _renderedBoardState;
                _paintReferenceSource = _boardApplicationTarget;
                _paintReferenceReadbacks++;
            }
            _paintExpectedFrames.Add(new(512, 512, _paintReferencePixels, now));
            _paintExpectedFrames.RemoveAll(frame => now - frame.PresentedAt > TimeSpan.FromMilliseconds(950));
            if (_paintExpectedFrames.Count > 9) _paintExpectedFrames.RemoveAt(0);
            _paintReferenceError = null;
        }
        catch (Exception error) when (!device.IsDeviceLost(error.HResult))
        {
            _paintExpectedFrames.Clear();
            ClearPaintReferencePixels();
            _paintReferenceError = error.Message;
        }
    }

    public PaintDisturbanceScene? GetPaintDisturbanceContext()
    {
        lock (_gate)
        {
            SyncPaintReference();
            if (!_paintReferenceActive || _paintClock() < _paintInputReadyAfter) return null;
            var cameraToProjector = _boardCameraMap!.ToMatrix();
            var projectorToBoard = _boardSurfaceMap!.Inverse().ToMatrix();
            var matrix = new double[9];
            for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
            for (int k = 0; k < 3; k++)
                matrix[row * 3 + column] += projectorToBoard[row * 3 + k] * cameraToProjector[k * 3 + column];
            return new(_paintReferenceRevision, matrix, _paintExpectedFrames.ToArray(), PaintInputBounds, PaintIgnoredRegions());
        }
    }

    public int CompletePaintDisturbance(PaintDisturbanceScene requested, PaintDisturbanceResult result,
        DateTimeOffset frameTime)
    {
        lock (_gate)
        {
            SyncPaintReference();
            var now = _paintClock();
            if (!_paintReferenceActive || now < _paintInputReadyAfter || requested.Revision != _paintReferenceRevision ||
                frameTime < _paintInputReadyAfter || frameTime > now ||
                now - frameTime > TimeSpan.FromMilliseconds(350)) return 0;
            if (result.ReferenceReady && result.ConfirmedCandidateCount > 0 &&
                result.ForegroundBoardArea >= PaintDisturbanceTracker.MinimumBoardArea)
                NotePaintUserActivity();
            int accepted = 0;
            foreach (var drop in result.Drops.Take(2))
            {
                var point = drop.BoardCenter;
                if (drop.ObservedAt != frameTime || !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                    point.X < PaintInputBounds.X || point.Y < PaintInputBounds.Y ||
                    point.X > PaintInputBounds.X + PaintInputBounds.Width || point.Y > PaintInputBounds.Y + PaintInputBounds.Height ||
                    drop.ForegroundBoardArea < PaintDisturbanceTracker.MinimumBoardArea ||
                    PaintIgnoredRegions().Any(bounds => point.X >= bounds.X && point.X <= bounds.X + bounds.Width &&
                        point.Y >= bounds.Y && point.Y <= bounds.Y + bounds.Height)) continue;
                if (AddPaintDrop(new(point.X, point.Y), drop.RadiusUv, frameTime)) accepted++;
            }
            return accepted;
        }
    }

    public object GetPaintInputDiagnostics()
    {
        lock (_gate)
            return new { active = PaintInputReady && _paintInputAvailable, inputAvailable = _paintInputAvailable,
                revision = _paintReferenceRevision, referenceReadbacks = _paintReferenceReadbacks,
                expectedFrameCount = _paintExpectedFrames.Count,
                newestExpectedUtc = _paintExpectedFrames.LastOrDefault()?.PresentedAt,
                readyAfterUtc = _paintInputReadyAfter,
                minimumForegroundBoardArea = PaintDisturbanceTracker.MinimumBoardArea,
                spotlightsEnabled = false, buttonSpotlightsEnabled = true,
                buttonLightIgnoreRegion = _paintClock() <= _paintButtonLightIgnoreUntil
                    ? _paintButtonLightIgnoreRegion : null, error = _paintReferenceError };
    }

#if DEBUG
    internal (long Readbacks, long Revision, PaintExpectedFrame[] Frames, bool Active) GetPaintReferenceCacheForVerification()
    {
        lock (_gate)
        {
            SyncPaintReference();
            return (_paintReferenceReadbacks, _paintReferenceRevision, _paintExpectedFrames.ToArray(),
                _paintReferenceActive);
        }
    }
#endif

    private void DrawPaintNavigationCursor(CanvasDrawingSession drawing, Rect output)
    {
        if (_boardSurfaceMap is null) return;
        foreach (var button in _boardSession.Buttons)
        {
            var b = button.Bounds;
            var points = new[] { new Point2(b.X, b.Y), new Point2(b.X + b.Width, b.Y),
                new Point2(b.X + b.Width, b.Y + b.Height), new Point2(b.X, b.Y + b.Height) }
                .Select(point => _boardSurfaceMap.Transform(point))
                .Select(point => new Vector2((float)(output.X + point.X * output.Width),
                    (float)(output.Y + point.Y * output.Height))).ToArray();
            using var geometry = CanvasGeometry.CreatePolygon(drawing.Device, points);
            using var layer = drawing.CreateLayer(1, geometry);
            DrawHandCursor(drawing, output);
        }
    }

    private void DisposePaintReference()
    {
        _paintReferenceTarget?.Dispose();
        _paintReferenceTarget = null;
        _paintExpectedFrames.Clear();
        ClearPaintReferencePixels();
        _paintReferenceRevision++;
        _paintInputReadyAfter = _paintClock().AddMilliseconds(900);
    }
}
