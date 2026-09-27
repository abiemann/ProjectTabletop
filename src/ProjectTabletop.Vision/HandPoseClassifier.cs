namespace ProjectTabletop.Vision;

/// <summary>
/// Image-space hand-pose heuristics on current MediaPipe landmarks. Geometry is
/// palm-normalized and independent of 2D camera rotation, mirroring and scale;
/// depth, severe foreshortening and physical contact are not inferred.
/// </summary>
public static class HandPoseClassifier
{
    public static bool IsSpreadOut(HandDetection? hand)
    {
        if (hand?.Landmarks is not { Count: 21 } points ||
            !double.IsFinite(hand.Confidence) || hand.Confidence is < .5 or > 1 ||
            !double.IsFinite(hand.RightHandProbability) || hand.RightHandProbability is < 0 or > 1 ||
            points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y))) return false;
        double length = Distance(points[0], points[9]), width = Distance(points[5], points[17]);
        double scale = Math.Max(length, width);
        if (!double.IsFinite(scale) || scale <= 1 || width < scale * .35 || length < scale * .45) return false;
        PixelPoint wristToIndex = Subtract(points[5], points[0]), wristToPinky = Subtract(points[17], points[0]);
        double palmArea = Math.Abs(wristToIndex.X * wristToPinky.Y - wristToIndex.Y * wristToPinky.X) / (scale * scale);
        if (!double.IsFinite(palmArea) || palmArea < .18) return false;
        PixelPoint forward = Unit(Subtract(points[9], points[0]), length);
        PixelPoint towardThumb = Unit(Subtract(points[5], points[17]), width);

        foreach (int root in new[] { 5, 9, 13, 17 })
        {
            double path = FingerPath(points, root);
            PixelPoint extended = Subtract(points[root + 3], points[root]);
            if (!double.IsFinite(path) || path < scale * .45 || Distance(points[root], points[root + 3]) < path * .88 ||
                Dot(extended, forward) < scale * .30 ||
                Distance(points[0], points[root + 3]) < Distance(points[0], points[root + 1]) + scale * .18)
                return false;
        }
        // Finger lengths alone can make adjacent tips far apart even when all
        // fingers point together. Require lateral splay relative to their own
        // knuckle spacing, preserving anatomical order through camera mirroring.
        foreach (int root in new[] { 5, 9, 13 })
        {
            double knuckleGap = Dot(Subtract(points[root], points[root + 4]), towardThumb);
            double tipGap = Dot(Subtract(points[root + 3], points[root + 7]), towardThumb);
            if (knuckleGap < scale * .05 || tipGap < Math.Max(scale * .22, knuckleGap * 1.20)) return false;
        }
        if (Dot(Subtract(points[8], points[20]), towardThumb) < width * 1.60) return false;

        double thumbPath = FingerPath(points, 1);
        if (!double.IsFinite(thumbPath) || thumbPath < scale * .55 ||
            Distance(points[1], points[4]) < thumbPath * .85 ||
            Dot(Subtract(points[4], points[5]), towardThumb) < scale * .40 ||
            Distance(points[4], points[8]) < scale * .65) return false;
        return true;
    }

    private static double FingerPath(IReadOnlyList<PixelPoint> points, int root)
    {
        double path = 0;
        for (int index = root; index < root + 3; index++)
        {
            double segment = Distance(points[index], points[index + 1]);
            if (!double.IsFinite(segment) || segment <= 0) return double.NaN;
            path += segment;
        }
        return path;
    }
    private static PixelPoint Subtract(PixelPoint a, PixelPoint b) => new(a.X - b.X, a.Y - b.Y);
    private static PixelPoint Unit(PixelPoint vector, double length) => new(vector.X / length, vector.Y / length);
    private static double Dot(PixelPoint a, PixelPoint b) => a.X * b.X + a.Y * b.Y;
    private static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
