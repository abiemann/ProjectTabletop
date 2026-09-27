using ProjectTabletop.Vision;

internal static class HandSpotlightSmoothingRegression
{
    public static void Run()
    {
        var elapsed = TimeSpan.FromSeconds(1.0 / 30);
        const double aspect = 16.0 / 9;
        var stationary = new HandSpotlight(new(.5, .45), .12);
        HandSpotlight filtered = stationary;
        var rawCenters = new List<double>();
        var filteredCenters = new List<double>();
        var rawRadii = new List<double>();
        var filteredRadii = new List<double>();
        for (int index = 0; index < 180; index++)
        {
            double sign = index % 2 == 0 ? 1 : -1;
            var observation = new HandSpotlight(new(
                stationary.Center.X + sign * stationary.Radius * .015 / aspect,
                stationary.Center.Y - sign * stationary.Radius * .01),
                stationary.Radius * (1 + sign * .015));
            filtered = HandSpotlightSmoother.Smooth(filtered, observation, elapsed, aspect);
            Covers(filtered, observation, aspect);
            if (index < 30) continue;
            rawCenters.Add(Distance(observation.Center, stationary.Center, aspect));
            filteredCenters.Add(Distance(filtered.Center, stationary.Center, aspect));
            rawRadii.Add(observation.Radius);
            filteredRadii.Add(filtered.Radius);
            Require(filtered.Radius <= stationary.Radius * 1.06,
                "Stationary noise accumulated an unnecessarily large spotlight.");
        }
        Require(Rms(filteredCenters) < Rms(rawCenters) * .4,
            "Steady-hand center noise was not substantially reduced.");
        Require(Range(filteredRadii) < Range(rawRadii) * .4,
            "Steady-hand radius noise still made the circle breathe.");

        // A real hand move must not be mistaken for noise or leave its fingers
        // outside the light while the old center catches up.
        var moved = new HandSpotlight(new(.64, .62), stationary.Radius);
        var followed = HandSpotlightSmoother.Smooth(stationary, moved, elapsed, aspect);
        Require(Distance(followed.Center, moved.Center, aspect) <= moved.Radius * .041,
            "A moving hand outran its spotlight.");
        Require(followed.Radius <= moved.Radius * 1.041,
            "Following a large move flooded a broad area of the board.");
        Covers(followed, moved, aspect);

        // The full fresh circle, not merely its center or a few landmarks, is
        // guaranteed illumination at varied directions, sizes and frame rates.
        foreach (double scale in new[] { .25, 1.0, 2.0 })
        foreach (double seconds in new[] { 1.0 / 60, 1.0 / 30, .10, .30 })
        for (int direction = 0; direction < 12; direction++)
        {
            double angle = direction * Math.PI / 6;
            var before = new HandSpotlight(new(.5, .5), .10 * scale);
            var after = new HandSpotlight(new(.5 + Math.Cos(angle) * .06 * scale / aspect,
                .5 + Math.Sin(angle) * .06 * scale), .12 * scale);
            var light = HandSpotlightSmoother.Smooth(before, after, TimeSpan.FromSeconds(seconds), aspect);
            Covers(light, after, aspect);
            Require(light.Radius <= after.Radius * 1.041,
                "A fresh expansion exceeded the small center-lag coverage allowance.");
        }

        var expanded = new HandSpotlight(stationary.Center, .20);
        var expandedLight = HandSpotlightSmoother.Smooth(stationary, expanded, elapsed, aspect);
        Require(expandedLight.Radius >= expanded.Radius,
            "Opening the fingers left part of the fresh fit dark.");
        var contracted = new HandSpotlight(stationary.Center, .10);
        var contraction = HandSpotlightSmoother.Smooth(expandedLight, contracted, elapsed, aspect);
        Require(contraction.Radius > .17 && contraction.Radius < expandedLight.Radius,
            "Radius contraction either snapped immediately or failed to begin.");
        for (int index = 1; index < 60; index++)
            contraction = HandSpotlightSmoother.Smooth(contraction, contracted, elapsed, aspect);
        Require(contraction.Radius < contracted.Radius * 1.005,
            "Radius padding ratcheted upward instead of settling to the fresh fit.");

        // Camera time, rather than number of callbacks, controls convergence.
        var once = HandSpotlightSmoother.Smooth(expanded, contracted, TimeSpan.FromMilliseconds(300), aspect);
        var subdivided = expanded;
        for (int index = 0; index < 9; index++)
            subdivided = HandSpotlightSmoother.Smooth(subdivided, contracted, elapsed, aspect);
        Require(Math.Abs(once.Radius - subdivided.Radius) < 1e-7,
            "Radius smoothing changed when the same source-time interval was partitioned.");
        var smallShift = stationary with { Center = new(stationary.Center.X + .001 / aspect, stationary.Center.Y) };
        once = HandSpotlightSmoother.Smooth(stationary, smallShift, TimeSpan.FromMilliseconds(300), aspect);
        subdivided = stationary;
        for (int index = 0; index < 9; index++)
            subdivided = HandSpotlightSmoother.Smooth(subdivided, smallShift, elapsed, aspect);
        Require(Distance(once.Center, subdivided.Center, aspect) < 1e-8,
            "Center smoothing depended on callback frequency for a small stable target shift.");

        // Coordinates are normalized independently on each axis, but motion
        // and radius are measured in projector-height units. No stretching.
        foreach (double outputAspect in new[] { .5625, 1.0, 1.7777777777777777, 3.0 })
        {
            var before = new HandSpotlight(new(.4 / outputAspect, .5), .12);
            var after = new HandSpotlight(new(.402 / outputAspect, .501), .118);
            var light = HandSpotlightSmoother.Smooth(before, after, elapsed, outputAspect);
            var squareLight = HandSpotlightSmoother.Smooth(new(new(.4, .5), .12),
                new(new(.402, .501), .118), elapsed, 1);
            Require(Math.Abs(light.Center.X * outputAspect - squareLight.Center.X) < 1e-12 &&
                Math.Abs(light.Center.Y - squareLight.Center.Y) < 1e-12 &&
                Math.Abs(light.Radius - squareLight.Radius) < 1e-12,
                "Changing output aspect stretched the smoothing geometry.");
        }

        foreach (TimeSpan gap in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(-1), TimeSpan.FromMilliseconds(351), TimeSpan.FromSeconds(5) })
            Require(HandSpotlightSmoother.Smooth(stationary, moved, gap, aspect) == moved,
                "Invalid source order or a long gap carried old light geometry into a fresh observation.");
        foreach (double invalidAspect in new[] { 0.0, -1, double.NaN, double.PositiveInfinity })
            Require(HandSpotlightSmoother.Smooth(stationary, moved, elapsed, invalidAspect) == moved,
                "Invalid aspect did not reset to the current fit.");
        foreach (HandSpotlight invalid in new[]
        {
            stationary with { Center = new(double.NaN, .5) },
            stationary with { Center = new(.5, double.PositiveInfinity) },
            stationary with { Radius = 0 }, stationary with { Radius = -1 },
            stationary with { Radius = double.NaN }, stationary with { Radius = double.PositiveInfinity }
        })
            Require(HandSpotlightSmoother.Smooth(invalid, moved, elapsed, aspect) == moved,
                "Invalid history contaminated the fresh spotlight.");
        Require(HandSpotlightSmoother.Smooth(null!, moved, elapsed, aspect) == moved,
            "Missing history did not initialize from the fresh fit.");

        Console.WriteLine("Hand spotlight smoothing regression: stationary center/radius jitter, bounded motion lag, " +
            "full fresh-circle coverage, prompt expansion and smooth contraction, source-time invariance, " +
            "projector aspect, gap/order and invalid-history resets passed.");
    }

    private static void Covers(HandSpotlight light, HandSpotlight raw, double aspect)
    {
        Require(light.Radius + 1e-12 >= raw.Radius + Distance(light.Center, raw.Center, aspect),
            "Smoothing cut into the fresh fitted circle's finger coverage margin.");
    }

    private static double Distance(PixelPoint first, PixelPoint second, double aspect) =>
        Math.Sqrt(Math.Pow((first.X - second.X) * aspect, 2) + Math.Pow(first.Y - second.Y, 2));
    private static double Rms(IEnumerable<double> values) => Math.Sqrt(values.Average(value => value * value));
    private static double Range(IEnumerable<double> values) => values.Max() - values.Min();
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
