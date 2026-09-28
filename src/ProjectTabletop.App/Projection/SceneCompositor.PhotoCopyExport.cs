using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    internal PhotoCopySwirlImage RenderPhotoCopySwirlImage(PhotoCopyMemoryImage image)
    {
        lock (_gate)
        {
            if (!IsPhotoCopyMemoryImageCurrent(image))
                throw new InvalidOperationException("This swirl has already been cleared.");
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

            // The board cache stores sampling density, which can differ between
            // axes and grow when a preview gets larger. Apply the actual output
            // mapping before exporting so those cache scales cannot stretch the
            // saved artwork. A rigid rotation makes the board's top edge upright.
            double angle = Math.Atan2(corners[1].Y - corners[0].Y, corners[1].X - corners[0].X);
            double cosine = Math.Cos(angle), sine = Math.Sin(angle);
            var rotated = corners.Select(point => (X: cosine * point.X + sine * point.Y,
                Y: -sine * point.X + cosine * point.Y)).ToArray();
            double left = rotated.Min(point => point.X), top = rotated.Min(point => point.Y);
            double width = rotated.Max(point => point.X) - left, height = rotated.Max(point => point.Y) - top;
            if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1)
                throw new InvalidOperationException("The board's saved-image bounds are invalid.");
            var device = CanvasDevice.GetSharedDevice();
            // A quarter-turn can introduce a sub-pixel floating-point remainder
            // at an exact integer edge. It must not add an otherwise empty row.
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
                drawing.Clear(AppPalette.PhotoCopyBackground);
                // Render every stored placement, including the copies beneath
                // controls, without advancing the live Swirl animation.
                DrawPhotoCopyStamps(drawing, _photoCopyPlacements.Count);
                if (_photoCopyRenderFailed)
                    throw new InvalidOperationException("The swirl could not be rendered for saving.");
            }

            // Compose cache pixels -> board UV -> physical projector pixels ->
            // upright artwork bounds. Translation includes the homogeneous
            // denominator so perspective remains identical to the live board.
            double X(int column) => cosine * _projectorPixelWidth * h[column] +
                sine * _projectorPixelHeight * h[column + 3] - left * h[column + 6];
            double Y(int column) => -sine * _projectorPixelWidth * h[column] +
                cosine * _projectorPixelHeight * h[column + 3] - top * h[column + 6];
            var matrix = new Matrix4x4(
                (float)(X(0) / size.Width), (float)(Y(0) / size.Width), 0, (float)(h[6] / size.Width),
                (float)(X(1) / size.Height), (float)(Y(1) / size.Height), 0, (float)(h[7] / size.Height),
                0, 0, 1, 0,
                (float)X(2), (float)Y(2), 0, (float)h[8]);
            using var projected = new Transform3DEffect
            {
                Source = artwork,
                TransformMatrix = matrix,
                InterpolationMode = CanvasImageInterpolation.Linear,
                BorderMode = EffectBorderMode.Soft
            };
            using var target = new CanvasRenderTarget(device, pixelWidth, pixelHeight, 96);
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Clear(AppPalette.PhotoCopyBackground);
                using var polygon = CanvasGeometry.CreatePolygon(device, rotated.Select(point =>
                    new Vector2((float)(point.X - left), (float)(point.Y - top))).ToArray());
                using var boardClip = drawing.CreateLayer(1, polygon);
                drawing.DrawImage(projected);
            }
            return new(pixelWidth, pixelHeight, target.GetPixelBytes());
        }
    }
}
