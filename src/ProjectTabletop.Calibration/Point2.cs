namespace ProjectTabletop.Calibration;

/// <summary>A point in a caller-defined 2D coordinate system. Calibration uses raw image pixels.</summary>
public readonly record struct Point2(double X, double Y)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
}
