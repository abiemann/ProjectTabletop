using System.Diagnostics;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public sealed class DuckHuntFlightArticulationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(.55, -1)]
    [InlineData(1, 1)]
    [InlineData(1.8, -1)]
    public void SmallerBirdPreservesPhysicalAspectAndUsesTheRenderedWingPiecesForHits(double aspect, int direction)
    {
        var bird = Bird(0, direction);
        var bounds = DuckHuntFlightLayout.SpriteBounds(bird, aspect);
        Assert.Equal(Math.Min(.22, .25 / aspect) * .85, bounds.Width, 12);
        Assert.Equal(bounds.Width * aspect, bounds.Height, 12);
        foreach (double phase in new[] { 0.0, .35, .9, 1.5, 2.1, 2.7, 3.3, 4.1, 4.8, 5.5, 6.1 })
        {
            bird = Bird(phase, direction);
            var wing = DuckHuntFlightLayout.NearWingPose(bird);
            foreach (var point in new[] { wing.Inner.Transform(600, 1100), wing.Outer.Transform(400, 600) })
            {
                double u = direction < 0 ? 1 - point.U : point.U;
                var aim = new BoardAim(bounds.X + u * bounds.Width, bounds.Y + point.V * bounds.Height);
                Assert.True(DuckHuntFlightLayout.ContainsBird(aim, bird, aspect));
            }
        }
    }

    [Fact]
    public void WristRemainsSealedAndWingsBeatTogetherAcrossTheEntireCycle()
    {
        for (int i = 0; i <= 240; i++)
        {
            var bird = Bird(Math.Tau * i / 240);
            var near = DuckHuntFlightLayout.NearWingPose(bird);
            var far = DuckHuntFlightLayout.FarWingPose(bird);
            foreach (double x in new[] { 0.0, 400, 820, 1024 })
            {
                var inner = near.Inner.Transform(x, DuckHuntFlightLayout.WingWristV);
                var outer = near.Outer.Transform(x, DuckHuntFlightLayout.WingWristV);
                Assert.Equal(inner.U, outer.U, 12);
                Assert.Equal(inner.V, outer.V, 12);
            }
            var nearTip = near.Outer.Transform(392, 27);
            var farTip = far.Outer.Transform(392, 27);
            Assert.Equal((nearTip.U - .50) * .78, farTip.U - .54, 12);
            Assert.Equal((nearTip.V - .60) * .78, farTip.V - .59, 12);
            Assert.InRange(nearTip.U, -.1, .6);
            Assert.InRange(nearTip.V, -.05, 1.2);
        }
    }

    [Fact]
    public void FeatherTipsFlexBeyondTheWristAndRecoveryStrokeFoldsTheWing()
    {
        var down = DuckHuntFlightLayout.NearWingPose(Bird(Math.Tau * .21));
        var up = DuckHuntFlightLayout.NearWingPose(Bird(Math.Tau * .71));
        // The return stroke narrows the feather chord, with a slower raised
        // recovery; the primary feathers do not remain one rigid shoulder fan.
        Assert.True(up.Inner.M11 < down.Inner.M11 * .8);
        var rigidTip = down.Inner.Transform(392, 27);
        var articulatedTip = down.Outer.Transform(392, 27);
        Assert.True(Math.Abs(rigidTip.V - articulatedTip.V) > .02);
        var first = DuckHuntFlightLayout.NearWingPose(Bird(0));
        var fullCycle = DuckHuntFlightLayout.NearWingPose(Bird(Math.Tau));
        Assert.Equal(first, fullCycle);
    }

    [Fact]
    public void CollapsedOrInvalidWingCannotCreateAnUnboundedHitRegion()
    {
        var collapsed = new DuckHuntFlightWingTransform(.0003, 0, .0001, 0, .5, .6);
        Assert.False(collapsed.TryInverse(.5, .6, out _));
        var invalid = collapsed with { M22 = double.NaN };
        Assert.False(invalid.TryInverse(.5, .6, out _));
        for (int i = 0; i < 360; i++)
        {
            var bird = Bird(Math.Tau * i / 360);
            Assert.False(DuckHuntFlightLayout.ContainsBird(new(.01, .01), bird));
            Assert.False(DuckHuntFlightLayout.ContainsBird(new(.99, .99), bird));
        }
    }

    [Fact]
    public void SharedArticulatedHitTestingHasNoPerCallAllocations()
    {
        const int iterations = 20000;
        var bird = Bird(.3);
        for (int i = 0; i < 1000; i++)
            _ = DuckHuntFlightLayout.ContainsBird(new(.49, .39), bird with { FlapPhase = i * .03 });
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long began = Stopwatch.GetTimestamp();
        int hitCount = 0;
        for (int i = 0; i < iterations; i++)
            if (DuckHuntFlightLayout.ContainsBird(new(.49, .39), bird with { FlapPhase = i * .03 })) hitCount++;
        var elapsed = Stopwatch.GetElapsedTime(began);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(0, bytes);
        output.WriteLine($"{iterations} shared hit checks: {elapsed.TotalMilliseconds:F2} ms; {bytes} allocated bytes; {hitCount} hits.");
    }

    private static DuckHuntFlightSnapshot Bird(double phase, int direction = 1) =>
        new(DuckHuntFlightPhase.Flying, 0, 1, 0, .5, .4, direction, phase, 1, true, 1);
}
