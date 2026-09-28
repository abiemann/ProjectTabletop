namespace ProjectTabletop.Calibration;

/// <summary>Four measured spot correspondences and an independent center check.</summary>
public static class BoardRegistration
{
    public const int SpotCount = 5;
    public const double MaximumCenterError = 0.015;

    // Conservative positions keep the complete disks on a centered board.
    // Their measured sequence establishes orientation even with a rotated webcam.
    public static Point2 SpotPosition(int index) => index switch
    {
        0 => new(.32, .32),
        1 => new(.68, .32),
        2 => new(.68, .68),
        3 => new(.32, .68),
        4 => new(.50, .50),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public static Homography FitAndValidate(IReadOnlyList<Point2> cameraPoints, out double centerError)
    {
        ArgumentNullException.ThrowIfNull(cameraPoints);
        if (cameraPoints.Count != SpotCount)
            throw new ArgumentException("Measure all five registration spots before accepting alignment.", nameof(cameraPoints));
        var map = Homography.FromFourPoints(cameraPoints.Take(4).ToArray(),
            Enumerable.Range(0, 4).Select(SpotPosition).ToArray());
        var actual = map.Transform(cameraPoints[4]);
        var expected = SpotPosition(4);
        centerError = Math.Sqrt(Math.Pow(actual.X - expected.X, 2) + Math.Pow(actual.Y - expected.Y, 2));
        if (centerError > MaximumCenterError)
            throw new InvalidOperationException($"The center check error is {centerError:F4} normalized, " +
                $"above the {MaximumCenterError:F3} limit.");
        return map;
    }
}
