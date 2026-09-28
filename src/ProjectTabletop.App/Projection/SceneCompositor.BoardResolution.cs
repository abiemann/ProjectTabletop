using System.Numerics;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // Layout and hit testing retain their logical coordinates. These dimensions
    // describe the actual GPU pixels, independent of Windows display scaling.
    private readonly record struct BoardRasterSize(int Width, int Height);
    private const long MaximumBoardRasterPixels = 64L * 1024 * 1024;
    private BoardRasterSize _boardRasterPixels = new(1000, 1000);
    private CanvasDevice? _boardRasterDevice;
    private int _projectorPixelWidth, _projectorPixelHeight;

    internal sealed record BoardResolutionDiagnostics(int LogicalSize, int BoardPixelWidth, int BoardPixelHeight,
        int FlightPixelWidth, int FlightPixelHeight, int PreviewPixelWidth, int PreviewPixelHeight,
        int ProjectorPixelWidth, int ProjectorPixelHeight);

    internal BoardResolutionDiagnostics GetBoardResolutionDiagnostics()
    {
        lock (_gate)
            return new((int)BoardSurfaceSize,
                (int)(_boardApplicationTarget?.SizeInPixels.Width ?? 0), (int)(_boardApplicationTarget?.SizeInPixels.Height ?? 0),
                (int)(_blackjackFlightTarget?.SizeInPixels.Width ?? 0), (int)(_blackjackFlightTarget?.SizeInPixels.Height ?? 0),
                (int)(_blackjackPreviewTarget?.SizeInPixels.Width ?? 0), (int)(_blackjackPreviewTarget?.SizeInPixels.Height ?? 0),
                _projectorPixelWidth, _projectorPixelHeight);
    }

    private void ReserveProjectedBoardPixels(CanvasDrawingSession drawing, Rect output, bool preview)
    {
        double width = output.Width * drawing.Dpi / 96, height = output.Height * drawing.Dpi / 96;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 ||
            width > int.MaxValue || height > int.MaxValue) return;
        if (!preview)
        {
            _projectorPixelWidth = Math.Max(_projectorPixelWidth, (int)Math.Ceiling(width));
            _projectorPixelHeight = Math.Max(_projectorPixelHeight, (int)Math.Ceiling(height));
        }
        var h = _boardSurfaceMap!.ToMatrix();
        // A perspective board can enlarge one end more than the other. Find
        // each UV derivative's actual maximum, not unrelated numerator and
        // denominator extremes that overestimate a strongly tapered board.
        double[] denominators = [h[8], h[6] + h[8], h[7] + h[8], h[6] + h[7] + h[8]];
        double denominator = denominators.Min(value => Math.Abs(value));
        if (h.Any(value => !double.IsFinite(value)) || denominator < 1e-9 ||
            denominators.Any(value => !double.IsFinite(value)) ||
            denominators.Any(value => Math.Sign(value) != Math.Sign(denominators[0]))) return;
        ReserveBoardPixels(drawing.Device, MaximumBoardAxisPixels(h, width, height, true),
            MaximumBoardAxisPixels(h, width, height, false));
    }

    private static double MaximumBoardAxisPixels(IReadOnlyList<double> h, double width, double height, bool u)
    {
        int xIndex = u ? 0 : 1, yIndex = u ? 3 : 4, axisIndex = u ? 6 : 7;
        double opposite = u ? h[7] : h[6];
        double x0 = width * (h[xIndex] * h[8] - h[axisIndex] * h[2]);
        double x1 = width * (h[xIndex] * opposite - h[axisIndex] * (u ? h[1] : h[0]));
        double y0 = height * (h[yIndex] * h[8] - h[axisIndex] * h[5]);
        double y1 = height * (h[yIndex] * opposite - h[axisIndex] * (u ? h[4] : h[3]));
        double a = x1 * x1 + y1 * y1, b = 2 * (x0 * x1 + y0 * y1), c = x0 * x0 + y0 * y0;
        if (!double.IsFinite(a) || !double.IsFinite(b) || !double.IsFinite(c)) return double.NaN;
        double maximum = 0;
        // At a fixed cross coordinate the numerator is constant along the
        // differentiated axis, so its maximum is at one of that axis's ends.
        for (int endpoint = 0; endpoint <= 1; endpoint++)
        {
            double d = opposite, e = h[axisIndex] * endpoint + h[8];
            void Evaluate(double cross)
            {
                if (!double.IsFinite(cross) || cross < 0 || cross > 1) return;
                double divisor = d * cross + e, x = x0 + x1 * cross, y = y0 + y1 * cross;
                maximum = Math.Max(maximum, Math.Sqrt(x * x + y * y) / (divisor * divisor));
            }
            Evaluate(0);
            Evaluate(1);
            // The squared length is N(t)/D(t)^4, with quadratic N and linear
            // D. Its remaining extrema are the roots of N'D - 4ND'.
            double qa = -2 * a * d, qb = 2 * a * e - 3 * b * d, qc = b * e - 4 * c * d;
            double scale = Math.Max(Math.Abs(qa), Math.Max(Math.Abs(qb), Math.Abs(qc)));
            if (!double.IsFinite(scale)) return double.NaN;
            if (scale == 0) continue;
            qa /= scale; qb /= scale; qc /= scale;
            if (Math.Abs(qa) < 1e-12)
            {
                if (Math.Abs(qb) >= 1e-12) Evaluate(-qc / qb);
                continue;
            }
            double discriminant = qb * qb - 4 * qa * qc;
            if (discriminant < -1e-12) continue;
            double q = -.5 * (qb + Math.CopySign(Math.Sqrt(Math.Max(0, discriminant)), qb));
            if (q == 0) Evaluate(-qb / (2 * qa));
            else
            {
                Evaluate(q / qa);
                Evaluate(qc / q);
            }
        }
        return maximum;
    }

    private void ReserveBoardPixels(CanvasDevice device, double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return;
        if (_boardRasterDevice != device)
        {
            _boardRasterDevice = device;
            _boardRasterPixels = new(1000, 1000);
        }
        int maximum = device.MaximumBitmapSizeInPixels;
        int previousWidth = Math.Min(_boardRasterPixels.Width, maximum);
        int previousHeight = Math.Min(_boardRasterPixels.Height, maximum);
        int nextWidth = Math.Max(previousWidth, (int)Math.Clamp(Math.Ceiling(width), 1, maximum));
        int nextHeight = Math.Max(previousHeight, (int)Math.Clamp(Math.Ceiling(height), 1, maximum));
        // Small laptop previews and diagnostic snapshots must never lower the
        // projector's sampling density. Preserve existing pixels first, fitting
        // only additional growth into the bounded GPU allocation budget.
        if ((long)nextWidth * nextHeight > MaximumBoardRasterPixels)
        {
            double low = 0, high = 1;
            for (int iteration = 0; iteration < 40; iteration++)
            {
                double middle = (low + high) / 2;
                double candidateWidth = previousWidth + (nextWidth - previousWidth) * middle;
                double candidateHeight = previousHeight + (nextHeight - previousHeight) * middle;
                if (candidateWidth * candidateHeight <= MaximumBoardRasterPixels) low = middle;
                else high = middle;
            }
            nextWidth = previousWidth + (int)Math.Floor((nextWidth - previousWidth) * low);
            nextHeight = previousHeight + (int)Math.Floor((nextHeight - previousHeight) * low);
        }
        _boardRasterPixels = new(nextWidth, nextHeight);
    }

    private bool EnsureBoardRenderTarget(ref CanvasRenderTarget? target, CanvasDevice device)
    {
        if (target is not null && target.Device == device &&
            target.SizeInPixels.Width == _boardRasterPixels.Width && target.SizeInPixels.Height == _boardRasterPixels.Height)
            return false;
        // Use explicit pixels at 96 DPI; don't create a dense 1000-DIP bitmap that
        // effect-input DPI compensation can shrink before perspective mapping.
        var replacement = new CanvasRenderTarget(device, _boardRasterPixels.Width, _boardRasterPixels.Height, 96);
        target?.Dispose();
        target = replacement;
        return true;
    }

    private void ResetBoardRaster()
    {
        CancelMonopolyDiceAnimation();
        DisposeMonopolyDiceLayer();
        CancelMonopolyEntrance();
        DisposeMonopolyEntranceLayers();
        _boardRasterPixels = new(1000, 1000);
        _boardRasterDevice = null;
        _projectorPixelWidth = _projectorPixelHeight = 0;
        _boardApplicationTarget?.Dispose();
        _boardApplicationTarget = null;
        _renderedBoardState = null;
        _blackjackFlightTarget?.Dispose();
        _blackjackFlightTarget = null;
        _blackjackPreviewTarget?.Dispose();
        _blackjackPreviewTarget = null;
        _blackjackPreviewRevision = -1;
        ResetPaint();
    }

    private static Matrix3x2 BoardRasterTransform(CanvasRenderTarget target) =>
        Matrix3x2.CreateScale((float)(target.SizeInPixels.Width / BoardSurfaceSize),
            (float)(target.SizeInPixels.Height / BoardSurfaceSize));
}
