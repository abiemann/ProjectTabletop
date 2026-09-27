using ProjectTabletop.Calibration;

internal static class BoardSizeEstimateRegression
{
    public static void Run()
    {
        const double height = 123, ratio = 1.2, aspect = 16.0 / 9;
        Point2[] fullImage = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        Check(BoardSizeEstimate.FromProjectorCorners(fullImage, height, ratio, aspect), 57.65625, 102.5);
        Point2[] inset = [new(.2, .15), new(.8, .15), new(.8, .85), new(.2, .85)];
        Check(BoardSizeEstimate.FromProjectorCorners(inset, height, ratio, aspect), 40.359375, 61.5);
        Check(BoardSizeEstimate.FromProjectorCorners(inset, height * 2, ratio, aspect), 80.71875, 123);
        Check(BoardSizeEstimate.FromImageDimensions(inset, 102.5, 57.65625), 40.359375, 61.5);

        // Measured full-image dimensions work independently of projector model or native
        // aspect: 4:3, 16:10, 16:9, portrait, and a custom physical image size are all valid.
        (double Width, double Height)[] imageSizes = [(120, 90), (128, 80), (160, 90), (75, 125), (103.7, 71.3)];
        foreach (var image in imageSizes)
        {
            Check(BoardSizeEstimate.FromImageDimensions(fullImage, image.Width, image.Height),
                Math.Min(image.Width, image.Height), Math.Max(image.Width, image.Height));
            Check(BoardSizeEstimate.FromImageDimensions(inset, image.Width, image.Height),
                Math.Min(image.Width * .6, image.Height * .7), Math.Max(image.Width * .6, image.Height * .7));
            foreach (double degrees in new[] { 0, 17, 45, 90, 137, 270 })
            {
                double angle = degrees * Math.PI / 180;
                Point2[] corners = [new(-20, -12), new(20, -12), new(20, 12), new(-20, 12)];
                var normalized = corners.Select(point => new Point2(
                    .5 + (point.X * Math.Cos(angle) - point.Y * Math.Sin(angle)) / image.Width,
                    .5 + (point.X * Math.Sin(angle) + point.Y * Math.Cos(angle)) / image.Height)).ToArray();
                Check(BoardSizeEstimate.FromImageDimensions(normalized, image.Width, image.Height), 24, 40);
                Check(BoardSizeEstimate.FromImageDimensions(normalized.Reverse().ToArray(), image.Width, image.Height), 24, 40);
                Check(BoardSizeEstimate.FromImageDimensions([normalized[2], normalized[3], normalized[0], normalized[1]],
                    image.Width, image.Height), 24, 40);
            }
        }

        // Rotate in physical centimeters before normalizing each image axis. Rotating
        // directly in square UV would silently give the wrong result for a 16:9 projector.
        foreach (double degrees in new[] { 0, 17, 45, 90, 137, 270 })
        {
            double angle = degrees * Math.PI / 180;
            Point2[] corners = [new(-20, -12), new(20, -12), new(20, 12), new(-20, 12)];
            var normalized = corners.Select(point => new Point2(
                .5 + (point.X * Math.Cos(angle) - point.Y * Math.Sin(angle)) / 102.5,
                .5 + (point.X * Math.Sin(angle) + point.Y * Math.Cos(angle)) / 57.65625)).ToArray();
            Check(BoardSizeEstimate.FromProjectorCorners(normalized, height, ratio, aspect), 24, 40);
            Check(BoardSizeEstimate.FromProjectorCorners(normalized.Reverse().ToArray(), height, ratio, aspect), 24, 40);
            Check(BoardSizeEstimate.FromProjectorCorners([normalized[2], normalized[3], normalized[0], normalized[1]],
                height, ratio, aspect), 24, 40);
        }

        // Neither the named projector's optics nor its aspect are baked into the helper.
        Check(BoardSizeEstimate.FromProjectorCorners(
            [new(.1, .2), new(.9, .2), new(.9, .8), new(.1, .8)], 150, 1.5, 1.5), 40, 80);
        Check(BoardSizeEstimate.FromProjectorCorners(
            [new(0, 0), new(1, 0), new(.9, 1), new(.1, 1)], 100, 1, 1), 90, Math.Sqrt(10100));
        Check(BoardSizeEstimate.FromProjectorCorners(
            [new(-.005, -.002), new(1.005, -.002), new(1.005, 1.002), new(-.005, 1.002)],
            height, ratio, aspect), 57.886875, 103.525);

        foreach (double invalid in new[] { 0, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Reject(() => BoardSizeEstimate.FromProjectorCorners(fullImage, invalid, ratio, aspect));
            Reject(() => BoardSizeEstimate.FromProjectorCorners(fullImage, height, invalid, aspect));
            Reject(() => BoardSizeEstimate.FromProjectorCorners(fullImage, height, ratio, invalid));
            Reject(() => BoardSizeEstimate.FromImageDimensions(fullImage, invalid, 80));
            Reject(() => BoardSizeEstimate.FromImageDimensions(fullImage, 120, invalid));
        }
        Reject(() => BoardSizeEstimate.FromProjectorCorners(null!, height, ratio, aspect));
        Reject(() => BoardSizeEstimate.FromImageDimensions(null!, 120, 80));
        Point2[][] invalidCorners =
        [
            fullImage.Take(3).ToArray(),
            [new(0, 0), new(1, 0), new(1, 0), new(0, 1)],
            [new(0, 0), new(1, 1), new(0, 1), new(1, 0)],
            [new(0, 0), new(1, 0), new(.2, .2), new(0, 1)],
            [new(0, 0), new(1, 0), new(2, 0), new(0, 1)],
            [new(0, 0), new(1, 0), new(2, 1e-12), new(0, 1)],
            [new(0, 0), new(1, 0), new(1, 1), new(double.NaN, 1)],
            [new(0, 0), new(1, 0), new(1, 1), new(0, double.PositiveInfinity)],
            [new(0, 0), new(0, 0), new(0, 0), new(0, 0)]
        ];
        foreach (var corners in invalidCorners)
        {
            Reject(() => BoardSizeEstimate.FromProjectorCorners(corners, height, ratio, aspect));
            Reject(() => BoardSizeEstimate.FromImageDimensions(corners, 120, 80));
        }
        Reject(() => BoardSizeEstimate.FromProjectorCorners(fullImage, double.MaxValue, .1, aspect));
        Reject(() => BoardSizeEstimate.FromProjectorCorners(fullImage, double.Epsilon, double.MaxValue, aspect));
        Reject(() => BoardSizeEstimate.FromImageDimensions(fullImage, double.MaxValue, 80));
        Reject(() => BoardSizeEstimate.FromImageDimensions(fullImage, 120, double.Epsilon));
        Console.WriteLine("Board size estimate verification passed: optical and measured-image scaling, landscape/portrait/custom " +
            "aspects, physical rotation, perimeter winding, opposite-edge averaging, edge extrapolation and invalid inputs.");
    }

    private static void Check(BoardSizeEstimate actual, double shortSide, double longSide)
    {
        if (!double.IsFinite(actual.ShortSideCentimeters) || !double.IsFinite(actual.LongSideCentimeters) ||
            Math.Abs(actual.ShortSideCentimeters - shortSide) > 1e-8 ||
            Math.Abs(actual.LongSideCentimeters - longSide) > 1e-8)
            throw new InvalidOperationException($"Expected board estimate {shortSide} × {longSide}; got {actual}.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Invalid board size estimation input was accepted.");
    }
}
