using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public sealed class BoardDisturbanceDetailTests
{
    private const int Width = 384, Height = 96, Stride = Width * 4;
    private static readonly PixelPoint[] Corners =
        [new(0, 0), new(Width - 1, 0), new(Width - 1, Height - 1), new(0, Height - 1)];
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, 25)]
    [InlineData(true, 25)]
    [InlineData(false, 50)]
    [InlineData(true, 50)]
    [InlineData(false, 100)]
    [InlineData(true, 100)]
    public void RelaxedPaleStarOcclusionStillCrossesAtDifferentCameraRates(bool reverse, int interval)
    {
        var vision = new BoardDisturbanceTracker();
        byte[] clear = PaleStars();
        DateTimeOffset time = Warm(vision, clear);
        AssertSwipe(vision, clear, ref time, reverse, interval, 2.6);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2.6)]
    [InlineData(true, 2.6)]
    public void GraduallyFadingRemoteStarsDoNotHideAnActualSwipe(bool reverse, double duration)
    {
        var vision = new BoardDisturbanceTracker();
        byte[] clear = PaleStars();
        Paint(clear, 12, 7, 5, 7, 100);
        Paint(clear, 355, 9, 5, 7, 100);
        DateTimeOffset time = Warm(vision, clear);
        AssertSwipe(vision, clear, ref time, reverse, 25, duration, (frame, seconds) =>
        {
            // Two distant stars fade with the scenery while the moving hand
            // makes fresh losses elsewhere. They must not spread its footprint.
            byte shade = (byte)Math.Clamp(100 - (seconds + .8) * 40, 22, 100);
            Paint(frame, 12, 7, 5, 7, shade);
            Paint(frame, 355, 9, 5, 7, shade);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeldPaleStarOcclusionCanBeginARelaxedSwipe(bool reverse)
    {
        var vision = new BoardDisturbanceTracker();
        byte[] clear = PaleStars();
        DateTimeOffset time = Warm(vision, clear);
        byte[] held = (byte[])clear.Clone();
        Cover(held, reverse ? .08 : .92);
        for (int i = 0; i < 40; i++) Update(vision, held, ref time, 25);
        AssertSwipe(vision, clear, ref time, reverse, 25, 2.6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GlobalColourShiftDoesNotBecomeASwipeOrPreventTheNextOne(bool reverse)
    {
        var vision = new BoardDisturbanceTracker();
        var path = new BoardControlSwipeTracker();
        byte[] clear = PaleStars();
        DateTimeOffset time = Warm(vision, clear);
        for (int i = 0; i < clear.Length; i += 4)
        {
            clear[i] += 7;
            clear[i + 1] += 11;
            clear[i + 2] += 15;
        }
        var samples = new List<BoardSwipeSample>(4);
        for (int i = 0; i < 20; i++)
        {
            CopySamples(Update(vision, clear, ref time, 25), samples);
            Assert.Null(path.Update(samples, time, time));
        }
        AssertSwipe(vision, clear, ref time, reverse, 25, 2.6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetUsesTheNewStarfieldBeforeTheNextSwipe(bool reverse)
    {
        var vision = new BoardDisturbanceTracker();
        byte[] clear = PaleStars();
        DateTimeOffset time = Warm(vision, clear);
        byte[] held = (byte[])clear.Clone();
        Cover(held, .5);
        for (int i = 0; i < 12; i++) Update(vision, held, ref time, 25);
        vision.Reset();
        for (int i = 0; i < clear.Length; i += 4)
            for (int c = 0; c < 3; c++) clear[i + c] = (byte)Math.Max(0, clear[i + c] - 7);
        time = Warm(vision, clear, time);
        Assert.Empty(Update(vision, clear, ref time, 25));
        AssertSwipe(vision, clear, ref time, reverse, 25, 2.6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PaleStarsFadingWithoutAHandDoNotTraverseTheControls(bool staggered)
    {
        var vision = new BoardDisturbanceTracker();
        var path = new BoardControlSwipeTracker();
        byte[] clear = PaleStars();
        DateTimeOffset time = Warm(vision, clear);
        var samples = new List<BoardSwipeSample>(4);
        for (int index = 0; index < 240; index++)
        {
            byte[] frame = (byte[])clear.Clone();
            for (int x = 18; x < Width - 8; x += 24)
            for (int row = 0; row < 2; row++)
            {
                double elapsed = index * .025 - (staggered ? x / (double)Width * 2.2 : 0);
                int original = 80 + (x / 24 + row) % 3 * 10;
                byte shade = (byte)Math.Clamp(original - Math.Max(0, elapsed) * 30, 22, original);
                Paint(frame, x, 32 + row * 24, 5, 7, shade);
            }
            CopySamples(Update(vision, frame, ref time, 25), samples);
            Assert.Null(path.Update(samples, time, time));
            Assert.False(path.HasTraversalInProgress);
        }
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 1.5)]
    [InlineData(true, 1.5)]
    public void OrderedGradualSceneFadeCannotImpersonateASwipe(bool reverse, double fadeSeconds)
    {
        var vision = new BoardDisturbanceTracker();
        var path = new BoardControlSwipeTracker();
        var samples = new List<BoardSwipeSample>(4);
        // A scene transition can have a moving boundary without an occluding
        // hand. Each stationary star changes gradually, including fine cores.
        for (int index = 0; index < 400; index++)
        {
            double seconds = (index - 40) * .025;
            DateTimeOffset time = Start.AddMilliseconds(index * 25);
            byte[] frame = new byte[Stride * Height];
            Paint(frame, 0, 0, Width, Height, 8);
            for (int y = 12; y < Height - 10; y += 18)
            for (int x = 12; x < Width - 10; x += 12)
            {
                double u = x / (double)Width;
                double delay = 3 * (reverse ? 1 - u : u);
                byte shade = (byte)(140 - 132 * Math.Clamp((Math.Max(0, seconds) - delay) / fadeSeconds, 0, 1));
                int size = (x / 12 + y / 18) % 4 == 0 ? 3 : 1;
                Paint(frame, x, y, size, size, shade);
            }
            IReadOnlyList<BoardMotionObservation> found = vision.Update(Width, Height, Stride, frame, Corners, time, time);
            if (seconds <= 0) continue;
            CopySamples(found, samples);
            Assert.Null(path.Update(samples, time, time));
        }
    }

    private static void AssertSwipe(BoardDisturbanceTracker vision, byte[] clear, ref DateTimeOffset time,
        bool reverse, int interval, double duration, Action<byte[], double>? changeScenery = null)
    {
        var path = new BoardControlSwipeTracker();
        var samples = new List<BoardSwipeSample>(4);
        int commands = 0, observed = 0;
        int frames = (int)Math.Ceiling(duration * 1000 / interval) + 1;
        for (int index = 0; index < frames; index++)
        {
            double center = reverse ? .08 + .84 * index / (frames - 1) : .92 - .84 * index / (frames - 1);
            byte[] frame = (byte[])clear.Clone();
            changeScenery?.Invoke(frame, index * interval / 1000d);
            Cover(frame, center);
            IReadOnlyList<BoardMotionObservation> found = Update(vision, frame, ref time, interval);
            foreach (var point in found) Assert.InRange(point.U, center - .12, center + .12);
            CopySamples(found, samples);
            if (found.Count != 0) observed++;
            if (path.Update(samples, time, time) is not null) commands++;
        }
        Assert.True(observed >= frames / 2, $"Only {observed}/{frames} hand positions were observed.");
        Assert.Equal(1, commands);
        for (int index = 0; index < 25; index++)
        {
            CopySamples(Update(vision, clear, ref time, interval), samples);
            Assert.Null(path.Update(samples, time, time));
        }
    }

    private static void CopySamples(IReadOnlyList<BoardMotionObservation> found, List<BoardSwipeSample> samples)
    {
        samples.Clear();
        foreach (var point in found) samples.Add(new(point.TrackingId, point.U, .82 + .18 * point.V));
    }

    private static DateTimeOffset Warm(BoardDisturbanceTracker vision, byte[] frame, DateTimeOffset? start = null)
    {
        DateTimeOffset time = start ?? Start;
        for (int index = 0; index < 20; index++) Update(vision, frame, ref time, 25);
        return time;
    }

    private static IReadOnlyList<BoardMotionObservation> Update(BoardDisturbanceTracker vision, byte[] frame,
        ref DateTimeOffset time, int interval)
    {
        time = time.AddMilliseconds(interval);
        return vision.Update(Width, Height, Stride, frame, Corners, time, time);
    }

    private static byte[] PaleStars()
    {
        byte[] frame = new byte[Stride * Height];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++) Paint(frame, x, y, 1, 1, (byte)(18 + (x / 12 + y / 8) % 9));
        for (int x = 18; x < Width - 8; x += 24)
        for (int row = 0; row < 2; row++)
            Paint(frame, x, 32 + row * 24, 5, 7, (byte)(80 + (x / 24 + row) % 3 * 10));
        return frame;
    }

    private static void Cover(byte[] frame, double center) => Paint(frame, (int)(center * Width) - 35, 25, 70, 50, 14);

    private static void Paint(byte[] frame, int left, int top, int width, int height, byte shade)
    {
        for (int y = Math.Max(0, top); y < Math.Min(Height, top + height); y++)
        for (int x = Math.Max(0, left); x < Math.Min(Width, left + width); x++)
        {
            int c = y * Stride + x * 4;
            frame[c] = frame[c + 1] = frame[c + 2] = shade;
            frame[c + 3] = 255;
        }
    }
}
