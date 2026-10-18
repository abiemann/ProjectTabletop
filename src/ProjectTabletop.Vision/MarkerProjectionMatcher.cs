using System.Buffers;
using System.Runtime.CompilerServices;

namespace ProjectTabletop.Vision;

/// <summary>An immutable copy of the rendered board. CameraToBoard maps raw camera pixels to UV.
/// This reference is separate from the stationary caption images used for button presses.</summary>
public sealed record MarkerProjectionFrame(int Width, int Height, byte[] Bgra,
    IReadOnlyList<double> CameraToBoard, DateTimeOffset RenderedAt);

/// <summary>Vetoes marker-shaped details already present in the app's own projected artwork.
/// Local normalized correlation tolerates exposure, tint, optical blur and small registration
/// errors. It neither learns a still physical marker as background nor extrapolates a missing one.</summary>
internal sealed class MarkerProjectionMatcher
{
    private const int Grid = 11, Search = 8, Margin = Search + 2;
    private const double MatchCorrelation = .75;
    private static readonly ConditionalWeakTable<MarkerProjectionFrame, Reference> References = new();
    private static readonly (int X, int Y)[] Offsets = MakeOffsets();
    private readonly Reference[] _frames;
    public bool Ready => _frames.Length > 0;

    public MarkerProjectionMatcher(IReadOnlyList<MarkerProjectionFrame> frames, DateTimeOffset frameTime)
    {
        // RenderedAt comes from the caller's clock (the app's MonotonicClock).
        // A wall-clock default could disagree with it and reject every frame.
        if (frameTime == default)
            throw new ArgumentException("Provide FrameTime, from the clock that stamped RenderedAt, with projection frames.");
        _frames = frames.Take(8).Where(frame => Valid(frame) &&
            // The renderer can publish its current copy just after this camera exposure.
            // This is only a negative artwork veto, never a source of future tip coordinates.
            frame.RenderedAt <= frameTime + TimeSpan.FromMilliseconds(250) &&
            frameTime - frame.RenderedAt <= TimeSpan.FromMilliseconds(1000))
            .OrderByDescending(frame => frame.RenderedAt).Take(3)
            .Select(frame => References.GetValue(frame, static value => new(value))).ToArray();
    }

    /// <summary>Whether any current reference places this raw camera point on the board.</summary>
    public bool ContainsBoardPoint(PixelPoint center) => _frames.Any(frame => frame.InBoard(center.X, center.Y));

    /// <param name="originX">The raw camera X of gray pixel (0, 0) when gray is a crop.</param>
    public bool IsPhysicalBlackBarCandidate(MarkerTipObservation strip, byte[] gray, int width, int height,
        double scaleX, double scaleY, double originX = 0, double originY = 0) => IsPhysicalCandidate(
            strip.Center, strip.RadiusPixels, gray, width, height, scaleX, scaleY, strip.Bar, originX, originY);

    private bool IsPhysicalCandidate(PixelPoint center, double radiusPixels, byte[] gray, int width, int height,
        double scaleX, double scaleY, BlackBarGeometry? bar,
        double originX = 0, double originY = 0)
    {
        bool inBoard = false;
        foreach (Reference frame in _frames)
        {
            if (!frame.InBoard(center.X, center.Y)) continue;
            inBoard = true;
            if (Matches(frame, center, radiusPixels, gray, width, height, scaleX, scaleY, bar,
                originX, originY)) return false;
        }
        return inBoard;
    }

    private static bool Matches(Reference frame, PixelPoint center, double radiusPixels, byte[] gray,
        int width, int height, double scaleX, double scaleY, BlackBarGeometry? bar,
        double originX, double originY)
    {
        double cx = (center.X - originX) / scaleX, cy = (center.Y - originY) / scaleY;
        int radius = (int)Math.Round(Math.Clamp(radiusPixels / Math.Max(scaleX, scaleY) * 3, 8, 28));
        int size = (radius + Margin) * 2 + 1;
        double left = cx - radius - Margin, top = cy - radius - Margin;
        // At most 77 x 77 samples. Every used element is overwritten before reading;
        // keep a distinct pooled lease per call so simultaneous detectors cannot share scratch.
        double[] scratch = ArrayPool<double>.Shared.Rent(size * size);
        try
        {
            Span<double> expected = scratch.AsSpan(0, size * size);
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                expected[y * size + x] = frame.Sample(originX + (left + x) * scaleX, originY + (top + y) * scaleY);
            }

            Span<int> locations = stackalloc int[Grid * Grid];
            Span<double> camera = stackalloc double[Grid * Grid];
            double sum = 0, sumSquares = 0;
            // A neighbouring physical strip must not hide that this strip was projected.
            // Compare its narrow oriented band and immediate flanks independently.
            double barX = 0, barY = 0, axisX = 0, axisY = 0, halfLength = 0, halfWidth = 0;
            if (bar is not null)
            {
                barX = ((bar.End1.X + bar.End2.X) * .5 - originX) / scaleX;
                barY = ((bar.End1.Y + bar.End2.Y) * .5 - originY) / scaleY;
                axisX = (bar.End2.X - bar.End1.X) / scaleX;
                axisY = (bar.End2.Y - bar.End1.Y) / scaleY;
                double length = Math.Sqrt(axisX * axisX + axisY * axisY);
                if (length < 1) return false;
                axisX /= length; axisY /= length;
                halfLength = length * .5 + 3;
                // Include a sampled flank even when the grid spacing exceeds three pixels.
                // Otherwise a wide strip can contribute only uniform ink samples and an
                // exact projected copy incorrectly survives the zero-contrast comparison.
                double flank = Math.Max(3, radius * 2.0 / (Grid - 1));
                halfWidth = Math.Abs((bar.Side2.X - bar.Side1.X) / scaleX * -axisY +
                    (bar.Side2.Y - bar.Side1.Y) / scaleY * axisX) * .5 + flank;
            }
            int count = 0, eligible = 0;
            for (int gy = 0; gy < Grid; gy++)
            for (int gx = 0; gx < Grid; gx++)
            {
                int x = (int)Math.Round(gx * radius * 2.0 / (Grid - 1));
                int y = (int)Math.Round(gy * radius * 2.0 / (Grid - 1));
                if (bar is not null)
                {
                    double dx = cx - radius + x - barX, dy = cy - radius + y - barY;
                    if (Math.Abs(dx * axisX + dy * axisY) > halfLength ||
                        Math.Abs(dx * -axisY + dy * axisX) > halfWidth) continue;
                }
                eligible++;
                double value = Sample(gray, width, height, cx - radius + x, cy - radius + y);
                if (value < 0) continue;
                locations[count] = (y + Margin) * size + x + Margin;
                camera[count++] = value;
                sum += value; sumSquares += value * value;
            }
            if (count < 12 || count < eligible * .80) return false;
            double mean = sum / count, energy = sumSquares - sum * mean;
            if (energy < count * 16) return false;
            for (int i = 0; i < count; i++) camera[i] -= mean;

            // The unblurred and two compact Gaussian kernels cover a small camera marker without
            // requiring whole-frame resampling. Most background candidates exit at the first fit.
            foreach (var offset in Offsets)
            {
                for (int blur = 0; blur < 3; blur++)
                {
                    double bSum = 0, bSquares = 0, cross = 0;
                    bool complete = true;
                    for (int i = 0; i < count; i++)
                    {
                        int index = locations[i] + offset.Y * size + offset.X;
                        double value = expected[index];
                        if (value < 0) { complete = false; break; }
                        if (blur != 0)
                        {
                            if (expected[index - 1] < 0 || expected[index + 1] < 0 ||
                                expected[index - size] < 0 || expected[index + size] < 0 ||
                                expected[index - size - 1] < 0 || expected[index - size + 1] < 0 ||
                                expected[index + size - 1] < 0 || expected[index + size + 1] < 0)
                            { complete = false; break; }
                            double cardinal = expected[index - 1] + expected[index + 1] +
                                expected[index - size] + expected[index + size];
                            double diagonal = expected[index - size - 1] + expected[index - size + 1] +
                                expected[index + size - 1] + expected[index + size + 1];
                            value = blur == 1 ? value * .64 + cardinal * .08 + diagonal * .01 :
                                value * .25 + cardinal * .125 + diagonal * .0625;
                        }
                        if (value < 0) { complete = false; break; }
                        bSum += value; bSquares += value * value; cross += camera[i] * value;
                    }
                    if (!complete) continue;
                    double bEnergy = bSquares - bSum * bSum / count;
                    if (bEnergy < count * 16) continue;
                    // A faint turf stripe can correlate with actual black ink after arbitrary
                    // normalization. It cannot explain more than four times its contrast.
                    if (cross / Math.Sqrt(energy * bEnergy) >= MatchCorrelation &&
                        (bar is null || energy <= bEnergy * 16)) return true;
                }
            }
            return false;
        }
        finally { ArrayPool<double>.Shared.Return(scratch); }
    }

    private static bool Valid(MarkerProjectionFrame? frame) => frame is { Width: > 1 and <= 4096,
        Height: > 1 and <= 4096, Bgra: not null, CameraToBoard.Count: 9 } &&
        frame.Bgra.LongLength == frame.Width * (long)frame.Height * 4 &&
        frame.CameraToBoard.All(double.IsFinite);

    private sealed class Reference
    {
        private readonly int _width, _height;
        private readonly byte[] _gray;
        private readonly double[] _map;
        public Reference(MarkerProjectionFrame frame)
        {
            _width = frame.Width; _height = frame.Height; _map = frame.CameraToBoard.ToArray();
            _gray = new byte[_width * _height];
            for (int i = 0; i < _gray.Length; i++)
                _gray[i] = (byte)((frame.Bgra[i * 4] * 29 + frame.Bgra[i * 4 + 1] * 150 +
                    frame.Bgra[i * 4 + 2] * 77 + 128) >> 8);
        }
        public bool InBoard(double x, double y)
        {
            var (u, v) = Map(x, y);
            return u >= 0 && u <= 1 && v >= 0 && v <= 1;
        }
        public double Sample(double x, double y)
        {
            var (u, v) = Map(x, y);
            return MarkerProjectionMatcher.Sample(_gray, _width, _height, u * _width, v * _height);
        }
        private (double U, double V) Map(double x, double y)
        {
            double w = _map[6] * x + _map[7] * y + _map[8];
            if (!double.IsFinite(w) || Math.Abs(w) < 1e-10) return (double.NaN, double.NaN);
            return ((_map[0] * x + _map[1] * y + _map[2]) / w,
                (_map[3] * x + _map[4] * y + _map[5]) / w);
        }
    }

    private static double Sample(byte[] pixels, int width, int height, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= width - 1 || y >= height - 1)
            return -1;
        int ix = (int)x, iy = (int)y;
        double dx = x - ix, dy = y - iy;
        return pixels[iy * width + ix] * (1 - dx) * (1 - dy) +
            pixels[iy * width + ix + 1] * dx * (1 - dy) +
            pixels[(iy + 1) * width + ix] * (1 - dx) * dy + pixels[(iy + 1) * width + ix + 1] * dx * dy;
    }

    private static (int X, int Y)[] MakeOffsets() => (from y in Enumerable.Range(-Search, Search * 2 + 1)
        from x in Enumerable.Range(-Search, Search * 2 + 1) orderby x * x + y * y select (x, y)).ToArray();
}
