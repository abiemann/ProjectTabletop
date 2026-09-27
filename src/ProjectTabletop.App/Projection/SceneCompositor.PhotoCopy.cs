using System.Numerics;
using Microsoft.Graphics.Canvas;
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

    private static readonly TimeSpan PhotoCopySurfaceSettle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PhotoCopyRemoveHandDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PhotoCopyStampInterval = TimeSpan.FromMilliseconds(25);
    private const string PhotoCopyReadyMessage = "Place an object on grey; lift your hand briefly to lock its light. Then pinch beside it.";
    private long _photoCopySessionRevision = -1;
    private long _photoCopyRevision;
    private DateTimeOffset _photoCopySurfaceShownAt;
    private DateTimeOffset _photoCopyAnimationStarts;
    private PhotoHandCutout? _photoCopyCutout;
    private byte[]? _photoCopyPremultipliedPixels;
    private CanvasBitmap? _photoCopyBitmap;
    private bool _photoCopyRenderFailed;
    private IReadOnlyList<PhotoCopyPlacement> _photoCopyPlacements = Array.Empty<PhotoCopyPlacement>();
    private string _photoCopyStatus = PhotoCopyReadyMessage;

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

    public int PhotoCopyCount
    {
        get { lock (_gate) { SyncPhotoCopySession(); return PhotoCopyStampCount(DateTimeOffset.UtcNow); } }
    }

    public void ShowPhotoCopy()
    {
        lock (_gate)
        {
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
            _photoCopyRevision++;
            _photoCopyCutout = null;
            _photoCopyPremultipliedPixels = null;
            _photoCopyRenderFailed = false;
            _photoCopyBitmap?.Dispose();
            _photoCopyBitmap = null;
            _photoCopyPlacements = Array.Empty<PhotoCopyPlacement>();
            _photoCopySurfaceShownAt = DateTimeOffset.MinValue;
            _photoCopyObjectTarget = null;
            _photoCopyObjectShownAt = DateTimeOffset.MinValue;
            _photoCopyAnimationStarts = DateTimeOffset.MinValue;
            _photoCopyStatus = PhotoCopyReadyMessage;
            _renderedBoardState = null;
        }
    }

    private void SyncPhotoCopySession()
    {
        if (_photoCopySessionRevision != _boardSession.Revision)
            InvalidatePhotoCopyCapture();
    }

    private bool PhotoCopyCaptureAllowed => !_disposed && !_blackOutput && !_boardSetup && !_photoCopyRenderFailed &&
        _calibrationTarget < 0 && _boardSession.Screen == BoardScreen.PhotoCopy &&
        _boardMediaClip is not null && _boardCameraMap is not null && _boardSurfaceMap is not null;

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

    private int PhotoCopyStampCount(DateTimeOffset now)
    {
        if (_photoCopyRenderFailed || _photoCopyCutout is null || now < _photoCopyAnimationStarts) return 0;
        return (int)Math.Min(_photoCopyPlacements.Count,
            1 + (now - _photoCopyAnimationStarts).TotalMilliseconds / PhotoCopyStampInterval.TotalMilliseconds);
    }

    private string PhotoCopyDisplayStatus(DateTimeOffset now)
    {
        if (_photoCopyRenderFailed || _photoCopyCutout is null) return _photoCopyStatus;
        var count = PhotoCopyStampCount(now);
        if (count == 0) return "Photo captured. Remove the object and your hands.";
        return count < _photoCopyPlacements.Count
            ? $"Copying your photo: {count} of {_photoCopyPlacements.Count}"
            : "Copies complete. Pinch Capture again to make another photo.";
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

    private void HandlePhotoCopyRenderFailure(Exception error)
    {
        if (!_photoCopyRenderFailed) AppLog.Write("Photo Copy rendering", error);
        _photoCopyRenderFailed = true;
        _photoCopyStatus = "Photo could not be drawn. Pinch Capture again to retry.";
        _renderedBoardState = null;
    }
}
