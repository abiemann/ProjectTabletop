using System.Runtime.CompilerServices;

namespace ProjectTabletop.Vision;

/// <summary>An immutable copy of the rendered board. CameraToBoard maps raw camera pixels to UV.
/// This reference is separate from the stationary caption images used for button presses.</summary>
public sealed record EyeTipProjectionFrame(int Width, int Height, byte[] Bgra,
    IReadOnlyList<double> CameraToBoard, DateTimeOffset RenderedAt);

/// <summary>Optional learned scale, clicked/associated location, and current projected scene.
/// A supplied projection history limits candidates to that board and rejects its printed marks;
/// it never supplies positive evidence of a physical eye. Null preserves stand-alone detection.
/// FrameTime is required with a projection history and must use the clock of RenderedAt.</summary>
public sealed record EyeTipDetectionOptions(double? ExpectedRadiusPixels = null,
    PixelPoint? PreferredCenter = null, IReadOnlyList<EyeTipProjectionFrame>? ProjectionFrames = null,
    DateTimeOffset FrameTime = default)
{
    internal void Validate()
    {
        if (ExpectedRadiusPixels is { } radius && (!double.IsFinite(radius) || radius is < 1 or > 1024))
            throw new ArgumentException("Invalid expected eye radius.");
        if (PreferredCenter is { } center && (!double.IsFinite(center.X) || !double.IsFinite(center.Y) ||
            center.X < 0 || center.Y < 0)) throw new ArgumentException("Invalid preferred eye center.");
    }
}

/// <summary>Vetoes eye-shaped details already present in the app's own projected artwork.
/// Local normalized correlation tolerates exposure, tint, optical blur and small registration
/// errors. It neither learns a still physical marker as background nor extrapolates a missing one.</summary>
internal sealed class EyeTipProjectionMatcher
{
    private const int Grid = 11, Search = 8, Margin = Search + 2;
    private const double MatchCorrelation = .75;
    private static readonly ConditionalWeakTable<EyeTipProjectionFrame, Reference> References = new();
    private static readonly (int X, int Y)[] Offsets = MakeOffsets();
    private readonly Reference[] _frames;
    public bool Ready => _frames.Length > 0;

    public EyeTipProjectionMatcher(IReadOnlyList<EyeTipProjectionFrame> frames, DateTimeOffset frameTime)
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

    public bool IsPhysicalCandidate(EyeTipObservation eye, byte[] gray, int width, int height,
        double scaleX, double scaleY) => IsPhysicalCandidate(eye.Center, eye.RadiusPixels,
            gray, width, height, scaleX, scaleY);

    public bool IsPhysicalCandidate(PixelPoint center, double radiusPixels, byte[] gray, int width, int height,
        double scaleX, double scaleY) => IsPhysicalCandidate(center, radiusPixels, gray, width, height,
            scaleX, scaleY, null);

    public bool IsPhysicalColorCandidate(ColorTipObservation tip, ColorTipProfile profile, byte[] gray,
        int width, int height, double scaleX, double scaleY) =>
        IsPhysicalCandidate(tip.Center, tip.RadiusPixels, gray, width, height, scaleX, scaleY, profile);

    private bool IsPhysicalCandidate(PixelPoint center, double radiusPixels, byte[] gray, int width, int height,
        double scaleX, double scaleY, ColorTipProfile? color)
    {
        bool inBoard = false;
        foreach (Reference frame in _frames)
        {
            if (!frame.InBoard(center.X, center.Y)) continue;
            inBoard = true;
            if (Matches(frame, center, radiusPixels, gray, width, height, scaleX, scaleY, color)) return false;
        }
        return inBoard;
    }

    private static bool Matches(Reference frame, PixelPoint center, double radiusPixels, byte[] gray,
        int width, int height, double scaleX, double scaleY, ColorTipProfile? color)
    {
        double cx = center.X / scaleX, cy = center.Y / scaleY;
        int radius = (int)Math.Round(Math.Clamp(radiusPixels / Math.Max(scaleX, scaleY) * 3, 8, 28));
        int size = (radius + Margin) * 2 + 1;
        double left = cx - radius - Margin, top = cy - radius - Margin;
        var expected = new double[size * size];
        bool[]? expectedColor = color is null ? null : new bool[expected.Length];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            expected[y * size + x] = frame.Sample((left + x) * scaleX, (top + y) * scaleY);
            if (color is not null) expectedColor![y * size + x] =
                frame.ColorMatches((left + x) * scaleX, (top + y) * scaleY, color);
        }

        Span<int> locations = stackalloc int[Grid * Grid];
        Span<double> camera = stackalloc double[Grid * Grid];
        double sum = 0, sumSquares = 0;
        int count = 0;
        for (int gy = 0; gy < Grid; gy++)
        for (int gx = 0; gx < Grid; gx++)
        {
            int x = (int)Math.Round(gx * radius * 2.0 / (Grid - 1));
            int y = (int)Math.Round(gy * radius * 2.0 / (Grid - 1));
            double value = Sample(gray, width, height, cx - radius + x, cy - radius + y);
            if (value < 0) continue;
            locations[count] = (y + Margin) * size + x + Margin;
            camera[count++] = value;
            sum += value; sumSquares += value * value;
        }
        if (count < Grid * Grid * .80) return false;
        double mean = sum / count, energy = sumSquares - sum * mean;
        if (energy < count * 16) return false;
        for (int i = 0; i < count; i++) camera[i] -= mean;

        // The unblurred and two compact Gaussian kernels cover a small camera pupil without
        // requiring whole-frame resampling. Most background candidates exit at the first fit.
        foreach (var offset in Offsets)
        {
            // A tiny physical coloured pin can preserve most of its surrounding pebble texture.
            // Correlation of that larger grey patch is not evidence the pin itself was projected:
            // the rendered core must also contain the learned saturated colour at the same fit.
            if (expectedColor is not null && !CoreColorMatches(expectedColor, size, radius + Margin,
                Math.Clamp((int)Math.Round(radiusPixels / Math.Max(scaleX, scaleY) * .5), 2, 4), offset)) continue;
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
                if (cross / Math.Sqrt(energy * bEnergy) >= MatchCorrelation) return true;
            }
        }
        return false;
    }

    private static bool CoreColorMatches(bool[] colors, int size, int center, int radius, (int X, int Y) offset)
    {
        int count = 0, matching = 0;
        for (int y = -radius; y <= radius; y++)
        for (int x = -radius; x <= radius; x++)
        {
            if (x * x + y * y > radius * radius) continue;
            count++;
            if (colors[(center + offset.Y + y) * size + center + offset.X + x]) matching++;
        }
        return matching >= count * .60;
    }

    private static bool Valid(EyeTipProjectionFrame? frame) => frame is { Width: > 1 and <= 4096,
        Height: > 1 and <= 4096, Bgra: not null, CameraToBoard.Count: 9 } &&
        frame.Bgra.LongLength == frame.Width * (long)frame.Height * 4 &&
        frame.CameraToBoard.All(double.IsFinite);

    private sealed class Reference
    {
        private readonly int _width, _height;
        private readonly byte[] _gray;
        private readonly byte[] _bgra;
        private readonly double[] _map;
        public Reference(EyeTipProjectionFrame frame)
        {
            _width = frame.Width; _height = frame.Height; _map = frame.CameraToBoard.ToArray();
            _bgra = frame.Bgra;
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
            return EyeTipProjectionMatcher.Sample(_gray, _width, _height, u * _width, v * _height);
        }
        public bool ColorMatches(double x, double y, ColorTipProfile profile)
        {
            var (u, v) = Map(x, y);
            double px = u * _width, py = v * _height;
            if (!double.IsFinite(px) || !double.IsFinite(py) || px < 0 || py < 0 || px >= _width - 1 || py >= _height - 1)
                return false;
            int ix = (int)px, iy = (int)py;
            double dx = px - ix, dy = py - iy;
            double Channel(int channel) =>
                _bgra[(iy * _width + ix) * 4 + channel] * (1 - dx) * (1 - dy) +
                _bgra[(iy * _width + ix + 1) * 4 + channel] * dx * (1 - dy) +
                _bgra[((iy + 1) * _width + ix) * 4 + channel] * (1 - dx) * dy +
                _bgra[((iy + 1) * _width + ix + 1) * 4 + channel] * dx * dy;
            double r = Channel(2), g = Channel(1), b = Channel(0);
            double maximum = Math.Max(r, Math.Max(g, b)), minimum = Math.Min(r, Math.Min(g, b));
            double delta = maximum - minimum;
            if (maximum < 8 || delta / maximum < Math.Max(.18, profile.Saturation * .60)) return false;
            double hue = maximum == r ? 60 * (g - b) / delta : maximum == g ?
                120 + 60 * (b - r) / delta : 240 + 60 * (r - g) / delta;
            if (hue < 0) hue += 360;
            return ColorTipDetector.HueDistance(hue, profile.HueDegrees) <= Math.Max(25, profile.HueToleranceDegrees * 1.5);
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
