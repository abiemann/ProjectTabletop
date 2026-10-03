using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    public sealed record PhotoCopyCaptureContext(long Revision, double[] CameraToBoard,
        DateTimeOffset ReadyAfter, PhotoObjectTarget? Target);
    internal sealed record PhotoCopyMemoryImage(long Revision, PhotoHandCutout Cutout);
    internal sealed record PhotoCopySwirlImage(int Width, int Height, byte[] BgraPixels);

    private static readonly TimeSpan PhotoCopySurfaceSettle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PhotoCopyRemoveHandDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PhotoCopyStampInterval = TimeSpan.FromMilliseconds(25);
    private const string PhotoCopyReadyMessage = "Place an object above the controls and lift your hand. Hold Swirl or Copy for a second.";
    private long _photoCopySessionRevision = -1;
    private long _photoCopyRevision;
    private DateTimeOffset _photoCopySurfaceShownAt;
    private DateTimeOffset _photoCopyAnimationStarts;
    private PhotoHandCutout? _photoCopyCutout;
    private byte[]? _photoCopyPremultipliedPixels;
    private CanvasBitmap? _photoCopyBitmap;
    private CanvasRenderTarget? _photoCopyCameraTarget;
    private bool _photoCopyRenderFailed;
    private IReadOnlyList<PhotoCopyPlacement> _photoCopyPlacements = Array.Empty<PhotoCopyPlacement>();
    private string _photoCopyStatus = PhotoCopyReadyMessage;
    private string? _photoCopyMemorySaveStatus;
    private DateTimeOffset _photoCopySavedUntil, _photoCopyCountdownUntil;

    public string PhotoCopyStatus
    {
        get
        {
            lock (_gate)
            {
                SyncPhotoCopySession();
                return PhotoCopyDisplayStatus(DateTimeOffset.UtcNow);
            }
        }
    }

    internal string GetPhotoCopyDisplayStatus(DateTimeOffset now)
    {
        lock (_gate) { SyncPhotoCopySession(); return PhotoCopyDisplayStatus(now); }
    }

    public int PhotoCopyCount
    {
        get { lock (_gate) { SyncPhotoCopySession(); return PhotoCopyStampCount(DateTimeOffset.UtcNow); } }
    }

    public void ShowPhotoCopy()
    {
        lock (_gate)
        {
            CancelBoardReveal();
            _blackOutput = false;
            _boardSession.ShowPhotoCopy();
            ClearHandSpotlights();
            SyncPhotoCopySession();
        }
    }

    public void InvalidatePhotoCopyCapture()
    {
        lock (_gate)
        {
            _photoCopySessionRevision = _boardSession.Revision;
            _photoCopyGestureShutter = null;
            _photoCopyMemorySaveRequest = null;
            _photoCopyInputAllowed = false;
            _photoCopyReadyContext = null;
            _photoCopyMemorySaveStatus = null;
            _boardSession.PhotoCopyShutterEnabled = false;
            _boardSession.PhotoCopyHasSwirl = false;
            _photoCopyRevision++;
            _photoCopyCutout = null;
            _photoCopyPremultipliedPixels = null;
            _photoCopyRenderFailed = false;
            _photoCopyBitmap?.Dispose();
            _photoCopyBitmap = null;
            _photoCopyCameraTarget?.Dispose();
            _photoCopyCameraTarget = null;
            _photoCopyPlacements = Array.Empty<PhotoCopyPlacement>();
            _photoCopySurfaceShownAt = DateTimeOffset.MinValue;
            _photoCopyObjectTarget = null;
            _photoCopyObjectShownAt = DateTimeOffset.MinValue;
            _photoCopyAnimationStarts = DateTimeOffset.MinValue;
            _photoCopySavedUntil = _photoCopyCountdownUntil = DateTimeOffset.MinValue;
            _photoCopyStatus = PhotoCopyReadyMessage;
            _renderedBoardState = null;
        }
    }

    private void SyncPhotoCopySession()
    {
        HasMonopolyDicePresentation(_monopolyClock());
        if (_photoCopySessionRevision != _boardSession.Revision)
            InvalidatePhotoCopyCapture();
    }

    private bool PhotoCopyBoardReady => !_disposed && !_blackOutput && !_boardSetup && !IsBoardRevealActive &&
        _calibrationTarget < 0 && _boardSession.Screen == BoardScreen.PhotoCopy &&
        _boardMediaClip is not null && _boardCameraMap is not null && _boardSurfaceMap is not null;

    private bool PhotoCopyCaptureAllowed => PhotoCopyBoardReady && !_photoCopyRenderFailed;

    internal bool TryGetPhotoCopyMemoryImage(out PhotoCopyMemoryImage image)
    {
        lock (_gate)
        {
            SyncPhotoCopySession();
            image = null!;
            if (!PhotoCopyBoardReady || _photoCopyCutout is null) return false;
            image = new(_photoCopyRevision, _photoCopyCutout);
            return true;
        }
    }

    internal bool IsPhotoCopyMemoryImageCurrent(PhotoCopyMemoryImage image)
    {
        lock (_gate)
            return TryGetPhotoCopyMemoryImage(out var current) && current.Revision == image.Revision &&
                ReferenceEquals(current.Cutout, image.Cutout);
    }

    internal bool BeginPhotoCopyMemorySave(PhotoCopyMemoryImage image)
    {
        lock (_gate)
        {
            if (!IsPhotoCopyMemoryImageCurrent(image)) return false;
            _photoCopySavedUntil = DateTimeOffset.MinValue;
            _photoCopyMemorySaveStatus = "Saving image…";
            _renderedBoardState = null;
            return true;
        }
    }

    internal bool CompletePhotoCopyMemorySave(PhotoCopyMemoryImage image, DateTimeOffset? savedAt = null)
    {
        lock (_gate)
        {
            if (!IsPhotoCopyMemoryImageCurrent(image)) return false;
            _photoCopyMemorySaveStatus = null;
            _photoCopySavedUntil = (savedAt ?? DateTimeOffset.UtcNow).AddSeconds(3);
            _renderedBoardState = null;
            return true;
        }
    }

    internal void FailPhotoCopyMemorySave(PhotoCopyMemoryImage image)
    {
        lock (_gate)
        {
            if (!IsPhotoCopyMemoryImageCurrent(image)) return;
            _photoCopyMemorySaveStatus = "Image could not be saved. Check the Pictures folder and select Save again.";
            _renderedBoardState = null;
        }
    }

    public bool TryGetPhotoCopyCaptureContext(out PhotoCopyCaptureContext context)
    {
        lock (_gate)
        {
            SyncPhotoCopySession();
            context = null!;
            if (!PhotoCopyCaptureAllowed || _photoCopyCutout is not null ||
                _photoCopySurfaceShownAt == DateTimeOffset.MinValue) return false;
            var readyAfter = _photoCopySurfaceShownAt + PhotoCopySurfaceSettle;
            if (_photoCopyObjectTarget is not null)
            {
                if (_photoCopyObjectShownAt == DateTimeOffset.MinValue) return false;
                var illuminatedAfter = _photoCopyObjectShownAt + TimeSpan.FromMilliseconds(400);
                if (illuminatedAfter > readyAfter) readyAfter = illuminatedAfter;
            }
            if (DateTimeOffset.UtcNow < readyAfter) return false;
            var cameraToProjector = _boardCameraMap!.ToMatrix();
            var projectorToBoard = _boardSurfaceMap!.Inverse().ToMatrix();
            var cameraToBoard = new double[9];
            for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
            for (var middle = 0; middle < 3; middle++)
                cameraToBoard[row * 3 + column] +=
                    projectorToBoard[row * 3 + middle] * cameraToProjector[middle * 3 + column];
            context = new PhotoCopyCaptureContext(_photoCopyRevision, cameraToBoard, readyAfter, _photoCopyObjectTarget);
            return true;
        }
    }

    public bool SetPhotoCopyCapture(PhotoHandCutout cutout, long revision, PhotoObjectTarget? expectedTarget = null)
    {
        ArgumentNullException.ThrowIfNull(cutout);
        lock (_gate)
        {
            SyncPhotoCopySession();
            if (!PhotoCopyCaptureAllowed || revision != _photoCopyRevision || _photoCopyCutout is not null ||
                !ReferenceEquals(expectedTarget, _photoCopyObjectTarget))
                return false;
            if (cutout.Width <= 0 || cutout.Height <= 0 ||
                cutout.BgraPixels.LongLength != (long)cutout.Width * cutout.Height * 4 ||
                !double.IsFinite(cutout.PalmAnchor.X) || !double.IsFinite(cutout.PalmAnchor.Y) ||
                cutout.PalmAnchor.X < 0 || cutout.PalmAnchor.X > cutout.Width ||
                cutout.PalmAnchor.Y < 0 || cutout.PalmAnchor.Y > cutout.Height ||
                !double.IsFinite(cutout.MiddleFingerDirection.X) || !double.IsFinite(cutout.MiddleFingerDirection.Y) ||
                Math.Abs(cutout.MiddleFingerDirection.X) + Math.Abs(cutout.MiddleFingerDirection.Y) < 1e-6)
                return false;
            var placements = PhotoCopyLayout.Create(cutout.Width / (double)cutout.Height);
            _photoCopyCutout = cutout;
            _boardSession.PhotoCopyHasSwirl = true;
            _photoCopySavedUntil = _photoCopyCountdownUntil = DateTimeOffset.MinValue;
            _photoCopyObjectTarget = null;
            _photoCopyObjectShownAt = DateTimeOffset.MinValue;
            _photoCopyPremultipliedPixels = PhotoCopyBitmapPixels.Premultiply(cutout.BgraPixels);
            _photoCopyPlacements = placements;
            _photoCopyAnimationStarts = DateTimeOffset.UtcNow + PhotoCopyRemoveHandDelay;
            _renderedBoardState = null;
            return true;
        }
    }

    public void SetPhotoCopyStatus(string status, long revision)
    {
        lock (_gate)
        {
            SyncPhotoCopySession();
            if (!PhotoCopyCaptureAllowed || revision != _photoCopyRevision || _photoCopyCutout is not null) return;
            _photoCopyStatus = status;
        }
    }

    public bool BeginPhotoCopyCountdown(PhotoCopyCaptureContext context, DateTimeOffset due)
    {
        lock (_gate)
        {
            if (!IsPhotoCopyCaptureCurrent(context) || context.Target is null) return false;
            _photoCopySavedUntil = DateTimeOffset.MinValue;
            _photoCopyCountdownUntil = due;
            _renderedBoardState = null;
            return true;
        }
    }

    public bool BeginPhotoCopyCapture(PhotoCopyCaptureContext context)
    {
        lock (_gate)
        {
            if (!IsPhotoCopyCaptureCurrent(context)) return false;
            _photoCopySavedUntil = DateTimeOffset.MinValue;
            _photoCopyStatus = "Taking a photo of the object…";
            _renderedBoardState = null;
            return true;
        }
    }

    public void EndPhotoCopyCountdown(long revision)
    {
        lock (_gate)
        {
            SyncPhotoCopySession();
            if (_photoCopyRevision != revision) return;
            _photoCopyCountdownUntil = DateTimeOffset.MinValue;
            _renderedBoardState = null;
        }
    }

    // Called only after the final PNG has been written successfully.
    public bool SetPhotoCopyImageSaved(PhotoCopyCaptureContext context, DateTimeOffset? savedAt = null)
    {
        lock (_gate)
        {
            if (!IsPhotoCopyCaptureCurrent(context)) return false;
            _photoCopyCountdownUntil = DateTimeOffset.MinValue;
            _photoCopySavedUntil = (savedAt ?? DateTimeOffset.UtcNow).AddSeconds(3);
            _photoCopyStatus = PhotoCopyReadyMessage;
            _renderedBoardState = null;
            return true;
        }
    }

    private int PhotoCopyStampCount(DateTimeOffset now)
    {
        if (_photoCopyRenderFailed || _photoCopyCutout is null || now < _photoCopyAnimationStarts) return 0;
        return (int)Math.Min(_photoCopyPlacements.Count,
            1 + (now - _photoCopyAnimationStarts).TotalMilliseconds / PhotoCopyStampInterval.TotalMilliseconds);
    }

    private string PhotoCopyDisplayStatus(DateTimeOffset now)
    {
        if (now < _photoCopySavedUntil) return "Image Saved";
        if (_photoCopyMemorySaveStatus is { } memoryStatus) return memoryStatus;
        if (_photoCopyCountdownUntil != DateTimeOffset.MinValue)
        {
            int seconds = Math.Max(0, (int)Math.Ceiling((_photoCopyCountdownUntil - now).TotalSeconds));
            return seconds > 0 ? $"Copy in {seconds}… Move your hand away." : "Taking photo…";
        }
        if (_photoCopyRenderFailed) return _photoCopyStatus;
        if (_photoCopyCutout is null)
        {
            var shutter = CurrentFingerSelectionFeedback.FirstOrDefault(item => item.ButtonId == "photo-shutter");
            return shutter?.Stage switch
            {
                BoardFingerSelectionStage.Arming => "Keep four fingers together beside the subject.",
                BoardFingerSelectionStage.Armed => "Ready · move your index finger sideways to copy.",
                BoardFingerSelectionStage.Separating => "Taking your selection...",
                _ => _photoCopyStatus
            };
        }
        var count = PhotoCopyStampCount(now);
        if (count == 0) return "Photo captured. Remove the object and your hands.";
        return count < _photoCopyPlacements.Count
            ? $"Copying your photo: {count} of {_photoCopyPlacements.Count}"
            : "Copies complete. Select Save to save the swirl, or Clear to start again.";
    }

    private void DrawPhotoCopyStamps(CanvasDrawingSession surface, int count)
    {
        if (_photoCopyRenderFailed || _photoCopyCutout is not { } cutout || count == 0) return;
        var priorTransform = surface.Transform;
        var anchor = new Vector2((float)cutout.PalmAnchor.X, (float)cutout.PalmAnchor.Y);
        var originalAngle = Math.Atan2(cutout.MiddleFingerDirection.Y, cutout.MiddleFingerDirection.X);
        try
        {
            if (_photoCopyBitmap is null || !Equals(_photoCopyBitmap.Device, surface.Device))
            {
                _photoCopyBitmap?.Dispose();
                _photoCopyBitmap = CanvasBitmap.CreateFromBytes(surface.Device, _photoCopyPremultipliedPixels!,
                    cutout.Width, cutout.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
            }
            if (cutout.CameraGeometry is { } camera)
            {
                // This layer uses camera coordinates for geometry, but its pixels
                // must be dense enough for the current projector-resolution board.
                // Keep a single raster scale so photographed objects cannot stretch.
                float rasterScale = GetPhotoCopyCameraRasterScale(camera, _boardRasterPixels.Width,
                    _boardRasterPixels.Height, surface.Device.MaximumBitmapSizeInPixels);
                int rasterWidth = (int)Math.Ceiling(camera.FrameWidth * (double)rasterScale);
                int rasterHeight = (int)Math.Ceiling(camera.FrameHeight * (double)rasterScale);
                if (_photoCopyCameraTarget is null || !Equals(_photoCopyCameraTarget.Device, surface.Device) ||
                    _photoCopyCameraTarget.SizeInPixels.Width != rasterWidth ||
                    _photoCopyCameraTarget.SizeInPixels.Height != rasterHeight)
                {
                    // Keep the existing resource intact if allocating a larger
                    // display-resolution layer fails.
                    var replacement = new CanvasRenderTarget(surface.Device, rasterWidth, rasterHeight, 96);
                    var previous = _photoCopyCameraTarget;
                    _photoCopyCameraTarget = replacement;
                    previous?.Dispose();
                }
                DrawPhotoCopyNativeLayer(surface, _photoCopyCameraTarget, _photoCopyBitmap,
                    cutout, _photoCopyPlacements, count, rasterScale);
                return;
            }
            for (var index = 0; index < count; index++)
            {
                var placement = _photoCopyPlacements[index];
                var scale = (float)(placement.Height * BoardSurfaceSize / cutout.Height);
                // Rotate the photographed subject's upward axis toward the center.
                // For a detected hand, that axis is its measured middle finger.
                var rotation = (float)(placement.RotationRadians - Math.PI / 2 - originalAngle);
                surface.Transform = Matrix3x2.CreateTranslation(-anchor) *
                    Matrix3x2.CreateScale(scale) * Matrix3x2.CreateRotation(rotation) *
                    Matrix3x2.CreateTranslation((float)(placement.CenterU * BoardSurfaceSize),
                        (float)(placement.CenterV * BoardSurfaceSize)) * priorTransform;
                surface.DrawImage(_photoCopyBitmap);
            }
        }
        catch (Exception error) when (!surface.Device.IsDeviceLost(error.HResult))
        {
            HandlePhotoCopyRenderFailure(error);
        }
        finally { surface.Transform = priorTransform; }
    }

    // Draw native camera photographs using only rotation and a single scalar.
    // The one camera-to-board projection combines with the outer board-to-projector
    // mapping to retain alignment and the photograph's camera-view proportions.
    internal static void DrawPhotoCopyNativeLayer(CanvasDrawingSession surface, CanvasRenderTarget cameraLayer,
        CanvasBitmap bitmap, PhotoHandCutout cutout, IReadOnlyList<PhotoCopyPlacement> placements, int count,
        float rasterScale = 1)
    {
        if (!float.IsFinite(rasterScale) || rasterScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(rasterScale));
        var camera = cutout.CameraGeometry ?? throw new ArgumentException("A native photograph needs camera geometry.");
        var h = camera.CameraToBoard;
        var inverse = InvertPhotoCopyMap(h);
        Vector2 CameraPoint(double u, double v)
        {
            double denominator = inverse[6] * u + inverse[7] * v + inverse[8];
            if (!double.IsFinite(denominator) || Math.Abs(denominator) < 1e-10)
                throw new InvalidOperationException("A photo placement reached the camera's projective horizon.");
            return new((float)((inverse[0] * u + inverse[1] * v + inverse[2]) / denominator),
                (float)((inverse[3] * u + inverse[4] * v + inverse[5]) / denominator));
        }
        var cameraCenter = CameraPoint(.5, .5);
        var anchor = new Vector2((float)cutout.PalmAnchor.X, (float)cutout.PalmAnchor.Y);
        double originalAngle = Math.Atan2(cutout.MiddleFingerDirection.Y, cutout.MiddleFingerDirection.X);
        using (var drawing = cameraLayer.CreateDrawingSession())
        {
            drawing.Clear(Colors.Transparent);
            foreach (var placement in placements.Take(count))
            {
                var destination = CameraPoint(placement.CenterU, placement.CenterV);
                var inward = cameraCenter - destination;
                double du = .5 - placement.CenterU, dv = .5 - placement.CenterV;
                double distance = Math.Sqrt(du * du + dv * dv);
                if (distance < 1e-9 || inward.LengthSquared() < 1e-6)
                    throw new InvalidOperationException("A photo placement has no inward direction.");
                // Measure the desired board height along its inward axis near this
                // destination. Use that one camera-space length for both image axes.
                double halfHeight = placement.Height / 2;
                var start = CameraPoint(placement.CenterU - du / distance * halfHeight,
                    placement.CenterV - dv / distance * halfHeight);
                var end = CameraPoint(placement.CenterU + du / distance * halfHeight,
                    placement.CenterV + dv / distance * halfHeight);
                float scale = Vector2.Distance(start, end) / cutout.Height;
                float rotation = (float)(Math.Atan2(inward.Y, inward.X) - originalAngle);
                if (!float.IsFinite(scale) || scale <= 0 || !float.IsFinite(destination.X) || !float.IsFinite(destination.Y))
                    throw new InvalidOperationException("The native photograph has an invalid placement.");
                drawing.Transform = Matrix3x2.CreateTranslation(-anchor) * Matrix3x2.CreateScale(scale) *
                    Matrix3x2.CreateRotation(rotation) * Matrix3x2.CreateTranslation(destination) *
                    Matrix3x2.CreateScale(rasterScale);
                drawing.DrawImage(bitmap);
            }
        }
        var matrix = new Matrix4x4(
            (float)(BoardSurfaceSize * h[0] / rasterScale), (float)(BoardSurfaceSize * h[3] / rasterScale), 0, (float)(h[6] / rasterScale),
            (float)(BoardSurfaceSize * h[1] / rasterScale), (float)(BoardSurfaceSize * h[4] / rasterScale), 0, (float)(h[7] / rasterScale),
            0, 0, 1, 0,
            (float)(BoardSurfaceSize * h[2]), (float)(BoardSurfaceSize * h[5]), 0, (float)h[8]);
        using var projected = new Transform3DEffect
        {
            Source = cameraLayer, TransformMatrix = matrix,
            InterpolationMode = CanvasImageInterpolation.Linear, BorderMode = EffectBorderMode.Soft
        };
        surface.DrawImage(projected);
    }

    internal static float GetPhotoCopyCameraRasterScale(PhotoCopyCameraGeometry camera,
        int boardRasterWidth, int boardRasterHeight, int maximumBitmapSize)
    {
        if (boardRasterWidth <= 0 || boardRasterHeight <= 0 || maximumBitmapSize <= 1)
            throw new ArgumentOutOfRangeException(nameof(boardRasterWidth));
        var h = camera.CameraToBoard;
        var inverse = InvertPhotoCopyMap(h);
        double requiredScale = 1;
        // Perspective magnification varies across the board. Sample its complete
        // footprint, measuring the largest physical-pixel derivative in any
        // direction, rather than comparing the whole webcam and output widths.
        for (int row = 0; row <= 8; row++)
        for (int column = 0; column <= 8; column++)
        {
            double u = column / 8d, v = row / 8d;
            double inverseDenominator = inverse[6] * u + inverse[7] * v + inverse[8];
            if (!double.IsFinite(inverseDenominator) || Math.Abs(inverseDenominator) < 1e-10)
                throw new InvalidOperationException("The camera-to-board mapping reached its projective horizon.");
            double x = (inverse[0] * u + inverse[1] * v + inverse[2]) / inverseDenominator;
            double y = (inverse[3] * u + inverse[4] * v + inverse[5]) / inverseDenominator;
            double denominator = h[6] * x + h[7] * y + h[8];
            double a = boardRasterWidth * (h[0] - u * h[6]) / denominator;
            double b = boardRasterWidth * (h[1] - u * h[7]) / denominator;
            double c = boardRasterHeight * (h[3] - v * h[6]) / denominator;
            double d = boardRasterHeight * (h[4] - v * h[7]) / denominator;
            double trace = a * a + b * b + c * c + d * d;
            double determinant = a * d - b * c;
            double largestScale = Math.Sqrt((trace + Math.Sqrt(Math.Max(0,
                trace * trace - 4 * determinant * determinant))) / 2);
            if (!double.IsFinite(largestScale))
                throw new InvalidOperationException("The native photograph has an invalid raster scale.");
            requiredScale = Math.Max(requiredScale, largestScale);
        }
        // Round upward to avoid reallocations for insignificant mapping noise.
        // Reserve one pixel at the GPU limit for float/ceil rounding.
        double quantizedScale = Math.Ceiling(requiredScale * 16) / 16;
        double maximumScale = (maximumBitmapSize - 1d) / Math.Max(camera.FrameWidth, camera.FrameHeight);
        // Bound memory as well as either edge: a skewed mapping must not allocate
        // a maximum-width, maximum-height staging texture. The same scalar still
        // applies to both axes. Reserve one pixel on each axis before ceil rounds
        // the eventual texture dimensions, keeping its area within 64 MiPixels.
        const long maximumPixels = 64L * 1024 * 1024;
        double areaScale = Math.Sqrt(maximumPixels / ((double)camera.FrameWidth * camera.FrameHeight)) -
            1d / Math.Min(camera.FrameWidth, camera.FrameHeight);
        return (float)Math.Min(quantizedScale, Math.Min(maximumScale, areaScale));
    }

    private static double[] InvertPhotoCopyMap(IReadOnlyList<double> h)
    {
        if (h.Count != 9 || h.Any(value => !double.IsFinite(value)))
            throw new ArgumentException("The camera mapping must have nine finite coefficients.");
        double[] adjugate = [
            h[4] * h[8] - h[5] * h[7], h[2] * h[7] - h[1] * h[8], h[1] * h[5] - h[2] * h[4],
            h[5] * h[6] - h[3] * h[8], h[0] * h[8] - h[2] * h[6], h[2] * h[3] - h[0] * h[5],
            h[3] * h[7] - h[4] * h[6], h[1] * h[6] - h[0] * h[7], h[0] * h[4] - h[1] * h[3]
        ];
        double determinant = h[0] * adjugate[0] + h[1] * adjugate[3] + h[2] * adjugate[6];
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-12)
            throw new ArgumentException("The camera mapping is singular.");
        return adjugate.Select(value => value / determinant).ToArray();
    }

    private void HandlePhotoCopyRenderFailure(Exception error)
    {
        if (!_photoCopyRenderFailed) AppLog.Write("Photo Copy rendering", error);
        _photoCopyRenderFailed = true;
        _photoCopyStatus = "Photo could not be drawn. Select Clear to retry.";
        _renderedBoardState = null;
    }
}
