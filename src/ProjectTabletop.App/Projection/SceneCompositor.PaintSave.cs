using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    internal sealed record PaintMemoryImage(long Generation, long SaveId, int Width, int Height, byte[] BgraPixels);
    private sealed record PaintSaveRequest(DateTimeOffset FrameTime, long Generation);
    private PaintSaveRequest? _paintSaveRequest;
    private long _paintSaveGeneration, _paintSaveId;
    private bool _paintSaving;
    private string? _paintSaveError;
    private DateTimeOffset _paintSavedUntil;

    internal bool CanSavePaint
    {
        get
        {
            lock (_gate)
            {
                SyncPaintSession();
                return PaintInputReady && _paintDropCount > 0 && !_paintSaving;
            }
        }
    }

    internal string? GetPaintSaveStatus(DateTimeOffset now)
    {
        lock (_gate)
        {
            SyncPaintSession();
            if (_paintSaving) return "Saving image…";
            if (now < _paintSavedUntil) return "Image Saved";
            return _paintSaveError;
        }
    }

    // Only the camera observation that accepted Save may consume the request.
    internal void QueuePaintSaveRequest(DateTimeOffset frameTime)
    {
        lock (_gate)
            _paintSaveRequest = CanSavePaint ? new(frameTime, _paintSaveGeneration) : null;
    }

    internal bool TryTakePaintSaveRequest(DateTimeOffset frameTime, out PaintMemoryImage image)
    {
        lock (_gate)
        {
            SyncPaintSession();
            var request = _paintSaveRequest;
            _paintSaveRequest = null;
            image = null!;
            return request is not null && request.FrameTime == frameTime &&
                request.Generation == _paintSaveGeneration && TryBeginPaintSave(out image);
        }
    }

    internal bool TryBeginPaintSave(out PaintMemoryImage image)
    {
        lock (_gate)
        {
            image = null!;
            if (!CanSavePaint) return false;
            NotePaintUserActivity();
            _paintSaving = true;
            _paintSaveError = null;
            _paintSavedUntil = default;
            _paintSaveId++;
            _renderedBoardState = null;
            try
            {
                // Freeze the paint at the selection time. Controls, cursors and
                // camera pixels never enter this in-memory artwork snapshot.
                image = RenderPaintMemoryImage(_paintClock());
                return true;
            }
            catch
            {
                _paintSaving = false;
                _paintSaveError = "Save failed - retry";
                _renderedBoardState = null;
                throw;
            }
        }
    }

    internal bool IsPaintMemoryImageCurrent(PaintMemoryImage image)
    {
        lock (_gate)
        {
            SyncPaintSession();
            // Readiness gates taking the snapshot, not finishing its disk write.
            // Blanking/setup can temporarily hide the same board; completion
            // must still release its busy latch. Reset/navigation change the
            // generation and continue to reject obsolete feedback.
            return !_disposed && _paintSaving && image.Generation == _paintSaveGeneration &&
                image.SaveId == _paintSaveId;
        }
    }

    internal bool CompletePaintSave(PaintMemoryImage image, DateTimeOffset? savedAt = null)
    {
        lock (_gate)
        {
            if (!IsPaintMemoryImageCurrent(image)) return false;
            _paintSaving = false;
            _paintSaveError = null;
            _paintSavedUntil = (savedAt ?? _paintClock()).AddSeconds(3);
            _renderedBoardState = null;
            return true;
        }
    }

    internal void FailPaintSave(PaintMemoryImage image)
    {
        lock (_gate)
        {
            if (!IsPaintMemoryImageCurrent(image)) return;
            _paintSaving = false;
            _paintSaveError = "Save failed - retry";
            _renderedBoardState = null;
        }
    }

    private void ResetPaintSave()
    {
        _paintSaveGeneration++;
        _paintSaveRequest = null;
        _paintSaving = false;
        _paintSaveError = null;
        _paintSavedUntil = default;
    }

    private PaintMemoryImage RenderPaintMemoryImage(DateTimeOffset now)
    {
        if (_boardSurfaceMap is null || _projectorPixelWidth <= 0 || _projectorPixelHeight <= 0)
            throw new InvalidOperationException("The board must be projected before its artwork can be saved.");

        var h = _boardSurfaceMap.ToMatrix();
        var corners = new (double X, double Y)[4];
        (double U, double V)[] uv = [(0, 0), (1, 0), (1, 1), (0, 1)];
        for (int index = 0; index < corners.Length; index++)
        {
            var (u, v) = uv[index];
            double divisor = h[6] * u + h[7] * v + h[8];
            if (!double.IsFinite(divisor) || Math.Abs(divisor) < 1e-9)
                throw new InvalidOperationException("The board's saved-image geometry is invalid.");
            corners[index] = (_projectorPixelWidth * (h[0] * u + h[1] * v + h[2]) / divisor,
                _projectorPixelHeight * (h[3] * u + h[4] * v + h[5]) / divisor);
        }

        // Cache dimensions express sampling density, not the physical board's
        // aspect. Reapply the calibrated projection, then turn its top edge
        // upright so neither a preview resize nor board rotation stretches PNGs.
        double angle = Math.Atan2(corners[1].Y - corners[0].Y, corners[1].X - corners[0].X);
        double cosine = Math.Cos(angle), sine = Math.Sin(angle);
        var rotated = corners.Select(point => (X: cosine * point.X + sine * point.Y,
            Y: -sine * point.X + cosine * point.Y)).ToArray();
        double left = rotated.Min(point => point.X), top = rotated.Min(point => point.Y);
        double width = rotated.Max(point => point.X) - left, height = rotated.Max(point => point.Y) - top;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1)
            throw new InvalidOperationException("The board's saved-image bounds are invalid.");
        var device = CanvasDevice.GetSharedDevice();
        int pixelWidth = (int)Math.Ceiling(Math.Min(int.MaxValue, width - 1e-6));
        int pixelHeight = (int)Math.Ceiling(Math.Min(int.MaxValue, height - 1e-6));
        if (pixelWidth > device.MaximumBitmapSizeInPixels || pixelHeight > device.MaximumBitmapSizeInPixels ||
            (long)pixelWidth * pixelHeight > MaximumBoardRasterPixels)
            throw new InvalidOperationException("The saved artwork exceeds the graphics device's image limits.");

        var size = _boardRasterPixels;
        using var artwork = new CanvasRenderTarget(device, size.Width, size.Height, 96);
        using (var drawing = artwork.CreateDrawingSession())
        {
            drawing.Transform = BoardRasterTransform(artwork);
            drawing.Clear(PaintColor(3, 5, 12));
            DrawPaintSurface(drawing, now);
        }

        double X(int column) => cosine * _projectorPixelWidth * h[column] +
            sine * _projectorPixelHeight * h[column + 3] - left * h[column + 6];
        double Y(int column) => -sine * _projectorPixelWidth * h[column] +
            cosine * _projectorPixelHeight * h[column + 3] - top * h[column + 6];
        using var projected = new Transform3DEffect
        {
            Source = artwork,
            TransformMatrix = new Matrix4x4(
                (float)(X(0) / size.Width), (float)(Y(0) / size.Width), 0, (float)(h[6] / size.Width),
                (float)(X(1) / size.Height), (float)(Y(1) / size.Height), 0, (float)(h[7] / size.Height),
                0, 0, 1, 0,
                (float)X(2), (float)Y(2), 0, (float)h[8]),
            InterpolationMode = CanvasImageInterpolation.Linear,
            BorderMode = EffectBorderMode.Soft
        };
        using var target = new CanvasRenderTarget(device, pixelWidth, pixelHeight, 96);
        using (var drawing = target.CreateDrawingSession())
        {
            drawing.Clear(PaintColor(3, 5, 12));
            using var polygon = CanvasGeometry.CreatePolygon(device, rotated.Select(point =>
                new Vector2((float)(point.X - left), (float)(point.Y - top))).ToArray());
            using var boardClip = drawing.CreateLayer(1, polygon);
            drawing.DrawImage(projected);
        }
        return new(_paintSaveGeneration, _paintSaveId, pixelWidth, pixelHeight, target.GetPixelBytes());
    }
}
