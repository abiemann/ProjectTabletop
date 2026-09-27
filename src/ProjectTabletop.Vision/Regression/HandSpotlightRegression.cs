using ProjectTabletop.Vision;

internal static class HandSpotlightRegression
{
    public static void Run()
    {
        HandDetection hand = OpenHand();
        double[] flat = [.001, 0, .3, 0, .001, .3, 0, 0, 1];
        double[] rotated = [0, -.001, .7, .001, 0, .3, 0, 0, 1];
        double[] perspective = [.001, .00015, .3, -.0001, .001, .3, .0003, -.0001, 1];
        foreach (double aspect in new[] { 1.0, 16.0 / 9, 9.0 / 16 })
        foreach (double[] map in new[] { flat, rotated, perspective })
        {
            HandSpotlight light = Create(hand, map, aspect);
            foreach (PixelPoint point in hand.Landmarks.Skip(1))
            {
                PixelPoint projected = Map(point, map);
                double distance = Math.Sqrt(Math.Pow((projected.X - light.Center.X) * aspect, 2) +
                    Math.Pow(projected.Y - light.Center.Y, 2));
                Require(distance < light.Radius / 1.03,
                    "A thumb, finger joint or fingertip fell outside the spotlight margin.");
            }
            foreach (double scale in new[] { -7.0, 1e-10, 1e10 })
            {
                HandSpotlight equivalent = Create(hand, map.Select(value => value * scale).ToArray(), aspect);
                Require(Near(light.Center.X, equivalent.Center.X) && Near(light.Center.Y, equivalent.Center.Y) &&
                    Near(light.Radius, equivalent.Radius), "Scaling homography coefficients changed the spotlight.");
            }
        }

        HandSpotlight square = Create(hand, flat, 1);
        HandSpotlight wide = Create(hand, flat, 16.0 / 9);
        Require(Near(square.Center.X, wide.Center.X) && Near(square.Center.Y, wide.Center.Y) && wide.Radius > square.Radius,
            "The spotlight's center or radius did not account for projector aspect ratio.");

        // An open hand and a pinch both keep their fingertips illuminated while
        // the wrist no longer pulls the circle down or enlarges its footprint.
        foreach (HandDetection pose in new[] { hand, PinchingHand() })
        {
            PixelPoint[] extended = pose.Landmarks.ToArray();
            extended[0] = new(extended[0].X, extended[0].Y + 100);
            HandSpotlight ordinary = Create(pose, flat, 1);
            HandSpotlight wristExtended = Create(pose with { Landmarks = extended }, flat, 1);
            Require(Near(ordinary.Center.X, wristExtended.Center.X) && Near(ordinary.Center.Y, wristExtended.Center.Y),
                "Moving only the wrist pulled illumination away from the fingers.");
            foreach (HandDetection observation in new[] { pose, pose with { Landmarks = extended } })
            {
                HandSpotlight light = Create(observation, flat, 1);
                PixelPoint[] all = observation.Landmarks.Select(point => Map(point, flat)).ToArray();
                PixelPoint oldCenter = new((all.Min(point => point.X) + all.Max(point => point.X)) / 2,
                    (all.Min(point => point.Y) + all.Max(point => point.Y)) / 2);
                double oldPalmSpan = Math.Max(Distance(all[0], all[9]), Distance(all[5], all[17]));
                double oldRadius = all.Max(point => Distance(point, oldCenter)) * 1.04 + oldPalmSpan * .025;
                Require(light.Center.Y < oldCenter.Y && light.Radius < oldRadius,
                    "Finger-focused illumination did not move toward the fingers and shrink from the former wrist-inclusive circle.");
                foreach (PixelPoint point in all.Skip(1))
                    Require(Distance(point, light.Center) < light.Radius / 1.03,
                        "An open or pinching hand lost thumb/finger coverage after excluding its wrist.");
            }
        }

        // Keep a physical hand and its output pixel dimensions unchanged while
        // widening only the output: the circle's pixel radius must not change.
        double[] widerOutput = flat.ToArray();
        for (int index = 0; index < 3; index++) widerOutput[index] /= 2;
        HandSpotlight samePixels = Create(hand, widerOutput, 2);
        Require(Near(square.Radius, samePixels.Radius) && Near(square.Center.X / 2, samePixels.Center.X),
            "An output aspect change stretched the physically circular spotlight.");

        double[] edge = [.001, 0, -.18, 0, .001, .3, 0, 0, 1];
        HandSpotlight clipped = Create(hand, edge, 1);
        Require(clipped.Center.X < 0 && clipped.Center.X + clipped.Radius > 0,
            "A hand partly outside the projector was rejected or clamped away from its true position.");

        Reject(hand with { Landmarks = hand.Landmarks.Take(20).ToArray() }, flat, 1, "Incomplete landmarks");
        PixelPoint[] invalid = hand.Landmarks.ToArray();
        invalid[12] = new(double.NaN, 20);
        Reject(hand with { Landmarks = invalid }, flat, 1, "Non-finite landmarks");
        invalid = hand.Landmarks.ToArray();
        invalid[0] = new(double.NaN, 20);
        Reject(hand with { Landmarks = invalid }, flat, 1, "Non-finite wrist used for palm scale");
        Reject(hand with { Landmarks = Enumerable.Repeat(new PixelPoint(100, 100), 21).ToArray() }, flat, 1,
            "Collapsed landmarks");
        Reject(hand with { Confidence = double.NaN }, flat, 1, "Invalid model scores");
        Reject(null, flat, 1, "Missing hand");
        Reject(hand, null, 1, "Missing calibration");
        Reject(hand, flat.Take(8).ToArray(), 1, "Incomplete calibration");
        Reject(hand, [1, 0, 0, 0, double.PositiveInfinity, 0, 0, 0, 1], 1, "Non-finite calibration");
        Reject(hand, new double[9], 1, "Zero calibration");
        Reject(hand, [.001, 0, .3, .001, 0, .3, 0, 0, 1], 1, "Singular calibration");
        Reject(hand, [.001, 0, .3, 0, .001, .3, .001, 0, -.1], 1, "A horizon across the hand");
        Reject(hand, [.001, 0, .3, 0, .001, .3, .001, 0, -.12], 1, "A landmark on the horizon");
        Reject(hand, [.01, 0, .3, 0, .01, .3, 0, 0, 1], 1, "Excessively large footprint");
        foreach (double aspect in new[] { 0.0, -1, double.NaN, double.PositiveInfinity })
            Reject(hand, flat, aspect, "Invalid aspect ratio");

        Console.WriteLine("Hand spotlight geometry regression: thumb/finger coverage, smaller finger-focused open/pinch circles, " +
            "wrist-independent centering, rotation/perspective, projector aspect, equivalent homographies, " +
            "board-edge clipping and invalid geometry passed.");
    }

    private static HandDetection OpenHand() => new(
    [
        new(120, 230), new(90, 190), new(65, 155), new(45, 135), new(20, 120),
        new(80, 140), new(75, 100), new(70, 70), new(65, 40),
        new(115, 130), new(115, 85), new(115, 50), new(115, 25),
        new(150, 140), new(155, 100), new(160, 70), new(165, 45),
        new(180, 155), new(190, 125), new(195, 105), new(200, 85)
    ], .95, .5);

    private static HandDetection PinchingHand()
    {
        PixelPoint[] points = OpenHand().Landmarks.ToArray();
        points[3] = new(70, 130);
        points[4] = new(83, 110);
        points[6] = new(75, 110);
        points[7] = new(78, 100);
        points[8] = points[4];
        return new(points, .95, .5);
    }

    private static HandSpotlight Create(HandDetection hand, double[] map, double aspect)
    {
        Require(HandSpotlight.TryCreate(hand, map, aspect, out var light), "A valid hand spotlight was rejected.");
        return light!;
    }

    private static PixelPoint Map(PixelPoint point, IReadOnlyList<double> h)
    {
        double divisor = h[6] * point.X + h[7] * point.Y + h[8];
        return new((h[0] * point.X + h[1] * point.Y + h[2]) / divisor,
            (h[3] * point.X + h[4] * point.Y + h[5]) / divisor);
    }

    private static void Reject(HandDetection? hand, IReadOnlyList<double>? map, double aspect, string scenario) =>
        Require(!HandSpotlight.TryCreate(hand, map, aspect, out var light) && light is null,
            $"{scenario} produced a spotlight.");

    private static bool Near(double first, double second) => Math.Abs(first - second) < 1e-10;
    private static double Distance(PixelPoint first, PixelPoint second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
