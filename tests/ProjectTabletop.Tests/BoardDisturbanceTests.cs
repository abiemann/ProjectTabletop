using System.Diagnostics;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public sealed class BoardDisturbanceTests(ITestOutputHelper output)
{
    private const int Width = 384, Height = 96, Stride = Width * 4;
    private static readonly PixelPoint[] Corners = [new(0, 0), new(Width - 1, 0), new(Width - 1, Height - 1), new(0, Height - 1)];
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CrossingKeepsIdentityAndStationaryContactDoesNotDrift()
    {
        var tracker = new BoardDisturbanceTracker();
        byte[] clear = Frame();
        DateTimeOffset time = Warm(tracker, clear);
        long id = 0;
        for (int i = 0; i < 30; i++)
        {
            double center = .87 - i * .025;
            byte[] frame = Hand(clear, center, .55);
            IReadOnlyList<BoardMotionObservation> found = Update(tracker, frame, ref time);
            if (i == 0) { Assert.Empty(found); continue; }
            BoardMotionObservation hand = Assert.Single(found);
            if (id == 0) id = hand.TrackingId;
            Assert.Equal(id, hand.TrackingId);
            Assert.InRange(hand.U, center - .015, center + .015);
        }
        byte[] held = Hand(clear, .145, .55);
        for (int i = 0; i < 20; i++)
        {
            BoardMotionObservation hand = Assert.Single(Update(tracker, held, ref time));
            Assert.Equal(id, hand.TrackingId);
            Assert.InRange(hand.U, .13, .16);
        }
        // Stationary objects are deliberately absorbed so a hand in a new baseline cannot
        // leave a permanent ghost. Its brief removal residual must stay still and disappear.
        for (int i = 0; i < 35; i++)
        {
            IReadOnlyList<BoardMotionObservation> residual = Update(tracker, clear, ref time);
            for (int j = 0; j < residual.Count; j++) Assert.InRange(residual[j].U, .13, .16);
        }
        Assert.Empty(Update(tracker, clear, ref time));
    }

    [Fact]
    public void BaselineWithHandAndVisibilityTransitionCannotLeavePermanentGhosts()
    {
        var tracker = new BoardDisturbanceTracker();
        byte[] clear = Frame(), endpoint = Hand(clear, .16, .55);
        DateTimeOffset time = Warm(tracker, endpoint);
        for (int i = 0; i < 120; i++) Update(tracker, clear, ref time);
        Assert.Empty(Update(tracker, clear, ref time));
        long crossingId = 0;
        for (int i = 0; i < 28; i++)
        {
            byte[] hand = Hand(clear, .86 - i * .025, .55);
            IReadOnlyList<BoardMotionObservation> found = Update(tracker, hand, ref time);
            if (i < 2) continue;
            BoardMotionObservation current = Assert.Single(found);
            if (crossingId == 0) crossingId = current.TrackingId;
            Assert.Equal(crossingId, current.TrackingId);
        }
        tracker.Reset();
        time = Warm(tracker, endpoint, time);
        for (int i = 0; i < 120; i++) Update(tracker, clear, ref time);
        Assert.Empty(Update(tracker, clear, ref time));
    }

    [Fact]
    public void CameraDisturbancesProduceSingleSlowHideAndShowAcrossVisibilityReset()
    {
        var vision = new BoardDisturbanceTracker();
        var gestures = new BoardControlSwipeTracker();
        byte[] clear = Frame();
        DateTimeOffset time = Warm(vision, clear);
        var commands = new List<BoardControlSwipe>();
        var samples = new List<BoardSwipeSample>(2);
        void Process(byte[] frame)
        {
            samples.Clear();
            IReadOnlyList<BoardMotionObservation> found = Update(vision, frame, ref time);
            for (int i = 0; i < found.Count; i++)
                samples.Add(new(found[i].TrackingId, found[i].U, .82 + .18 * found[i].V));
            if (gestures.Update(samples, time, time) is { } command) commands.Add(command);
        }
        // 4.6 seconds, including the starting edge. Changing captions can reset the camera
        // baseline while the same hand still covers the destination edge.
        for (int i = 0; i < 140; i++) Process(Hand(clear, .88 - i * .00465, .55));
        Assert.Equal(new[] { BoardControlSwipe.Hide }, commands);
        vision.Reset();
        gestures.Reset();
        byte[] endpoint = Hand(clear, .23, .55);
        time = Warm(vision, endpoint, time);
        for (int i = 0; i < 30; i++) Process(clear);
        for (int i = 0; i < 140; i++) Process(Hand(clear, .12 + i * .00545, .55));
        Assert.Equal(new[] { BoardControlSwipe.Hide, BoardControlSwipe.Show }, commands);
        for (int i = 0; i < 60; i++) Process(Hand(clear, .88, .55));
        Assert.Equal(2, commands.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NeutralObjectsAndPartiallyClippedHandsRemainVisible(bool neutral)
    {
        var tracker = new BoardDisturbanceTracker();
        byte[] clear = Frame();
        DateTimeOffset time = Warm(tracker, clear);
        byte[] hand = Hand(clear, .75, .97, neutral: neutral);
        Update(tracker, hand, ref time);
        BoardMotionObservation result = Assert.Single(Update(tracker, hand, ref time));
        Assert.InRange(result.U, .73, .77);
        Assert.InRange(result.V, .78, 1);
    }

    [Fact]
    public void ExposureNoiseSmallObjectsAndAnimatedRipplesDoNotBecomeHands()
    {
        var tracker = new BoardDisturbanceTracker();
        byte[] clear = Frame();
        DateTimeOffset time = Warm(tracker, clear);
        for (int i = 0; i < 150; i++)
        {
            byte[] frame = Frame(i % 30 < 15 ? 22 : -12);
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int offset = y * Stride + x * 4;
                int noise = (x * 13 + y * 11 + i * 7) % 7 - 3;
                for (int c = 0; c < 3; c++) frame[offset + c] = (byte)Math.Clamp(frame[offset + c] + noise, 0, 255);
                double radius = Math.Sqrt(Math.Pow(x - Width * .5, 2) + Math.Pow((y - Height * .5) * 2, 2));
                if (Math.Abs(radius - (30 + i % 60)) < 1.5)
                    for (int c = 0; c < 3; c++) frame[offset + c] = (byte)Math.Min(255, frame[offset + c] + 65);
            }
            Paint(frame, 20 + i % 250, 70, 12, 8, 225, 180, 55);
            Assert.Empty(Update(tracker, frame, ref time));
        }
    }

    [Fact]
    public void SceneRedrawAndFreshnessBarriersCannotCarryAnIdentityAcrossScenes()
    {
        var tracker = new BoardDisturbanceTracker();
        byte[] clear = Frame();
        DateTimeOffset time = Warm(tracker, clear);
        byte[] hand = Hand(clear, .8, .55);
        Update(tracker, hand, ref time);
        long firstId = Assert.Single(Update(tracker, hand, ref time)).TrackingId;
        byte[] redraw = Frame();
        Paint(redraw, 0, 0, Width / 2, Height, 40, 180, 240);
        Assert.Empty(Update(tracker, redraw, ref time));
        for (int i = 0; i < 12; i++) Assert.Empty(Update(tracker, redraw, ref time));
        Assert.Empty(tracker.Update(Width, Height, Stride, hand, Corners, time, time.AddSeconds(1)));
        time = time.AddSeconds(1);
        time = Warm(tracker, clear, time);
        Update(tracker, hand, ref time);
        Assert.NotEqual(firstId, Assert.Single(Update(tracker, hand, ref time)).TrackingId);
        Assert.Empty(tracker.Update(Width, Height, Stride, hand, Corners, time, time));
        time = Warm(tracker, clear, time.AddMilliseconds(40));
        Update(tracker, hand, ref time);
        Assert.Single(Update(tracker, hand, ref time));
        time = time.AddSeconds(1);
        Assert.Empty(Update(tracker, hand, ref time));
        tracker.Reset();
        Assert.Empty(Update(tracker, hand, ref time));
    }

    [Fact]
    public void TwoObjectsStaySeparateAndDisconnectedArrivalsDoNotBorrowIdentity()
    {
        var tracker = new BoardDisturbanceTracker();
        byte[] clear = Frame();
        DateTimeOffset time = Warm(tracker, clear);
        byte[] left = Hand(clear, .15, .5);
        Update(tracker, left, ref time);
        long leftId = Assert.Single(Update(tracker, left, ref time)).TrackingId;
        byte[] right = Hand(clear, .85, .5);
        Assert.Empty(Update(tracker, right, ref time));
        Assert.NotEqual(leftId, Assert.Single(Update(tracker, right, ref time)).TrackingId);
        for (int i = 0; i < 8; i++) Update(tracker, clear, ref time);
        byte[] two = Hand(Hand(clear, .2, .5), .8, .5);
        Update(tracker, two, ref time);
        IReadOnlyList<BoardMotionObservation> both = Update(tracker, two, ref time);
        Assert.Equal(2, both.Count);
        Assert.NotEqual(both[0].TrackingId, both[1].TrackingId);
        Assert.True(Math.Abs(both[0].U - both[1].U) > .5);
    }

    [Fact]
    public void SamplingUsesTheSuppliedBandAndPlayerFacingOrientation()
    {
        var tracker = new BoardDisturbanceTracker();
        PixelPoint[] bottom = [new(0, Height * .5), new(Width - 1, Height * .5), new(Width - 1, Height - 1), new(0, Height - 1)];
        byte[] clear = Frame();
        DateTimeOffset time = Warm(tracker, clear, corners: bottom);
        byte[] outside = (byte[])clear.Clone();
        Paint(outside, 220, 0, 70, 35, 200, 110, 60);
        for (int i = 0; i < 15; i++) Assert.Empty(Update(tracker, outside, ref time, bottom));
        PixelPoint[] rotated = [Corners[2], Corners[3], Corners[0], Corners[1]];
        time = Warm(tracker, clear, time, rotated);
        byte[] hand = Hand(clear, .2, .35);
        Update(tracker, hand, ref time, rotated);
        BoardMotionObservation result = Assert.Single(Update(tracker, hand, ref time, rotated));
        Assert.InRange(result.U, .78, .82);
        Assert.InRange(result.V, .63, .67);
    }

    [Fact]
    public void PerspectiveSamplingDoesNotUseBilinearQuadCoordinates()
    {
        var tracker = new BoardDisturbanceTracker();
        PixelPoint[] trapezoid = [new(96, 0), new(288, 0), new(383, 95), new(0, 95)];
        byte[] clear = Frame();
        DateTimeOffset time = Warm(tracker, clear, corners: trapezoid);
        // With this trapezoid, region V=.5 projects near y=32, not bilinear y=47.5.
        byte[] hand = (byte[])clear.Clone();
        Paint(hand, 170, 20, 44, 24, 200, 110, 60);
        Update(tracker, hand, ref time, trapezoid);
        BoardMotionObservation result = Assert.Single(Update(tracker, hand, ref time, trapezoid));
        Assert.InRange(result.U, .47, .53);
        Assert.InRange(result.V, .44, .55);
    }

    [Fact]
    public void SteadyStateSamplingReusesMemory()
    {
        var tracker = new BoardDisturbanceTracker();
        byte[] clear = Frame(), hand = Hand(clear, .7, .55);
        DateTimeOffset time = Warm(tracker, clear);
        for (int i = 0; i < 20; i++) Update(tracker, hand, ref time);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        const int iterations = 300;
        for (int i = 0; i < iterations; i++) Update(tracker, i % 2 == 0 ? clear : hand, ref time);
        double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        output.WriteLine($"192x48 band: {milliseconds / iterations:F3} ms/frame, {bytes} managed bytes over {iterations} updates.");
        Assert.Equal(0, bytes);
    }

    private static DateTimeOffset Warm(BoardDisturbanceTracker tracker, byte[] clear,
        DateTimeOffset? start = null, IReadOnlyList<PixelPoint>? corners = null)
    {
        DateTimeOffset time = start ?? Start;
        for (int i = 0; i < 12; i++) Update(tracker, clear, ref time, corners);
        return time;
    }

    private static IReadOnlyList<BoardMotionObservation> Update(BoardDisturbanceTracker tracker, byte[] frame,
        ref DateTimeOffset time, IReadOnlyList<PixelPoint>? corners = null)
    {
        time = time.AddMilliseconds(33);
        return tracker.Update(Width, Height, Stride, frame, corners ?? Corners, time, time);
    }

    private static byte[] Frame(int exposure = 0)
    {
        var frame = new byte[Stride * Height];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int offset = y * Stride + x * 4, texture = (x * 7 + y * 13) % 17;
            frame[offset] = (byte)(90 + exposure + texture);
            frame[offset + 1] = (byte)(100 + exposure + texture);
            frame[offset + 2] = (byte)(95 + exposure + texture);
            frame[offset + 3] = 255;
        }
        return frame;
    }

    private static byte[] Hand(byte[] clear, double u, double v, bool neutral = false)
    {
        byte[] frame = (byte[])clear.Clone();
        Paint(frame, (int)(u * Width) - 25, (int)(v * Height) - 27, 50, 54,
            neutral ? (byte)175 : (byte)205, neutral ? (byte)180 : (byte)115, neutral ? (byte)170 : (byte)70);
        return frame;
    }

    private static void Paint(byte[] frame, int left, int top, int width, int height, byte red, byte green, byte blue)
    {
        for (int y = Math.Max(0, top); y < Math.Min(Height, top + height); y++)
        for (int x = Math.Max(0, left); x < Math.Min(Width, left + width); x++)
        {
            int offset = y * Stride + x * 4;
            frame[offset] = blue; frame[offset + 1] = green; frame[offset + 2] = red;
        }
    }
}
