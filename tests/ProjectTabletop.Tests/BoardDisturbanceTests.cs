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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CameraDisturbancesToggleOncePerSwipeAcrossVisibilityReset(bool repeatDirection)
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
        // A deliberate crossing within one second. Changing captions can reset the camera
        // baseline while the same hand still covers the destination edge.
        for (int i = 0; i < 31; i++) Process(Hand(clear, .88 - i * .025, .55));
        Assert.Equal(new[] { BoardControlSwipe.Toggle }, commands);
        vision.Reset();
        gestures.Reset();
        byte[] endpoint = Hand(clear, .23, .55);
        time = Warm(vision, endpoint, time);
        for (int i = 0; i < 30; i++) Process(clear);
        for (int i = 0; i < 31; i++)
            Process(Hand(clear, repeatDirection ? .88 - i * .025 : .12 + i * .025, .55));
        Assert.Equal(new[] { BoardControlSwipe.Toggle, BoardControlSwipe.Toggle }, commands);
        for (int i = 0; i < 60; i++) Process(Hand(clear, repeatDirection ? .23 : .88, .55));
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
    public void DimSurfaceCrossingKeepsIdentityThroughBrightControlsAndToggles()
    {
        var tracker = new BoardDisturbanceTracker();
        var gestures = new BoardControlSwipeTracker();
        byte[] clear = new byte[Stride * Height];
        for (int x = 0; x < Width; x++)
        {
            byte shade = (byte)(26 + 84 * Math.Clamp((.60 - x / (double)Width) / .20, 0, 1));
            Paint(clear, x, 0, 1, Height, shade, shade, shade);
        }
        DateTimeOffset time = Warm(tracker, clear);
        for (int i = 0; i < 45; i++) Update(tracker, clear, ref time);
        long id = 0;
        var commands = new List<BoardControlSwipe>();
        for (int i = 0; i < 31; i++)
        {
            double center = .88 - i * .025;
            byte[] frame = (byte[])clear.Clone();
            int left = (int)(center * Width) - 25;
            for (int x = left; x < left + 50; x++)
            {
                byte shade = (byte)(clear[x * 4] * .12);
                Paint(frame, x, 26, 1, 54, shade, shade, shade);
            }
            IReadOnlyList<BoardMotionObservation> found = Update(tracker, frame, ref time);
            BoardMotionObservation hand = Assert.Single(found);
            if (id == 0) id = hand.TrackingId;
            Assert.Equal(id, hand.TrackingId);
            Assert.InRange(hand.U, center - .02, center + .02);
            BoardSwipeSample[] samples = [new(hand.TrackingId, hand.U, .82 + .18 * hand.V)];
            if (gestures.Update(samples, time, time) is { } command) commands.Add(command);
        }
        Assert.Equal(new[] { BoardControlSwipe.Toggle }, commands);
    }

    [Theory]
    [InlineData(26)]
    [InlineData(8)]
    public void DimSurfaceNoiseExposureAndSmallMovingObjectsRemainRejected(byte brightness)
    {
        var tracker = new BoardDisturbanceTracker();
        byte[] clear = new byte[Stride * Height];
        Paint(clear, 0, 0, Width, Height, brightness, brightness, brightness);
        DateTimeOffset time = Warm(tracker, clear);
        for (int i = 0; i < 150; i++)
        {
            byte[] frame = (byte[])clear.Clone();
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int noise = (x * 13 + y * 11 + i * 7) % 7 - 3;
                byte shade = (byte)Math.Clamp(brightness + noise + (i % 30 < 15 ? 5 : -3), 0, 255);
                Paint(frame, x, y, 1, 1, shade, shade, shade);
            }
            Paint(frame, 20 + i % 250, 70, 12, 8, 70, 70, 70);
            Assert.Empty(Update(tracker, frame, ref time));
        }
    }

    [Fact]
    public void BrightSurfaceRetainsItsOriginalLuminanceThreshold()
    {
        var tracker = new BoardDisturbanceTracker();
        byte[] clear = new byte[Stride * Height];
        Paint(clear, 0, 0, Width, Height, 100, 100, 100);
        DateTimeOffset time = Warm(tracker, clear);
        for (int i = 0; i < 30; i++)
        {
            byte[] frame = (byte[])clear.Clone();
            Paint(frame, 30 + i * 6, 26, 50, 54, 65, 65, 65);
            Assert.Empty(Update(tracker, frame, ref time));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SparseBrightDetailLossSupportsBothSwipeDirections(bool reverse)
    {
        var tracker = new BoardDisturbanceTracker();
        var gestures = new BoardControlSwipeTracker();
        byte[] clear = Stars();
        DateTimeOffset time = Warm(tracker, clear);
        var samples = new List<BoardSwipeSample>(4);
        var commands = new List<BoardControlSwipe>();
        int observedFrames = 0;
        for (int i = 0; i < 32; i++)
        {
            double center = reverse ? .11 + i * .025 : .89 - i * .025;
            byte[] frame = (byte[])clear.Clone();
            Paint(frame, (int)(center * Width) - 23, 23, 46, 55, 6, 6, 6);
            IReadOnlyList<BoardMotionObservation> found = Update(tracker, frame, ref time);
            samples.Clear();
            foreach (var detail in found)
            {
                Assert.Equal(0, detail.TrackingId);
                Assert.InRange(detail.U, center - .065, center + .065);
                samples.Add(new(detail.TrackingId, detail.U, .82 + .18 * detail.V));
            }
            if (found.Count > 0) observedFrames++;
            if (gestures.Update(samples, time, time) is { } command) commands.Add(command);
        }
        Assert.True(observedFrames >= 20);
        Assert.Equal(new[] { BoardControlSwipe.Toggle }, commands);
        // A bright detail reappearing after the hand leaves is not a new arrival.
        Assert.Empty(Update(tracker, clear, ref time));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalPaleStarLossesCanCrossWhileOneRemoteStarDrifts(bool reverse)
    {
        byte[] clear = TexturedPaleStars();
        const int remoteX = 12, remoteY = 12;
        Paint(clear, remoteX, remoteY, 1, 1, 140, 140, 140);
        AssertPaleStarSwipe(clear, reverse, noisyOcclusion: false, frameMilliseconds: 33,
            (frame, index) =>
            {
                // A star moving independently of the hand leaves a distant
                // loss. It must not enlarge the hand's region to the whole band.
                Paint(frame, remoteX, remoteY, 1, 1, 22, 22, 22);
                Paint(frame, remoteX + 2 + index / 6, remoteY, 1, 1, 140, 140, 140);
            });
    }

    [Theory]
    [InlineData(false, 33)]
    [InlineData(true, 33)]
    [InlineData(false, 66)]
    [InlineData(true, 66)]
    public void FluctuatingOcclusionOfPaleStarsDoesNotEraseTheMovingHand(bool reverse, int frameMilliseconds)
    {
        // The projected light changes on a moving hand. These variations are
        // part of the occlusion, not evidence that the clear sky became noisy.
        AssertPaleStarSwipe(TexturedPaleStars(), reverse, noisyOcclusion: true, frameMilliseconds);
    }

    [Fact]
    public void WidespreadPaleStarFlickerOnTexturedSkyDoesNotBecomeLocalMotion()
    {
        var tracker = new BoardDisturbanceTracker();
        byte[] clear = TexturedPaleStars();
        DateTimeOffset time = Warm(tracker, clear);
        for (int i = 0; i < 120; i++)
        {
            byte[] frame = (byte[])clear.Clone();
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int c = y * Stride + x * 4;
                int noise = (x * 13 + y * 11 + i * 7) % 5 - 2;
                int shade = clear[c] > 60 && i % 12 < 6 ? 24 : clear[c];
                for (int channel = 0; channel < 3; channel++)
                    frame[c + channel] = (byte)Math.Clamp(shade + noise + (i % 30 < 15 ? 4 : -3), 0, 255);
            }
            Assert.Empty(Update(tracker, frame, ref time));
        }
    }

    private static void AssertPaleStarSwipe(byte[] clear, bool reverse, bool noisyOcclusion,
        int frameMilliseconds, Action<byte[], int>? addRemoteMotion = null)
    {
        var tracker = new BoardDisturbanceTracker();
        var gestures = new BoardControlSwipeTracker();
        DateTimeOffset time = Warm(tracker, clear);
        var samples = new List<BoardSwipeSample>(4);
        int toggles = 0, observed = 0;
        const int frames = 36;
        for (int i = 0; i < frames; i++)
        {
            double center = reverse ? .10 + .80 * i / (frames - 1) : .90 - .80 * i / (frames - 1);
            byte[] frame = (byte[])clear.Clone();
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int c = y * Stride + x * 4;
                int noise = (x * 13 + y * 11 + i * 7) % 5 - 2;
                bool covered = Math.Abs(x - center * Width) <= 35 && y is >= 25 and < 75;
                int shade = covered ? noisyOcclusion && i % 2 == 0 ? 36 : 14 : clear[c];
                for (int channel = 0; channel < 3; channel++)
                    frame[c + channel] = (byte)Math.Clamp(shade + noise, 0, 255);
            }
            addRemoteMotion?.Invoke(frame, i);
            time = time.AddMilliseconds(frameMilliseconds);
            IReadOnlyList<BoardMotionObservation> found = tracker.Update(Width, Height, Stride, frame, Corners, time, time);
            samples.Clear();
            foreach (var detail in found)
            {
                Assert.InRange(detail.U, center - .11, center + .11);
                samples.Add(new(detail.TrackingId, detail.U, .82 + .18 * detail.V));
            }
            if (found.Count != 0) observed++;
            if (gestures.Update(samples, time, time) is not null) toggles++;
        }
        Assert.True(observed >= frames / 2, $"Only {observed}/{frames} hand positions were observed.");
        Assert.Equal(1, toggles);
        // Lifting is not a reverse swipe, even after the sky was temporarily
        // hidden and the distant star has moved independently.
        for (int i = 0; i < 20; i++)
        {
            time = time.AddMilliseconds(frameMilliseconds);
            samples.Clear();
            foreach (var detail in tracker.Update(Width, Height, Stride, clear, Corners, time, time))
                samples.Add(new(detail.TrackingId, detail.U, .82 + .18 * detail.V));
            Assert.Null(gestures.Update(samples, time, time));
        }
    }

    [Fact]
    public void SparseDetailRejectsNewBrightObjectsRipplesAndScatteredStarFlicker()
    {
        var tracker = new BoardDisturbanceTracker();
        byte[] clear = Stars();
        DateTimeOffset time = Warm(tracker, clear);
        for (int i = 0; i < 150; i++)
        {
            byte[] frame = (byte[])clear.Clone();
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int c = y * Stride + x * 4;
                int noise = (x * 13 + y * 11 + i * 7) % 7 - 3;
                double radius = Math.Sqrt(Math.Pow(x - Width * .5, 2) + Math.Pow((y - Height * .5) * 2, 2));
                bool ripple = Math.Abs(radius - (30 + i % 100)) < 1.5;
                for (int channel = 0; channel < 3; channel++)
                    frame[c + channel] = (byte)Math.Clamp(frame[c + channel] + noise +
                        (i % 30 < 15 ? 5 : -3) + (ripple ? 65 : 0), 0, 255);
            }
            Paint(frame, 20 + i % 250, 70, 12, 8, 225, 180, 55);
            Assert.Empty(Update(tracker, frame, ref time));
        }
        // Black surroundings make a global change in star brightness invisible
        // to median exposure compensation. All qualified losses still count
        // toward the spatial extent, including features beyond candidate slots.
        time = Warm(tracker, clear, time);
        for (int i = 0; i < 30; i++)
        {
            byte[] frame = (byte[])clear.Clone();
            for (int c = 0; c < frame.Length; c += 4)
                if (clear[c] > 60)
                    for (int channel = 0; channel < 3; channel++) frame[c + channel] = (byte)(i % 2 == 0 ? 12 : 170);
            Assert.Empty(Update(tracker, frame, ref time));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SinglePixelStarLossesSupplyBothSwipeDirections(bool reverse)
    {
        var tracker = new BoardDisturbanceTracker();
        var gestures = new BoardControlSwipeTracker();
        byte[] clear = FineStars();
        DateTimeOffset time = Warm(tracker, clear);
        var samples = new List<BoardSwipeSample>(4);
        int toggles = 0, observed = 0;
        // Continue to the physical edge: a sparse star's position can trail
        // the centre of the occluding hand until the next star is covered.
        for (int i = 0; i < 36; i++)
        {
            double center = reverse ? .11 + i * .025 : .89 - i * .025;
            byte[] frame = (byte[])clear.Clone();
            Paint(frame, (int)(center * Width) - 23, 23, 46, 55, 6, 6, 6);
            samples.Clear();
            foreach (var detail in Update(tracker, frame, ref time))
            {
                Assert.InRange(detail.U, center - .08, center + .08);
                samples.Add(new(detail.TrackingId, detail.U, .82 + .18 * detail.V));
            }
            if (samples.Count > 0) observed++;
            if (gestures.Update(samples, time, time) is not null) toggles++;
        }
        Assert.True(observed >= 20);
        Assert.Equal(1, toggles);
        Assert.Empty(Update(tracker, clear, ref time));
    }

    [Fact]
    public void FineStarFlickerAndExposureChangesDoNotSupplyLocalMotion()
    {
        var tracker = new BoardDisturbanceTracker();
        byte[] clear = FineStars();
        DateTimeOffset time = Warm(tracker, clear);
        for (int i = 0; i < 120; i++)
        {
            byte[] frame = (byte[])clear.Clone();
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int c = y * Stride + x * 4;
                int noise = (x * 13 + y * 11 + i * 7) % 7 - 3;
                int shade = clear[c] > 60 && (i % 2 == 0 || x > Width / 2) ? 8 : clear[c];
                for (int channel = 0; channel < 3; channel++)
                    frame[c + channel] = (byte)Math.Clamp(shade + noise + (i % 30 < 15 ? 5 : -3), 0, 255);
            }
            Paint(frame, 20 + i * 2, 70, 1, 1, 220, 180, 60);
            Assert.Empty(Update(tracker, frame, ref time));
        }
    }

    [Fact]
    public void OversampledMovingSingleStarCannotSupplyASwipe()
    {
        const int width = 96, height = 24, stride = width * 4;
        PixelPoint[] corners = [new(0, 0), new(width - 1, 0), new(width - 1, height - 1), new(0, height - 1)];
        byte[] dark = new byte[stride * height];
        Array.Fill(dark, (byte)8);
        byte[] clear = (byte[])dark.Clone();
        for (int channel = 0; channel < 3; channel++) clear[12 * stride + 12 * 4 + channel] = 170;
        var tracker = new BoardDisturbanceTracker();
        var gestures = new BoardControlSwipeTracker();
        var samples = new List<BoardSwipeSample>(4);
        DateTimeOffset time = Start;
        for (int i = 0; i < 12; i++)
        {
            time = time.AddMilliseconds(33);
            tracker.Update(width, height, stride, clear, corners, time, time);
        }
        for (int i = 0; i < 80; i++)
        {
            byte[] frame = (byte[])dark.Clone();
            for (int channel = 0; channel < 3; channel++) frame[12 * stride + (15 + i) * 4 + channel] = 170;
            time = time.AddMilliseconds(33);
            samples.Clear();
            foreach (var detail in tracker.Update(width, height, stride, frame, corners, time, time))
            {
                Assert.InRange(detail.U, .08, .17);
                samples.Add(new(detail.TrackingId, detail.U, .82 + .18 * detail.V));
            }
            Assert.Null(gestures.Update(samples, time, time));
        }
    }

    [Fact]
    public void AStationaryDetailMayBeObservedButCannotSupplyASwipe()
    {
        var tracker = new BoardDisturbanceTracker();
        var gestures = new BoardControlSwipeTracker();
        byte[] clear = Stars();
        DateTimeOffset time = Warm(tracker, clear);
        var samples = new List<BoardSwipeSample>(4);
        int observedFrames = 0;
        for (int i = 0; i < 120; i++)
        {
            byte[] frame = (byte[])clear.Clone();
            if (i % 6 < 3) Paint(frame, Width / 2 - 8, 23, 16, 55, 6, 6, 6);
            samples.Clear();
            foreach (var detail in Update(tracker, frame, ref time))
                samples.Add(new(detail.TrackingId, detail.U, .82 + .18 * detail.V));
            if (samples.Count > 0) observedFrames++;
            Assert.Null(gestures.Update(samples, time, time));
        }
        Assert.True(observedFrames > 0);
    }

    [Fact]
    public void MovingOneBrightFeatureCannotBorrowItsVacatedPositionForASwipe()
    {
        var tracker = new BoardDisturbanceTracker();
        var gestures = new BoardControlSwipeTracker();
        byte[] dark = new byte[Stride * Height];
        Paint(dark, 0, 0, Width, Height, 8, 8, 8);
        byte[] clear = (byte[])dark.Clone();
        Paint(clear, 46, 40, 8, 12, 170, 170, 170);
        DateTimeOffset time = Warm(tracker, clear);
        var samples = new List<BoardSwipeSample>(4);
        for (int i = 0; i < 120; i++)
        {
            byte[] frame = (byte[])dark.Clone();
            Paint(frame, 54 + i * 2, 40, 8, 12, 170, 170, 170);
            samples.Clear();
            foreach (var detail in Update(tracker, frame, ref time))
            {
                // Losing the original stationary feature may be observed, but
                // a new bright point cannot move that observation across the band.
                Assert.InRange(detail.U, .115, .15);
                samples.Add(new(detail.TrackingId, detail.U, .82 + .18 * detail.V));
            }
            Assert.Null(gestures.Update(samples, time, time));
        }
    }

    [Fact]
    public void SmallDarkObjectsNeedExistingBrightDetail()
    {
        var tracker = new BoardDisturbanceTracker();
        byte[] clear = new byte[Stride * Height];
        Paint(clear, 0, 0, Width, Height, 26, 26, 26);
        DateTimeOffset time = Warm(tracker, clear);
        for (int i = 0; i < 45; i++) Update(tracker, clear, ref time);
        for (int i = 0; i < 120; i++)
        {
            byte[] frame = (byte[])clear.Clone();
            Paint(frame, 20 + i * 2, 40, 12, 8, 0, 0, 0);
            Assert.Empty(Update(tracker, frame, ref time));
        }
    }

    [Fact]
    public void SparseDetailSamplingReusesMemory()
    {
        var tracker = new BoardDisturbanceTracker();
        byte[] clear = Stars(), covered = (byte[])clear.Clone();
        Paint(covered, Width / 2 - 8, 23, 16, 55, 6, 6, 6);
        DateTimeOffset time = Warm(tracker, clear);
        for (int i = 0; i < 20; i++) Update(tracker, i % 2 == 0 ? clear : covered, ref time);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        const int iterations = 300;
        for (int i = 0; i < iterations; i++) Update(tracker, i % 2 == 0 ? clear : covered, ref time);
        double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        output.WriteLine($"Sparse details: {milliseconds / iterations:F3} ms/frame, {bytes} managed bytes over {iterations} updates.");
        Assert.Equal(0, bytes);
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
        Assert.NotEqual(leftId, Assert.Single(Update(tracker, right, ref time)).TrackingId);
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
    public void CompactDisturbancesArePublishedImmediatelyWithoutAvailableProtectionTracks()
    {
        var tracker = new BoardDisturbanceTracker();
        byte[] clear = Frame();
        DateTimeOffset time = Warm(tracker, clear);
        byte[] four = Hand(Hand(Hand(Hand(clear, .12, .5), .36, .5), .62, .5), .87, .5);
        IReadOnlyList<BoardMotionObservation> found = Update(tracker, four, ref time);
        Assert.Equal(4, found.Count);
        Assert.Equal(2, found.Count(observation => observation.TrackingId == 0));
        Assert.All(found, observation => Assert.InRange(observation.V, .47, .53));
        Assert.Equal(4, Update(tracker, four, ref time).Count);
    }

    [Fact]
    public void SpatialCrossingCanUseDisconnectedCompactDisturbances()
    {
        var tracker = new BoardDisturbanceTracker();
        var gestures = new BoardControlSwipeTracker();
        byte[] clear = Frame();
        DateTimeOffset time = Warm(tracker, clear);
        var commands = new List<BoardControlSwipe>();
        var ids = new HashSet<long>();
        foreach (double center in new[] { .88, .73, .55, .37, .12 })
        {
            // Brief, separate disturbances need not survive the vision tracker's expiry.
            for (int i = 0; i < 5; i++)
            {
                Update(tracker, clear, ref time);
                gestures.Update([], time, time);
            }
            BoardMotionObservation current = Assert.Single(Update(tracker, Hand(clear, center, .55), ref time));
            ids.Add(current.TrackingId);
            BoardSwipeSample[] samples = [new(current.TrackingId, current.U, .82 + .18 * current.V)];
            if (gestures.Update(samples, time, time) is { } command) commands.Add(command);
        }
        Assert.True(ids.Count > 1);
        Assert.Equal(new[] { BoardControlSwipe.Toggle }, commands);
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

    private static byte[] Stars()
    {
        var frame = new byte[Stride * Height];
        Paint(frame, 0, 0, Width, Height, 8, 8, 8);
        for (int i = 1; i <= 9; i++)
            Paint(frame, (int)(Width * i / 10d) - 4, 37 + i % 3 * 3, 8, 12, 170, 170, 170);
        return frame;
    }

    private static byte[] FineStars()
    {
        byte[] frame = new byte[Stride * Height];
        Paint(frame, 0, 0, Width, Height, 8, 8, 8);
        for (int i = 1; i < 18; i++)
            Paint(frame, Width * i / 18, 29 + i * 7 % 32, 1, 1, 170, 170, 170);
        return frame;
    }

    private static byte[] TexturedPaleStars()
    {
        byte[] frame = new byte[Stride * Height];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            byte shade = (byte)(18 + (x / 12 + y / 8) % 9);
            Paint(frame, x, y, 1, 1, shade, shade, shade);
        }
        for (int x = 18; x < Width - 8; x += 24)
        for (int row = 0; row < 2; row++)
        {
            byte shade = (byte)(80 + (x / 24 + row) % 3 * 10);
            Paint(frame, x, 32 + row * 24, 5, 7, shade, shade, shade);
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
