using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using ProjectTabletop.App.Media;
using ProjectTabletop.Calibration;
using ProjectTabletop.Vision;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

/// <summary>
/// Draws the same scene into the projector and laptop preview. Coordinates are fractions
/// of the fullscreen projector canvas, so preview scaling and Windows DPI do not change
/// registration. The GPU owns decoded video surfaces and final composition.
/// </summary>
public sealed class SceneCompositor : IDisposable
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
    private double _stageSize = 0.91;
    private double _stageOffsetX;
    private double _stageOffsetY;
    private int _calibrationTarget = -1;
    private bool _calibrationTargetTop;
    private bool _boardSetup;
    private bool _blackOutput;
    private BoardGrid? _boardGrid;
    private int _boardCalibrationSpot = -1;
    private Vector2? _customBoardCalibrationSpot;
    private DateTimeOffset _boardSetupStarted;
    private long _projectorFrames;
    private long _previewFrames;
    private long _projectorSlowFrames;
    private long _previewSlowFrames;
    private long _mediaRevision;

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
        lock (_gate) _displayAspect = double.IsFinite(aspect) && aspect > 0 ? aspect : 16.0 / 9;
    }

    public void SetStage(double heightFraction, double horizontalOffset, double verticalOffset)
    {
        lock (_gate)
        {
            _stageSize = Math.Clamp(heightFraction, 0.35, 0.95);
            _stageOffsetX = Math.Clamp(horizontalOffset, -0.25, 0.25);
            _stageOffsetY = Math.Clamp(verticalOffset, -0.25, 0.25);
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
            if (enabled && !_boardSetup) _boardSetupStarted = DateTimeOffset.UtcNow;
            _boardSetup = enabled;
            _blackOutput = false;
            _boardGrid = null;
            _boardCalibrationSpot = -1;
            _customBoardCalibrationSpot = null;
        }
    }

    /// <summary>Full-black projector output for an ambient camera reference frame.</summary>
    public void SetBlackOutput(bool enabled)
    {
        lock (_gate) _blackOutput = enabled;
    }

    public const int BoardCalibrationSpotCount = 5;

    public static Vector2 BoardCalibrationSpotPosition(int index) => index switch
    {
        0 => new(0.32f, 0.32f),
        1 => new(0.68f, 0.32f),
        2 => new(0.68f, 0.68f),
        3 => new(0.32f, 0.68f),
        4 => new(0.50f, 0.50f),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    /// <summary>Dark disk on the white field for camera/projector registration.</summary>
    public void ShowBoardCalibrationSpot(int index)
    {
        if (index < -1 || index >= BoardCalibrationSpotCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        lock (_gate)
        {
            if (!_boardSetup) return;
            _boardCalibrationSpot = index;
            _customBoardCalibrationSpot = null;
        }
    }

    /// <summary>Dark registration disk at a normalized projector-canvas position.</summary>
    public void ShowBoardCalibrationSpotAt(Vector2 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
            position.X < 0.05f || position.X > 0.95f ||
            position.Y < 0.05f || position.Y > 0.95f)
            throw new ArgumentOutOfRangeException(nameof(position));
        lock (_gate)
        {
            if (!_boardSetup) return;
            _boardCalibrationSpot = -1;
            _customBoardCalibrationSpot = position;
        }
    }

    /// <summary>
    /// Replaces the white scan with a grid inside the four physical board corners.
    /// Corners are normalized projector coordinates in perimeter order.
    /// </summary>
    public float SetDetectedBoardGrid(IReadOnlyList<Vector2> projectorCorners)
    {
        ArgumentNullException.ThrowIfNull(projectorCorners);
        var grid = BoardGrid.Create(projectorCorners);
        lock (_gate)
        {
            if (!_boardSetup) throw new InvalidOperationException("Board setup is not active.");
            _boardGrid = grid;
            _boardSetupStarted = DateTimeOffset.UtcNow;
        }
        return grid.InsetFraction;
    }

    public void ShowCalibrationTarget(int index, bool pieceTop)
    {
        lock (_gate)
        {
            _calibrationTarget = index is >= 0 and < 4 ? index : -1;
            _calibrationTargetTop = pieceTop;
        }
    }

    public Vector2[] GetCalibrationTargets(int displayWidth, int displayHeight, bool pieceTop)
    {
        lock (_gate)
        {
            var stage = StageRect(new Rect(0, 0, displayWidth, displayHeight));
            var margin = pieceTop ? 0.2 : 0.0;
            return
            [
                new((float)(stage.X + stage.Width * margin), (float)(stage.Y + stage.Height * margin)),
                new((float)(stage.X + stage.Width * (1 - margin)), (float)(stage.Y + stage.Height * margin)),
                new((float)(stage.X + stage.Width * (1 - margin)), (float)(stage.Y + stage.Height * (1 - margin))),
                new((float)(stage.X + stage.Width * margin), (float)(stage.Y + stage.Height * (1 - margin)))
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
            _blackOutput = false;
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
            var stage = StageRect(output);

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
                }
                else
                {
                    ds.FillRectangle(output, Colors.White);
                    if (_boardCalibrationSpot >= 0 || _customBoardCalibrationSpot is not null)
                    {
                        var point = _customBoardCalibrationSpot ??
                            BoardCalibrationSpotPosition(_boardCalibrationSpot);
                        var center = new Vector2((float)(output.X + point.X * output.Width),
                                                 (float)(output.Y + point.Y * output.Height));
                        ds.FillCircle(center, (float)Math.Min(output.Width, output.Height) * 0.035f,
                            Colors.Black);
                    }
                }
                return;
            }

            var image = _background?.GetFrame(ds.Device);
            if (image is null) DrawTestGrid(ds, stage);
            else
            {
                var size = image.SizeInPixels;
                var crop = Math.Min(size.Width, size.Height);
                var source = new Rect((size.Width - crop) / 2, (size.Height - crop) / 2, crop, crop);
                ds.DrawImage(image, stage, source, 1);
            }

            if (_calibrationTarget >= 0)
            {
                var margin = _calibrationTargetTop ? 0.2 : 0.0;
                var uv = _calibrationTarget switch
                {
                    0 => new Vector2((float)margin, (float)margin),
                    1 => new Vector2((float)(1 - margin), (float)margin),
                    2 => new Vector2((float)(1 - margin), (float)(1 - margin)),
                    _ => new Vector2((float)margin, (float)(1 - margin))
                };
                DrawCalibrationTarget(ds,
                    new Vector2((float)(stage.X + uv.X * stage.Width), (float)(stage.Y + uv.Y * stage.Height)),
                    _calibrationTarget + 1, _calibrationTargetTop);
            }

            if (_topPlaneMap is null || DateTimeOffset.UtcNow - _detectionTime > TimeSpan.FromMilliseconds(350)) return;
            foreach (var detection in _detections)
            {
                if (!_overlays.TryGetValue(detection.PieceId, out var media)) continue;
                var frame = media.GetFrame(ds.Device);
                if (frame is null || detection.Outline.Count < 3) continue;
                DrawOverlay(ds, output, stage, detection, frame, _topPlaneMap);
            }
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

    private Rect StageRect(Rect output)
    {
        var side = Math.Min(output.Height * _stageSize, output.Width * 0.95);
        var xMargin = (output.Width - side) / 2;
        var yMargin = (output.Height - side) / 2;
        var dx = Math.Clamp(_stageOffsetX * output.Width, -xMargin, xMargin);
        var dy = Math.Clamp(_stageOffsetY * output.Height, -yMargin, yMargin);
        return new Rect(output.X + xMargin + dx,
                        output.Y + yMargin + dy,
                        side, side);
    }

    private static void DrawTestGrid(CanvasDrawingSession ds, Rect stage)
    {
        ds.FillRectangle(stage, Colors.White);
        for (var i = 0; i <= 10; i++)
        {
            var x = (float)(stage.X + stage.Width * i / 10);
            var y = (float)(stage.Y + stage.Height * i / 10);
            ds.DrawLine(x, (float)stage.Y, x, (float)(stage.Y + stage.Height), Colors.Gray, i is 0 or 10 or 5 ? 2 : 1);
            ds.DrawLine((float)stage.X, y, (float)(stage.X + stage.Width), y, Colors.Gray, i is 0 or 10 or 5 ? 2 : 1);
        }
        ds.DrawText("21 in × 21 in", (float)stage.X + 16, (float)stage.Y + 12, Colors.Black);
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
            _disposed = true;
            foreach (var asset in _overlays.Values.Append(_background).OfType<MediaAsset>().Distinct()) asset.Dispose();
            _overlays.Clear();
        }
    }
}
