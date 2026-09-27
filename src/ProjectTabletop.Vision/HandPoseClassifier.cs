namespace ProjectTabletop.Vision;

/// <summary>
/// Image-space hand-pose heuristics on current MediaPipe landmarks. Geometry is
/// palm-normalized and independent of 2D camera rotation, mirroring and scale;
/// depth, severe foreshortening and physical contact are not inferred.
/// </summary>
public static class HandPoseClassifier
{
    /// <summary>
    /// Recognizes four extended front fingers without requiring a spread thumb
    /// or gaps between fingers. Confirmation time belongs to board interaction,
    /// so this reports only the current frame's geometry.
    /// </summary>
    public static bool AreFourFingersExtended(HandDetection? hand)
    {
        if (hand?.Landmarks is not { Count: 21 } points ||
            !double.IsFinite(hand.Confidence) || hand.Confidence is < .5 or > 1 ||
            !double.IsFinite(hand.RightHandProbability) || hand.RightHandProbability is < 0 or > 1 ||
            points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y))) return false;
        double length = Distance(points[0], points[9]), width = Distance(points[5], points[17]);
        double scale = Math.Max(length, width);
        if (!double.IsFinite(scale) || scale <= 1 || width < scale * .25 || length < scale * .35) return false;
        if (points.Any(point => !double.IsFinite(Distance(points[0], point)) || Distance(points[0], point) > scale * 4)) return false;
        PixelPoint wristToIndex = Subtract(points[5], points[0]), wristToPinky = Subtract(points[17], points[0]);
        double palmArea = Math.Abs(wristToIndex.X * wristToPinky.Y - wristToIndex.Y * wristToPinky.X) / (scale * scale);
        if (!double.IsFinite(palmArea) || palmArea < .12) return false;
        PixelPoint forward = Unit(Subtract(points[9], points[0]), length);
        foreach (int root in new[] { 5, 9, 13, 17 })
        {
            double path = FingerPath(points, root);
            PixelPoint extension = Subtract(points[root + 3], points[root]);
            // A relaxed hand may curve slightly. Require overall extension and
            // the tip beyond its first joint, while rejecting folded fingers.
            // The index may open sideways for selection; the other three still
            // need to extend forward along the palm.
            if (!double.IsFinite(path) || path < scale * .35 || path > scale * 2.2 ||
                Distance(points[root], points[root + 3]) < path * .78 ||
                Dot(extension, forward) < scale * (root == 5 ? -.08 : .25) ||
                Distance(points[0], points[root + 3]) < Distance(points[0], points[root + 1]) + scale * .12)
                return false;
            for (int part = root; part < root + 3; part++)
                if (Distance(points[part], points[part + 1]) < scale * .035) return false;
        }
        return true;
    }

    /// <summary>
    /// Describes the thumb-independent aim/open-index poses. Lateral gaps are
    /// measured perpendicular to the palm's forward axis and normalized by palm
    /// size; finger length differences therefore do not masquerade as opening.
    /// Neutral gaps between the two thresholds support interaction hysteresis.
    /// </summary>
    public static FingerSelectionPose DescribeFingerSelection(HandDetection? hand)
    {
        if (!AreFourFingersExtended(hand)) return new(false, false, false, null, null, null);
        var points = hand!.Landmarks;
        double length = Distance(points[0], points[9]);
        double scale = Math.Max(length, Distance(points[5], points[17]));
        var forward = Unit(Subtract(points[9], points[0]), length);
        var lateral = new PixelPoint(forward.Y, -forward.X);
        if (Dot(lateral, Subtract(points[5], points[17])) < 0) lateral = new(-lateral.X, -lateral.Y);
        double Gap(int first, int second) => Dot(Subtract(points[first], points[second]), lateral) / scale;
        double indexMiddle = Gap(8, 12), middleRing = Gap(12, 16), ringLittle = Gap(16, 20);
        double middleKnuckles = Gap(9, 13), ringKnuckles = Gap(13, 17);
        bool otherGrouped = middleRing >= -.08 && ringLittle >= -.08 &&
            middleRing <= Math.Max(.38, middleKnuckles * 1.20 + .03) &&
            ringLittle <= Math.Max(.38, ringKnuckles * 1.20 + .03);
        // Live aiming measured grouped gaps of .15-.27 and deliberate openings
        // of .45-.55. Palm-normalized thresholds preserve a .09 neutral band
        // without requiring an exaggerated opening from wider finger knuckles.
        const double togetherThreshold = .35, separatedThreshold = .44, roundoff = 1e-9;
        bool together = otherGrouped && indexMiddle >= -.08 && indexMiddle <= togetherThreshold + roundoff;
        bool separated = otherGrouped && indexMiddle >= separatedThreshold - roundoff;
        return new(together, separated, otherGrouped, indexMiddle, middleRing, ringLittle);
    }

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

/// <summary>Current finger-selection geometry; null gaps indicate an invalid or non-extended hand.</summary>
public sealed record FingerSelectionPose(bool Together, bool IndexSeparated, bool OtherFingersGrouped,
    double? IndexMiddleGap, double? MiddleRingGap, double? RingLittleGap);
