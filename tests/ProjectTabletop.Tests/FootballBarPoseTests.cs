using System.Numerics;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

public class FootballBarPoseTests
{
    [Theory]
    [InlineData(0, -.45f, 1)]
    [InlineData(1, .45f, -1)]
    public void BarHalfChoosesOpponentFacingSideWithClearSpace(int player, float x, int direction)
    {
        var center = new Vector2(x, .1f);
        Assert.True(FootballBarPose.TryResolve(player, center, center + new Vector2(0, -.06f),
            center + new Vector2(0, .06f), center + new Vector2(-.01f, 0), center + new Vector2(.01f, 0),
            null, out var position, out float heading));
        Assert.InRange(MathF.Abs(position.X - (x + direction * (.01f + FootballGame.KickerRadius + FootballBarPose.Clearance))), 0, .00001f);
        Assert.Equal(center.Y, position.Y);
        Assert.InRange(MathF.Abs(MathF.Cos(heading) - direction), 0, .00001f);
    }

    [Fact]
    public void TranslationRotationAndEndpointOrderKeepTheObjectInFront()
    {
        foreach (float angle in new[] { -.8f, -.2f, .3f, 1.1f })
        {
            var forward = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var axis = new Vector2(-forward.Y, forward.X);
            var center = new Vector2(-.3f, .05f);
            foreach (int order in new[] { 1, -1 })
            {
                Assert.True(FootballBarPose.TryResolve(0, center, center - axis * .06f * order,
                    center + axis * .06f * order, center - forward * .009f, center + forward * .009f,
                    null, out var position, out float heading));
                Assert.InRange(Vector2.Distance(position, center + forward * (.009f + FootballGame.KickerRadius + FootballBarPose.Clearance)), 0, .00001f);
                Assert.InRange(MathF.Abs(heading - angle), 0, .00001f);
            }
        }
    }

    [Fact]
    public void SideOnNoiseKeepsThePreviousFacingAndInvalidGeometryIsRejected()
    {
        Assert.True(FootballBarPose.TryResolve(0, Vector2.Zero, new(-.06f, .001f), new(.06f, -.001f),
            new(0, -.01f), new(0, .01f), -MathF.PI / 2, out _, out float heading));
        Assert.True(MathF.Sin(heading) < -.99f);
        Assert.False(FootballBarPose.TryResolve(0, Vector2.Zero, Vector2.Zero, Vector2.Zero,
            Vector2.Zero, Vector2.Zero, null, out _, out _));
        Assert.False(FootballBarPose.TryResolve(0, new(float.NaN, 0), new(0, -.05f), new(0, .05f),
            new(-.01f, 0), new(.01f, 0), null, out _, out _));
    }
}
