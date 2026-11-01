using System.Reflection;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public class BoardDisturbanceShapeTests
{
    // Exercise the shared connected-obstruction gate with camera-grid residuals.
    // The old bounding-box rule wrongly accepted every thin-border shape below.
    [Theory]
    [InlineData("horizontal")]
    [InlineData("diagonal")]
    [InlineData("curve")]
    [InlineData("corner")]
    public void ThinBorderCannotBlockButAnIndependentFingerStillCan(string shape)
    {
        foreach (bool restarted in new[] { false, true })
        {
            var fixture = new ResidualFixture();
            if (!restarted) Assert.False(fixture.Detect());
            for (int x = 12; x < 92; x++)
            {
                int y = shape switch
                {
                    "diagonal" => 20 + (x - 12) / 27,
                    "curve" => 21 + (int)Math.Round(Math.Sin((x - 12) * Math.PI / 79)),
                    "corner" => x < 16 ? 20 + 16 - x : 20,
                    _ => 20
                };
                fixture.Fill(x, y, 1, 2);
            }

            Assert.False(fixture.Detect());
            Assert.False(fixture.Detect(collectRegions: false));
            fixture.Fill(54, 64, 7, 7);
            Assert.True(fixture.Detect());
            Assert.True(fixture.Detect(collectRegions: false));
            fixture.Fill(54, 64, 7, 7, occupied: false);
            Assert.False(fixture.Detect());
            fixture.Clear();
            Assert.False(fixture.Detect());
        }
    }

    [Theory]
    [InlineData(4, 45)] // Narrow arm through the central gap.
    [InlineData(7, 7)] // A compact fingertip away from the caption.
    [InlineData(25, 28)] // A palm over the artwork.
    public void SubstantialObstructionRetainsImmediateColdAndWarmVeto(int width, int height)
    {
        var fixture = new ResidualFixture();
        fixture.Fill(50, 35, width, height);
        Assert.True(fixture.Detect());
        Assert.True(fixture.Detect());
        Assert.True(fixture.Detect(collectRegions: false));
        fixture.Clear();
        Assert.False(fixture.Detect());
        fixture.Fill(50, 35, width, height);
        Assert.True(fixture.Detect());
    }

    private sealed class ResidualFixture
    {
        private const int Columns = 120, Rows = 100;
        private readonly HandAcquisitionPresenceTracker _tracker = new();
        private readonly bool[] _residual = new bool[Columns * Rows];

        public ResidualFixture()
        {
            Set("_columns", Columns);
            Set("_rows", Rows);
            Set("_outsideControlResidual", _residual);
            Set("_outsideControlVisited", new bool[_residual.Length]);
            Set("_outsideControlQueue", new int[_residual.Length]);
            Set("_outsideControlAreas", Enumerable.Repeat(1d / _residual.Length, _residual.Length).ToArray());
            Set("_outsideControlRefinementRegion", new bool[_residual.Length]);
        }

        public void Fill(int x, int y, int width, int height, bool occupied = true)
        {
            for (int row = y; row < y + height; row++)
            for (int column = x; column < x + width; column++)
                _residual[row * Columns + column] = occupied;
        }

        public void Clear() => Array.Clear(_residual);

        public bool Detect(bool collectRegions = true) => (bool)typeof(HandAcquisitionPresenceTracker)
            .GetMethod("FindOutsideControlObstruction", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_tracker, [collectRegions])!;

        private void Set(string name, object value) => typeof(HandAcquisitionPresenceTracker)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_tracker, value);
    }
}
