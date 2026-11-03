namespace ProjectTabletop.Interaction;

/// <summary>Native feather pixels to the physically square bird rig. These same
/// affine coefficients drive the texture and its alpha-shaped hit polygon.</summary>
public readonly record struct DuckHuntFlightWingTransform(
    double M11, double M12, double M21, double M22, double M31, double M32)
{
    public BoardAim Transform(double x, double y) =>
        new(x * M11 + y * M21 + M31, x * M12 + y * M22 + M32);

    public bool TryInverse(double u, double v, out BoardAim source)
    {
        double determinant = M11 * M22 - M12 * M21;
        // The real wing briefly turns edge-on. Its vanishing area must not
        // turn into an unstable or oversized hit region.
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-10)
        {
            source = default;
            return false;
        }
        double x = u - M31, y = v - M32;
        source = new((x * M22 - y * M21) / determinant,
            (y * M11 - x * M12) / determinant);
        return double.IsFinite(source.U) && double.IsFinite(source.V);
    }
}

public readonly record struct DuckHuntFlightWingPose(
    DuckHuntFlightWingTransform Inner, DuckHuntFlightWingTransform Outer, double Opacity);

internal static class DuckHuntFlightWingMotion
{
    private const double DownstrokeFraction = .42;

    public static DuckHuntFlightWingPose Pose(double phase, bool far)
    {
        // Both wings beat together. Depth comes from projection and perspective,
        // rather than an alternating pair of flat fans rotating in the image.
        var shoulder = Beat(phase);
        var tips = Beat(phase - Math.Tau * .045);
        double perspective = far ? .78 : 1;
        double pivotU = far ? .54 : .50, pivotV = far ? .59 : .60;
        double span = DuckHuntFlightLayout.WingRootV - DuckHuntFlightLayout.WingTopV;
        double chord = .49 * (1 - .24 * shoulder.Fold) * perspective;
        double chordTilt = .022 * Math.Cos(shoulder.Elevation) * perspective;
        double innerSweep = (-.035 - .075 * Math.Cos(shoulder.Elevation)) * perspective;
        double innerLift = -.53 * (1 - .18 * shoulder.Fold) * Math.Sin(shoulder.Elevation) * perspective;
        double outerSweep = (-.035 - .075 * Math.Cos(tips.Elevation) - .025 * tips.Fold) * perspective;
        double outerLift = -.53 * (1 - .30 * tips.Fold) * Math.Sin(tips.Elevation) * perspective;

        // A shared chord axis makes the two texture pieces meet along the exact
        // same wrist line, while the primary feathers lag and fold independently.
        double xx = chord / span, xy = chordTilt / span;
        double innerYx = -innerSweep / span, innerYy = -innerLift / span;
        double outerYx = -outerSweep / span, outerYy = -outerLift / span;
        double rootX = DuckHuntFlightLayout.WingRootU, rootY = DuckHuntFlightLayout.WingRootV;
        double wristY = DuckHuntFlightLayout.WingWristV;
        var inner = new DuckHuntFlightWingTransform(xx, xy, innerYx, innerYy,
            pivotU - rootX * xx - rootY * innerYx,
            pivotV - rootX * xy - rootY * innerYy);
        var wrist = inner.Transform(rootX, wristY);
        var outer = new DuckHuntFlightWingTransform(xx, xy, outerYx, outerYy,
            wrist.U - rootX * xx - wristY * outerYx,
            wrist.V - rootX * xy - wristY * outerYy);
        return new(inner, outer, far ? .84 : 1);
    }

    private static (double Elevation, double Fold) Beat(double phase)
    {
        double cycle = phase / Math.Tau;
        cycle -= Math.Floor(cycle);
        const double top = 1.12, bottom = -.98;
        if (cycle < DownstrokeFraction)
        {
            double progress = cycle / DownstrokeFraction;
            double eased = .5 - .5 * Math.Cos(Math.PI * progress);
            return (top + (bottom - top) * eased, 0);
        }
        else
        {
            double progress = (cycle - DownstrokeFraction) / (1 - DownstrokeFraction);
            double eased = .5 - .5 * Math.Cos(Math.PI * progress);
            double fold = Math.Sin(Math.PI * progress);
            return (bottom + (top - bottom) * eased, fold * fold);
        }
    }
}
