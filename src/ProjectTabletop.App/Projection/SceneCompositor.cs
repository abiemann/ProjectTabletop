using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using ProjectTabletop.App.Media;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

/// <summary>
/// Draws the same scene into the projector and laptop preview. Coordinates are fractions
/// of the fullscreen projector canvas, so preview scaling and Windows DPI do not change
/// registration. The GPU owns decoded video surfaces and final composition.
/// </summary>
public sealed partial class SceneCompositor : IDisposable
{
    // Win2D drawing units in the projection canvas. Tune after measuring the actual
    // camera-to-projector edge error and projector DPI on the physical piece.
    private const float OverlayClipInset = 2.5f;
    private readonly object _gate = new();
    private readonly Dictionary<string, MediaAsset> _overlays = new(StringComparer.OrdinalIgnoreCase);
    private MediaAsset? _background;
    private IReadOnlyList<PieceDetection> _detections = Array.Empty<PieceDetection>();
    private DateTimeOffset _detectionTime;
    private Func<PixelPoint, Vector2>? _topPlaneMap;
    private bool _disposed;
    private double _displayAspect = 16.0 / 9;
    private int _calibrationTarget = -1;
    private bool _calibrationTargetTop;
    private bool _boardSetup;
    private bool _blackOutput;
    private BoardGrid? _boardGrid;
    private Point2[]? _detectedBoardCorners;
    private ProjectionClipRegion? _boardMediaClip;
    private Homography? _boardCameraMap;
    private Homography? _boardSurfaceMap;
    private ProjectedHandCursor[] _handTips = [];
    private DateTimeOffset _handFrameTime;
    private DateTimeOffset _handVisualResetThrough, _lastHandVisualFrameTime;
    private int _boardCalibrationSpot = -1;
    private DateTimeOffset _boardSetupStarted;
    private long _projectorFrames;
    private long _previewFrames;
    private long _projectorSlowFrames;
    private long _previewSlowFrames;
    private long _mediaRevision;

    private readonly record struct ProjectedHandCursor(Vector2 Position, DateTimeOffset ExecuteUntil,
        bool IsSpreadOut, bool FourFingersExtended, Vector2[] FingerPositions);

    public string BackgroundLabel { get; private set; } = "Test grid";

    public string? MediaError
    {
        get
        {
            lock (_gate)
                return _background?.Error ?? _overlays.Values.Select(item => item.Error).FirstOrDefault(error => error is not null);
        }
    }

    public (long ProjectorFrames, long PreviewFrames, long ProjectorSlowFrames, long PreviewSlowFrames) FrameCounts =>
        (Interlocked.Read(ref _projectorFrames), Interlocked.Read(ref _previewFrames),
         Interlocked.Read(ref _projectorSlowFrames), Interlocked.Read(ref _previewSlowFrames));

    public (long Revision, int VideoAssets, long FrameReadyEvents, long SurfaceCopies) PlaybackCounts
    {
        get
        {
            lock (_gate)
            {
                var videos = _overlays.Values.Append(_background).OfType<MediaAsset>()
                    .Where(asset => asset.IsVideo).Distinct().ToArray();
                long events = 0;
                long copies = 0;
                foreach (var video in videos)
                {
                    var counts = video.PlaybackCounts;
                    events += counts.FrameReadyEvents;
                    copies += counts.SurfaceCopies;
                }
                return (_mediaRevision, videos.Length, events, copies);
            }
        }
    }

    public IReadOnlyDictionary<string, string> OverlayPaths
    {
        get
        {
            lock (_gate) return _overlays.ToDictionary(pair => pair.Key, pair => pair.Value.Path);
        }
    }

    public bool HasBoardMediaClip
    {
        get { lock (_gate) return _boardMediaClip is not null; }
    }

    // Physical edges before the safety inset: measuring the media clip would
    // systematically underestimate the cardboard's dimensions.
    internal Point2[]? GetDetectedBoardCorners()
    {
        lock (_gate) return _boardMediaClip is not null ? _detectedBoardCorners?.ToArray() : null;
    }

    public void ClearBoardMediaClip()
    {
        lock (_gate)
        {
            CancelBoardReveal();
            CancelMonopolyEntrance();
            _boardMediaClip = null;
            SetEstimatedBoardSize(null);
            _detectedBoardCorners = null;
            _boardCameraMap = null;
            _boardSurfaceMap = null;
            ResetBoardRaster();
            _handTips = [];
            _handVisualResetThrough = DateTimeOffset.UtcNow;
            ClearHandSpotlights();
            _boardSession.ResetInput(DateTimeOffset.UtcNow);
            CancelBlackjackDeal();
            InvalidatePhotoCopyCapture();
        }
    }

    public void ClearHandTips(bool resetInput = true)
        => ClearHandTipsCore(resetInput, cancelMonopolyEntrance: true);

    private void ClearHandTipsCore(bool resetInput, bool cancelMonopolyEntrance)
    {
        lock (_gate)
        {
            _handTips = [];
            _handFrameTime = DateTimeOffset.MinValue;
            // A visual timeout must not reject a still-fresh inference that was
            // already in flight. Camera/calibration resets still block old input.
            if (resetInput)
            {
                if (cancelMonopolyEntrance) CancelMonopolyEntrance();
                _handVisualResetThrough = DateTimeOffset.UtcNow;
                ClearHandSpotlights();
                _boardSession.ResetInput(DateTimeOffset.UtcNow);
                CancelBlackjackDeal();
                CancelMonopolyDiceAnimation();
                if (!cancelMonopolyEntrance && _monopolyEntranceStartedAt is { } started && !_monopolyEntranceCompleted)
                    _boardSession.HoldMonopolyPresentationUntil(started.AddMilliseconds(MonopolyEntranceDurationMilliseconds));
            }
        }
    }

    public void SetHandCursors(IReadOnlyList<HandCursor> cursors, DateTimeOffset frameTime,
        bool photoCopyCaptureBusy = false, IReadOnlyList<HandCursor>? visualCursors = null)
    {
        lock (_gate)
        {
            if (BlockBoardRevealInput() || MonopolyEntranceActive) return;
            _handTips = [];
            var now = DateTimeOffset.UtcNow;
            var acceptVisual = frameTime <= now && now - frameTime <= TimeSpan.FromMilliseconds(350) &&
                frameTime > _handVisualResetThrough && frameTime > _lastHandVisualFrameTime;
            var boardSamples = new List<BoardHandSample>();
            var projectedTips = new List<ProjectedHandCursor>();
            foreach (var cursor in cursors)
            {
                var boardPoint = new Point2(double.NaN, double.NaN);
                var tip = cursor.Position;
                if (acceptVisual && _boardMediaClip is not null && _boardCameraMap is not null)
                {
                    // Share the camera preview's filtered positions, but keep
                    // raw cursors below for hit testing, gestures and matching.
                    var visual = cursor.TrackingId > 0 ? visualCursors?.FirstOrDefault(
                        item => item.TrackingId == cursor.TrackingId) ?? cursor : cursor;
                    var cameraMap = _boardCameraMap;
                    var point = ProjectFinger(visual.Position);
                    var fingers = cursor.HasFourExtendedFingers && visual.FingerTips.Count == 4
                        ? visual.FingerTips.Select(ProjectFinger).ToArray() : Array.Empty<Vector2>();
                    // Middle-tip aiming is independent of the old index cursor.
                    // Preserve finger order even when one point is outside the
                    // projective plane, so the middle marker never shifts fingers.
                    if (OnProjector(point) || fingers.Length == 4 && OnProjector(fingers[1]))
                    {
                        projectedTips.Add(new ProjectedHandCursor(point, cursor.ExecuteUntil, cursor.IsSpreadOut,
                            cursor.HasFourExtendedFingers, fingers));
                    }

                    Vector2 ProjectFinger(PixelPoint finger)
                    {
                        if (double.IsFinite(finger.X) && double.IsFinite(finger.Y))
                        {
                            try
                            {
                                var mapped = cameraMap.Transform(new Point2(finger.X, finger.Y));
                                return new Vector2((float)mapped.X, (float)mapped.Y);
                            }
                            catch (InvalidOperationException) { /* This individual finger is on the projective horizon. */ }
                        }
                        return new Vector2(float.NaN, float.NaN);
                    }
                    static bool OnProjector(Vector2 point) => point.X is >= 0 and <= 1 && point.Y is >= 0 and <= 1;
                }
                // Input uses current measured geometry, independently of visual
                // damping. Pinch selection retains the recent open pointing pose.
                var selection = cursor.SelectionPosition ?? tip;
                try
                {
                    if (!_boardSetup && _calibrationTarget < 0 && _boardMediaClip is not null &&
                        _boardCameraMap is not null && _boardSurfaceMap is not null &&
                        double.IsFinite(selection.X) && double.IsFinite(selection.Y))
                    {
                        var point = _boardCameraMap.Transform(new Point2(selection.X, selection.Y));
                        boardPoint = _boardSurfaceMap.InverseTransform(point);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Invalid or cancelled selection points still consume their
                    // event below, so a held pinch cannot click a later target.
                }
                // Even a pinch outside the board is consumed, so moving a held
                // red cursor onto a button cannot turn it into a delayed click.
                BoardAim? middleAim = null;
                if (!_blackOutput && !_boardSetup && _calibrationTarget < 0 && _boardMediaClip is not null &&
                    _boardCameraMap is not null && _boardSurfaceMap is not null &&
                    cursor.FingerTips.Count == 4 &&
                    double.IsFinite(cursor.FingerTips[1].X) && double.IsFinite(cursor.FingerTips[1].Y))
                {
                    try
                    {
                        var middle = cursor.FingerTips[1];
                        var projected = _boardCameraMap.Transform(new Point2(middle.X, middle.Y));
                        var aim = _boardSurfaceMap.InverseTransform(projected);
                        if (double.IsFinite(aim.X) && double.IsFinite(aim.Y)) middleAim = new(aim.X, aim.Y);
                    }
                    catch (InvalidOperationException) { /* Off-plane points have no usable board position. */ }
                }
                boardSamples.Add(new BoardHandSample(boardPoint.X, boardPoint.Y,
                    cursor.ExecuteUntil, cursor.ExecuteEventId, cursor.SelectionFrameTime)
                {
                    TrackingId = cursor.TrackingId,
                    FingerAim = middleAim,
                    FourFingersExtended = cursor.HasFourExtendedFingers,
                    FingersTogether = cursor.FingersTogether,
                    IndexFingerSeparated = cursor.IndexFingerSeparated
                });
            }
            _handTips = projectedTips.ToArray();
            _handFrameTime = acceptVisual ? frameTime : DateTimeOffset.MinValue;
            if (acceptVisual) _lastHandVisualFrameTime = frameTime;
            var shutterContext = PreparePhotoCopyGesture(cursors, frameTime, acceptVisual, photoCopyCaptureBusy);
            _boardSession.PaintSaveEnabled = CanSavePaint;
            var selectionResult = _boardSession.Update(boardSamples, frameTime, now);
            if (selectionResult is not null)
                _lastHandBoardSelection = new { frameTime, observedAt = now,
                    previous = selectionResult.Previous.ToString(), current = selectionResult.Current.ToString(),
                    selectionResult.ButtonId, selectionResult.TrackingId, gesture = selectionResult.Gesture.ToString() };
            if (selectionResult is { ButtonId: "paint-save" } && acceptVisual)
                QueuePaintSaveRequest(frameTime);
            if (selectionResult is { ButtonId: "photo-save" } && acceptVisual && !photoCopyCaptureBusy &&
                TryGetPhotoCopyMemoryImage(out var memoryImage))
                _photoCopyMemorySaveRequest = new(frameTime, memoryImage);
            else if (selectionResult is { ButtonId: "photo-shutter" } && shutterContext is not null)
                _photoCopyGestureShutter = new(selectionResult.TrackingId, frameTime, shutterContext);
            else if (selectionResult is not null && shutterContext is not null &&
                BoardSession.TryGetPhotoCopyAction(selectionResult.ButtonId, out var photoAction))
                _photoCopyGestureShutter = new(selectionResult.TrackingId, frameTime, shutterContext,
                    photoAction, selectionResult.Gesture);
            if (acceptVisual) ObserveHandLightingCommands(cursors, frameTime, now, selectionResult);
            SyncPhotoCopySession();
        }
    }

    public MediaAsset? FindAssetByPath(string path)
    {
        lock (_gate)
        {
            if (_background is not null && string.Equals(_background.Path, path, StringComparison.OrdinalIgnoreCase))
                return _background;
            return _overlays.Values.FirstOrDefault(asset =>
                string.Equals(asset.Path, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void SetDisplayAspect(double aspect)
    {
        lock (_gate)
        {
            CancelBoardReveal();
            CancelMonopolyEntrance();
            _displayAspect = double.IsFinite(aspect) && aspect > 0 ? aspect : 16.0 / 9;
            ClearHandSpotlights();
        }
    }

    public void SetTopPlaneMap(Func<PixelPoint, Vector2>? map)
    {
        lock (_gate) _topPlaneMap = map;
    }

    /// <summary>
    /// Illuminates the complete projector field while the camera finds the physical
    /// cardboard. No grid or corner marker is projected during this scan.
    /// </summary>
    public void SetBoardSetup(bool enabled)
    {
        lock (_gate)
        {
            CancelBoardReveal();
            if (enabled) CancelMonopolyDiceAnimation();
            if (enabled && !_boardSetup) _boardSetupStarted = DateTimeOffset.UtcNow;
            if (enabled) ClearBoardMediaClip();
            _boardSetup = enabled;
            _blackOutput = false;
            _boardGrid = null;
            _boardCalibrationSpot = -1;
        }
    }

    /// <summary>Full-black projector output for an ambient camera reference frame.</summary>
    public void SetBlackOutput(bool enabled)
    {
        lock (_gate)
        {
            if (enabled)
            {
                CancelBoardReveal();
                CancelMonopolyDiceAnimation();
                CancelMonopolyEntrance();
            }
            _blackOutput = enabled;
            if (enabled) ClearHandSpotlights();
        }
    }

    public const int BoardCalibrationSpotCount = BoardRegistration.SpotCount;

    public static Vector2 BoardCalibrationSpotPosition(int index)
    {
        var point = BoardRegistration.SpotPosition(index);
        return new((float)point.X, (float)point.Y);
    }

    /// <summary>Dark disk on the white field for camera/projector registration.</summary>
    public void ShowBoardCalibrationSpot(int index)
    {
        if (index < -1 || index >= BoardCalibrationSpotCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        lock (_gate)
        {
            if (!_boardSetup) return;
            _boardCalibrationSpot = index;
        }
    }

    /// <summary>
    /// Replaces the white scan with a grid inside the four physical board corners.
    /// Corners are normalized projector coordinates in perimeter order.
    /// </summary>
    public float SetDetectedBoardGrid(IReadOnlyList<Vector2> projectorCorners, Homography cameraMap)
    {
        ArgumentNullException.ThrowIfNull(projectorCorners);
        ArgumentNullException.ThrowIfNull(cameraMap);
        lock (_gate)
        {
            if (!_boardSetup) throw new InvalidOperationException("Board setup is not active.");
            var points = projectorCorners.Select(point => new Point2(point.X, point.Y)).ToArray();
            // The ordered calibration dots establish projector coordinates.
            // Camera corner order must not choose the interface's first facing.
            var heading = _boardFacingDegrees ?? 0;
            var ordered = BoardOrientation.Orient(points, heading, _displayAspect);
            var grid = BoardGrid.Create(ordered.Select(point => new Vector2((float)point.X, (float)point.Y)).ToArray());
            ApplyDetectedBoardGrid(grid, cameraMap);
            // Save the initial projector-facing default only after validation.
            _boardFacingDegrees ??= heading;
            return grid.InsetFraction;
        }
    }

    private void ApplyDetectedBoardGrid(BoardGrid grid, Homography cameraMap)
    {
        var mediaClip = ProjectionClipRegion.FromCorners(grid.GridCorners
            .Select(point => new Point2(point.X, point.Y)).ToArray());
        var surfaceMap = Homography.FromFourPoints(
            [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], mediaClip.Corners);
        ResetBoardRaster();
        _boardGrid = grid;
        _detectedBoardCorners = grid.BoardCorners.Select(point => new Point2(point.X, point.Y)).ToArray();
        _boardMediaClip = mediaClip;
        _boardCameraMap = cameraMap;
        _boardSurfaceMap = surfaceMap;
        _handTips = [];
        _handVisualResetThrough = DateTimeOffset.UtcNow;
        ClearHandSpotlights();
        _boardSession.ResetInput(DateTimeOffset.UtcNow);
        CancelBlackjackDeal();
        InvalidatePhotoCopyCapture();
        _boardSetupStarted = DateTimeOffset.UtcNow;
    }

    public void ShowCalibrationTarget(int index, bool pieceTop)
    {
        lock (_gate)
        {
            CancelBoardReveal();
            _calibrationTarget = index is >= 0 and < 4 ? index : -1;
            _calibrationTargetTop = pieceTop;
            if (_calibrationTarget >= 0)
            {
                CancelMonopolyEntrance();
                ClearHandSpotlights();
            }
        }
    }

    public Vector2[] GetCalibrationTargets(int displayWidth, int displayHeight, bool pieceTop)
    {
        lock (_gate)
        {
            var clip = _boardMediaClip ??
                throw new InvalidOperationException("Scan the cardboard before calibrating projection targets.");
            var output = new Rect(0, 0, displayWidth, displayHeight);
            var margin = pieceTop ? 0.2f : 0.1f;
            return
            [
                BoardPoint(clip, output, margin, margin),
                BoardPoint(clip, output, 1 - margin, margin),
                BoardPoint(clip, output, 1 - margin, 1 - margin),
                BoardPoint(clip, output, margin, 1 - margin)
            ];
        }
    }

    public void SetDetections(IReadOnlyList<PieceDetection> detections, DateTimeOffset frameTime)
    {
        lock (_gate)
        {
            _detections = detections;
            _detectionTime = frameTime;
        }
    }

    public void SetBackground(MediaAsset? asset)
    {
        lock (_gate)
        {
            CancelBoardReveal();
            _blackOutput = false;
            _boardSession.ShowMedia();
            SyncPhotoCopySession();
            var old = _background;
            _background = asset;
            BackgroundLabel = asset is null ? "Test grid" : Path.GetFileName(asset.Path);
            _mediaRevision++;
            DisposeIfUnreferenced(old);
        }
    }

    public void SetOverlay(string pieceId, MediaAsset? asset)
    {
        lock (_gate)
        {
            _overlays.Remove(pieceId, out var old);
            if (asset is not null) _overlays[pieceId] = asset;
            _mediaRevision++;
            DisposeIfUnreferenced(old);
        }
    }

    private void DisposeIfUnreferenced(MediaAsset? asset)
    {
        if (asset is null || ReferenceEquals(_background, asset) ||
            _overlays.Values.Any(value => ReferenceEquals(value, asset))) return;
        asset.Dispose();
    }

    public void Draw(CanvasDrawingSession ds, float canvasWidth, float canvasHeight,
                     bool preview, bool runningSlowly)
    {
        ds.Clear(Colors.Black);
        if (canvasWidth <= 0 || canvasHeight <= 0) return;
        if (preview)
        {
            Interlocked.Increment(ref _previewFrames);
            if (runningSlowly) Interlocked.Increment(ref _previewSlowFrames);
        }
        else
        {
            Interlocked.Increment(ref _projectorFrames);
            if (runningSlowly) Interlocked.Increment(ref _projectorSlowFrames);
        }

        lock (_gate)
        {
            if (_disposed) return;
            if (_blackOutput) return;
            var output = FitDisplay(canvasWidth, canvasHeight, preview);

            if (_boardSetup)
            {
                if (_boardGrid is { } grid)
                {
                    // Preview pixels outside the fitted projector rectangle are
                    // unreachable on the real display. Clip them here too so a
                    // corner beyond the light field is not shown as projected.
                    if (preview)
                    {
                        using (ds.CreateLayer(1, output))
                            DrawDetectedBoardGrid(ds, output, grid,
                                DateTimeOffset.UtcNow - _boardSetupStarted);
                    }
                    else
                        DrawDetectedBoardGrid(ds, output, grid,
                            DateTimeOffset.UtcNow - _boardSetupStarted);
                    DrawHandCursor(ds, output);
                }
                else
                {
                    ds.FillRectangle(output, Colors.White);
                    if (_boardCalibrationSpot >= 0)
                    {
                        var point = BoardCalibrationSpotPosition(_boardCalibrationSpot);
                        var center = new Vector2((float)(output.X + point.X * output.Width),
                                                 (float)(output.Y + point.Y * output.Height));
                        ds.FillCircle(center, (float)Math.Min(output.Width, output.Height) * 0.035f,
                            Colors.Black);
                    }
                }
                return;
            }

            if (TryDrawBoardReveal(ds, output, preview)) return;
            DrawBoardScene(ds, output, preview);
        }
    }

    private void DrawBoardScene(CanvasDrawingSession ds, Rect output, bool preview)
    {
        // Media is fail-closed: a successful board scan supplies the only
        // projector-space region that can receive images or video. The
        // inset polygon leaves a small guard band for optical edge blur.
        if (_boardMediaClip is not { } mediaClip) return;
        var clipPoints = mediaClip.Corners.Select(point => new Vector2(
            (float)(output.X + point.X * output.Width),
            (float)(output.Y + point.Y * output.Height))).ToArray();
        var mediaRect = BoardBounds(mediaClip, output);
        using var clipGeometry = CanvasGeometry.CreatePolygon(ds.Device, clipPoints);
        using var boardLayer = ds.CreateLayer(1, clipGeometry);

        if (_boardSession.Screen != BoardScreen.Media && _boardSurfaceMap is not null)
        {
            if (_boardSession.Screen == BoardScreen.HandTracking)
                DrawTestGrid(ds, mediaRect);
            DrawBoardApplication(ds, output, preview);
        }
        else
        {
            var image = _background?.GetFrame(ds.Device);
            if (image is null) DrawTestGrid(ds, mediaRect);
            else
            {
                var size = image.SizeInPixels;
                var targetAspect = mediaRect.Width / mediaRect.Height;
                var sourceAspect = size.Width / size.Height;
                var cropWidth = sourceAspect > targetAspect ? size.Height * targetAspect : size.Width;
                var cropHeight = sourceAspect > targetAspect ? size.Height : size.Width / targetAspect;
                var source = new Rect((size.Width - cropWidth) / 2,
                    (size.Height - cropHeight) / 2, cropWidth, cropHeight);
                ds.DrawImage(image, mediaRect, source, 1);
            }
        }

        if (_calibrationTarget >= 0)
        {
            var margin = _calibrationTargetTop ? 0.2f : 0.1f;
            var uv = _calibrationTarget switch
            {
                0 => new Vector2(margin, margin),
                1 => new Vector2(1 - margin, margin),
                2 => new Vector2(1 - margin, 1 - margin),
                _ => new Vector2(margin, 1 - margin)
            };
            DrawCalibrationTarget(ds,
                BoardPoint(mediaClip, output, uv.X, uv.Y),
                _calibrationTarget + 1, _calibrationTargetTop);
        }

        if (_boardSession.Screen == BoardScreen.Media && _topPlaneMap is not null &&
            DateTimeOffset.UtcNow - _detectionTime <= TimeSpan.FromMilliseconds(350))
        {
            foreach (var detection in _detections)
            {
                if (!_overlays.TryGetValue(detection.PieceId, out var media)) continue;
                var frame = media.GetFrame(ds.Device);
                if (frame is null || detection.Outline.Count < 3) continue;
                DrawOverlay(ds, output, mediaRect, detection, frame, _topPlaneMap);
            }
        }
        DrawHandAcquisitionLight(ds, output);
        DrawHandSpotlights(ds, output);
        if (_boardSession.Screen == BoardScreen.Paint) DrawPaintNavigationCursor(ds, output);
        else DrawHandCursor(ds, output);
    }

    private void DrawHandCursor(CanvasDrawingSession ds, Rect output)
    {
        // Four-finger aiming is visible on each board. Red pinch feedback stays
        // confined to the gesture tester.
        if (_boardSetup || _calibrationTarget >= 0 || MonopolyEntranceActive) return;
        var now = DateTimeOffset.UtcNow;
        if (_boardMediaClip is not { } clip || _boardCameraMap is null ||
            _handTips.Length == 0 || _handFrameTime > now ||
            now - _handFrameTime > TimeSpan.FromMilliseconds(350)) return;
        var clipPoints = clip.Corners.Select(point => new Vector2(
            (float)(output.X + point.X * output.Width),
            (float)(output.Y + point.Y * output.Height))).ToArray();
        using var geometry = CanvasGeometry.CreatePolygon(ds.Device, clipPoints);
        using var layer = ds.CreateLayer(1, geometry);
        var radius = Math.Max(6, (float)Math.Min(output.Width, output.Height) * 0.018f);
        foreach (var cursor in _handTips)
        {
            if (cursor.FourFingersExtended && cursor.FingerPositions.Length == 4)
            {
                for (int index = 0; index < cursor.FingerPositions.Length; index++)
                {
                    var finger = cursor.FingerPositions[index];
                    if (!float.IsFinite(finger.X) || !float.IsFinite(finger.Y)) continue;
                    var point = new Vector2((float)(output.X + finger.X * output.Width),
                        (float)(output.Y + finger.Y * output.Height));
                    float fingerRadius = index == 1 ? radius * .55f : radius * .22f;
                    ds.DrawCircle(point, fingerRadius, Colors.Black, 4);
                    ds.DrawCircle(point, fingerRadius, index == 1 ? Color.FromArgb(255, 244, 207, 111) : Colors.White, 2);
                }
            }
            if (_boardSession.Screen != BoardScreen.HandTracking || now >= cursor.ExecuteUntil) continue;
            var tip = cursor.Position;
            if (!float.IsFinite(tip.X) || !float.IsFinite(tip.Y)) continue;
            var center = new Vector2((float)(output.X + tip.X * output.Width),
                                    (float)(output.Y + tip.Y * output.Height));
            ds.DrawCircle(center, radius, Colors.Black, Math.Max(4, radius * 0.35f));
            ds.DrawCircle(center, radius, Colors.Red,
                Math.Max(2, radius * 0.17f));
        }
    }

    private Rect FitDisplay(float width, float height, bool preview)
    {
        if (!preview) return new Rect(0, 0, width, height);
        var targetAspect = _displayAspect;
        var availableAspect = width / height;
        return availableAspect > targetAspect
            ? new Rect((width - height * targetAspect) / 2, 0, height * targetAspect, height)
            : new Rect(0, (height - width / targetAspect) / 2, width, width / targetAspect);
    }

    private static Rect BoardBounds(ProjectionClipRegion clip, Rect output)
    {
        return new Rect(output.X + clip.MinX * output.Width,
            output.Y + clip.MinY * output.Height,
            (clip.MaxX - clip.MinX) * output.Width,
            (clip.MaxY - clip.MinY) * output.Height);
    }

    private static Vector2 BoardPoint(ProjectionClipRegion clip, Rect output, float u, float v)
    {
        var corners = clip.Corners;
        var top = Vector2.Lerp(new Vector2((float)corners[0].X, (float)corners[0].Y),
            new Vector2((float)corners[1].X, (float)corners[1].Y), u);
        var bottom = Vector2.Lerp(new Vector2((float)corners[3].X, (float)corners[3].Y),
            new Vector2((float)corners[2].X, (float)corners[2].Y), u);
        var point = Vector2.Lerp(top, bottom, v);
        return new Vector2((float)(output.X + point.X * output.Width),
            (float)(output.Y + point.Y * output.Height));
    }

    private static void DrawTestGrid(CanvasDrawingSession ds, Rect stage)
    {
        ds.FillRectangle(stage, AppPalette.Background);
        for (var i = 0; i <= 10; i++)
        {
            var x = (float)(stage.X + stage.Width * i / 10);
            var y = (float)(stage.Y + stage.Height * i / 10);
            ds.DrawLine(x, (float)stage.Y, x, (float)(stage.Y + stage.Height), AppPalette.GridLine, i is 0 or 10 or 5 ? 2 : 1);
            ds.DrawLine((float)stage.X, y, (float)(stage.X + stage.Width), y, AppPalette.GridLine, i is 0 or 10 or 5 ? 2 : 1);
        }
    }

    private static void DrawDetectedBoardGrid(CanvasDrawingSession ds, Rect output,
                                              BoardGrid grid, TimeSpan elapsed)
    {
        Vector2 View(Vector2 point) => new((float)(output.X + point.X * output.Width),
                                            (float)(output.Y + point.Y * output.Height));
        var gridCorners = grid.GridCorners.Select(View).ToArray();
        using (var polygon = CanvasGeometry.CreatePolygon(ds.Device, gridCorners))
            ds.FillGeometry(polygon, Colors.White);

        var shortestEdge = Enumerable.Range(0, 4)
            .Min(index => Vector2.Distance(gridCorners[index], gridCorners[(index + 1) % 4]));
        var gridThickness = Math.Clamp(shortestEdge * 0.0016f, 1.25f, 3.5f);
        var gridColor = Color.FromArgb(225, 79, 89, 102);
        foreach (var (first, last) in grid.Lines)
            ds.DrawLine(View(first), View(last), gridColor, gridThickness);

        var arm = Math.Clamp(shortestEdge * 0.07f, 17f, 95f);
        var thickness = Math.Clamp(shortestEdge * 0.006f, 3f, 9f);
        var seconds = elapsed.TotalSeconds;
        var pulse = 0.85 + 0.15 * Math.Sin(seconds * 3.2);
        var orange = Color.FromArgb((byte)(255 * pulse), 255, 111, 24);
        // The grid is inset to keep its light on the cardboard. The orange
        // brackets instead mark the detected physical edges, including when
        // the board nearly reaches the projector's clipping boundary.
        var boardCorners = grid.BoardCorners.Select(View).ToArray();
        for (var i = 0; i < boardCorners.Length; i++)
        {
            var t = Math.Clamp((seconds - i * 0.11) / 0.65, 0, 1);
            var ease = t * t * (3 - 2 * t);
            var corner = boardCorners[i];
            var towardNext = Vector2.Normalize(boardCorners[(i + 1) % 4] - corner);
            var towardPrevious = Vector2.Normalize(boardCorners[(i + 3) % 4] - corner);
            var length = arm * (float)ease;
            ds.DrawLine(corner, corner + towardNext * length, orange, thickness);
            ds.DrawLine(corner, corner + towardPrevious * length, orange, thickness);
        }
    }

    private sealed record BoardGrid(Vector2[] BoardCorners, Vector2[] GridCorners,
                                    (Vector2 First, Vector2 Last)[] Lines,
                                    float InsetFraction)
    {
        public static BoardGrid Create(IReadOnlyList<Vector2> corners)
        {
            if (corners.Count != 4 || corners.Any(point =>
                    !float.IsFinite(point.X) || !float.IsFinite(point.Y) ||
                    point.X < -0.06f || point.X > 1.06f || point.Y < -0.06f || point.Y > 1.06f))
                throw new ArgumentException("The mapped board lies outside the projector field.", nameof(corners));

            // The physical sheet nearly touches the projector's top and bottom edges.
            // Pull the grid a little inside the detected material so edge uncertainty
            // and the projector's optical blur cannot spill onto the surrounding floor.
            var middle = corners.Aggregate(Vector2.Zero, (sum, point) => sum + point) / 4;
            if (middle.X <= 0 || middle.X >= 1 || middle.Y <= 0 || middle.Y >= 1)
                throw new ArgumentException("The board center is outside the projector field.", nameof(corners));
            var inset = 0.01f;
            foreach (var point in corners)
            {
                if (point.X < 0.005f) inset = Math.Max(inset, (0.005f - point.X) / (middle.X - point.X));
                if (point.X > 0.995f) inset = Math.Max(inset, (point.X - 0.995f) / (point.X - middle.X));
                if (point.Y < 0.005f) inset = Math.Max(inset, (0.005f - point.Y) / (middle.Y - point.Y));
                if (point.Y > 0.995f) inset = Math.Max(inset, (point.Y - 0.995f) / (point.Y - middle.Y));
            }
            if (inset > 0.06f)
                throw new ArgumentException("Too much of the board is outside the projector field.", nameof(corners));
            var safeCorners = corners.Select(point => Vector2.Lerp(point, middle, inset)).ToArray();
            if (safeCorners.Any(point => point.X < 0 || point.X > 1 || point.Y < 0 || point.Y > 1))
                throw new ArgumentException("The grid cannot fit inside the projector field.", nameof(corners));

            Point2[] logical = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            var projected = safeCorners.Select(point => new Point2(point.X, point.Y)).ToArray();
            var map = Homography.FromFourPoints(logical, projected);
            Vector2 At(double u, double v)
            {
                var point = map.Transform(new Point2(u, v));
                return new Vector2((float)point.X, (float)point.Y);
            }

            var lines = new (Vector2 First, Vector2 Last)[22];
            for (var index = 0; index <= 10; index++)
            {
                var fraction = index / 10.0;
                lines[index] = (At(fraction, 0), At(fraction, 1));
                lines[11 + index] = (At(0, fraction), At(1, fraction));
            }
            return new BoardGrid(corners.ToArray(), safeCorners, lines, inset);
        }
    }

    private static void DrawCalibrationTarget(CanvasDrawingSession ds, Vector2 center, int number, bool pieceTop)
    {
        const float radius = 18;
        ds.FillCircle(center, radius + 4, Colors.Black);
        ds.DrawCircle(center, radius, Colors.Yellow, 4);
        ds.DrawLine(center.X - radius - 10, center.Y, center.X + radius + 10, center.Y, Colors.Yellow, 3);
        ds.DrawLine(center.X, center.Y - radius - 10, center.X, center.Y + radius + 10, Colors.Yellow, 3);
        ds.DrawText($"{(pieceTop ? "TOP" : "BOARD")} {number}", center.X + 28, center.Y + 16, Colors.Yellow);
    }

    private static void DrawOverlay(CanvasDrawingSession ds, Rect output, Rect stage, PieceDetection detection,
                                    CanvasBitmap image, Func<PixelPoint, Vector2> map)
    {
        var points = new Vector2[detection.Outline.Count];
        for (var i = 0; i < points.Length; i++)
        {
            var uv = map(detection.Outline[i]);
            points[i] = new Vector2((float)(output.X + uv.X * output.Width), (float)(output.Y + uv.Y * output.Height));
        }
        if (points.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y))) return;

        var centerUv = map(detection.Center);
        var center = new Vector2((float)(output.X + centerUv.X * output.Width),
                                 (float)(output.Y + centerUv.Y * output.Height));
        if (!float.IsFinite(center.X) || !float.IsFinite(center.Y)) return;
        var unit = new PixelPoint(detection.Center.X + Math.Cos(detection.AngleDegrees * Math.PI / 180) * 20,
                                  detection.Center.Y + Math.Sin(detection.AngleDegrees * Math.PI / 180) * 20);
        var mappedCenter = map(detection.Center);
        var mappedFront = map(unit);
        var angle = (float)Math.Atan2((mappedFront.Y - mappedCenter.Y) * output.Height,
                                     (mappedFront.X - mappedCenter.X) * output.Width);
        if (!float.IsFinite(angle)) return;
        var cosine = MathF.Cos(angle);
        var sine = MathF.Sin(angle);
        var local = points.Select(p => new Vector2((p.X - center.X) * cosine + (p.Y - center.Y) * sine,
                                                   -(p.X - center.X) * sine + (p.Y - center.Y) * cosine)).ToArray();
        var minX = local.Min(p => p.X);
        var maxX = local.Max(p => p.X);
        var minY = local.Min(p => p.Y);
        var maxY = local.Max(p => p.Y);
        if (maxX - minX < 2 || maxY - minY < 2) return;

        using var clip = CanvasGeometry.CreatePolygon(ds.Device, points);
        // Removing a stroke from the polygon's own boundary erodes its filled area
        // by half the stroke width. Unlike scaling vertices toward the centroid,
        // this follows concave edges and cannot push an inset vertex outside them.
        using var strokeStyle = new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round };
        using var boundary = clip.Stroke(2 * OverlayClipInset, strokeStyle);
        using var insetClip = clip.CombineWith(boundary, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        if (insetClip.ComputeArea() < 1) return;
        using (ds.CreateLayer(1, stage))
        using (ds.CreateLayer(1, insetClip))
        {
            var prior = ds.Transform;
            ds.Transform = Matrix3x2.CreateRotation(angle, center);
            var destination = new Rect(center.X + minX, center.Y + minY, maxX - minX, maxY - minY);
            var imageSize = image.SizeInPixels;
            var targetAspect = destination.Width / destination.Height;
            var imageAspect = imageSize.Width / imageSize.Height;
            var cropWidth = imageAspect > targetAspect ? imageSize.Height * targetAspect : imageSize.Width;
            var cropHeight = imageAspect > targetAspect ? imageSize.Height : imageSize.Width / targetAspect;
            var source = new Rect((imageSize.Width - cropWidth) / 2,
                                  (imageSize.Height - cropHeight) / 2, cropWidth, cropHeight);
            ds.DrawImage(image, destination, source, 1);
            ds.Transform = prior;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            CancelMonopolyEntrance();
            _disposed = true;
            foreach (var asset in _overlays.Values.Append(_background).OfType<MediaAsset>().Distinct()) asset.Dispose();
            _overlays.Clear();
            _boardApplicationTarget?.Dispose();
            _boardApplicationTarget = null;
            _acquisitionReferenceTarget?.Dispose();
            _acquisitionReferenceTarget = null;
            _blackjackPreviewTarget?.Dispose();
            _blackjackPreviewTarget = null;
            _monopolyPreviewTarget?.Dispose();
            _monopolyPreviewTarget = null;
            DisposeMonopolyDiceLayer();
            DisposeMonopolyEntranceLayers();
            _globePreviewTarget?.Dispose();
            _globePreviewTarget = null;
            DisposeGlobeRenderer();
            _blackjackFlightTarget?.Dispose();
            _blackjackFlightTarget = null;
            _blackjackFlights.Clear();
            _blackjackDeal = null;
            _boardSession.BlackjackHitOccurred -= OnBlackjackHit;
            _boardSession.BlackjackDealOccurred -= OnBlackjackDeal;
            _boardSession.MonopolyRollOccurred -= OnMonopolyRoll;
            _boardSession.BoardOpened -= OnBoardOpened;
            _photoCopyBitmap?.Dispose();
            _photoCopyBitmap = null;
            _photoCopyCameraTarget?.Dispose();
            _photoCopyCameraTarget = null;
            _photoCopyCutout = null;
            _photoCopyPremultipliedPixels = null;
            DisposePaintResources();
            DisposePaintReference();
        }
    }
}
