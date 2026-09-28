using ProjectTabletop.Calibration;

internal static class BoardOrientationRegression
{
    public static void Run()
    {
        const double aspect = 16.0 / 9;
        Point2[] unit = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        Point2[] physical = [new(.14, .17), new(.86, .12), new(.91, .88), new(.09, .92)];
        Point2[] cameraField = [new(210, 130), new(1650, 205), new(1590, 1010), new(310, 970)];
        var referenceSpots = Enumerable.Range(0, BoardRegistration.SpotCount)
            .Select(BoardRegistration.SpotPosition).ToArray();

        foreach (int viewingSide in new[] { 0, 1, 2, 3 })
        {
            var expected = Shift(physical, viewingSide);
            double heading = BoardOrientation.Heading(expected, aspect);
            var expectedSurface = Homography.FromFourPoints(unit, expected);
            // The real five-spot fit must keep both visuals and their hit coordinates stable
            // when camera roll changes the detector's top-left corner or perimeter winding.
            foreach (double cameraRoll in new[] { 0, 17, 44, 46, 90, 137, 180, 225, 270, 319 })
            {
                double radians = cameraRoll * Math.PI / 180;
                Point2 Rotate(Point2 point) => new(
                    960 + (point.X - 960) * Math.Cos(radians) - (point.Y - 540) * Math.Sin(radians),
                    540 + (point.X - 960) * Math.Sin(radians) + (point.Y - 540) * Math.Cos(radians));
                var projectorToCamera = Homography.FromFourPoints(unit, cameraField.Select(Rotate).ToArray());
                var fitted = BoardRegistration.FitAndValidate(referenceSpots.Select(projectorToCamera.Transform).ToArray(), out _);
                var cameraBoard = CameraCornerOrder(physical.Select(projectorToCamera.Transform).ToArray());
                foreach (bool reverse in new[] { false, true })
                    for (int first = 0; first < 4; first++)
                    {
                        var ordered = Shift(reverse ? cameraBoard.Reverse().ToArray() : cameraBoard, first);
                        var mapped = ordered.Select(fitted.Transform).ToArray();
                        var oriented = BoardOrientation.Orient(mapped, heading, aspect);
                        Match(oriented, expected);
                        Require(mapped.All(oriented.Contains), "Orientation changed physical corner coordinates.");
                        var actualSurface = Homography.FromFourPoints(unit, oriented);
                        foreach (var hit in new Point2[] { new(.18, .11), new(.5, .86), new(.83, .9), new(.42, .57) })
                        {
                            var camera = projectorToCamera.Transform(expectedSurface.Transform(hit));
                            Close(actualSurface.Inverse().Transform(fitted.Transform(camera)), hit);
                        }
                    }
            }
        }

        Point2[] rectangle = [new(.1, .2), new(.9, .2), new(.9, .8), new(.1, .8)];
        Match(BoardOrientation.Orient(rectangle, 180, aspect), Shift(rectangle, 2));
        Close(BoardOrientation.Heading(Shift(rectangle, 2), aspect), 180);
        Close(BoardOrientation.Heading([rectangle[2], rectangle[1], rectangle[0], rectangle[3]], aspect), 180);
        foreach (double heading in new[] { -180, 540, -540 })
            Match(BoardOrientation.Orient(rectangle, heading, aspect), Shift(rectangle, 2));

        // Equal-angle choices must not flip as the camera's first corner changes.
        foreach (double diagonal in new[] { 45, 135, 225, 315 })
        {
            var expected = BoardOrientation.Orient(unit, diagonal, 1);
            foreach (bool reverse in new[] { false, true })
                for (int first = 0; first < 4; first++)
                    Match(BoardOrientation.Orient(Shift(reverse ? unit.Reverse().ToArray() : unit, first), diagonal, 1), expected);
        }

        Point2[] diamond = [new(.2, .3), new(.5, .6), new(.4, .9), new(.1, .6)];
        Close(BoardOrientation.Heading(diamond, 2), Math.Atan2(.3, .6) * 180 / Math.PI);
        var moved = diamond.Select(point => new Point2(point.X * 2 + 6, point.Y * 2 - 4)).ToArray();
        Close(BoardOrientation.Heading(moved, 2), BoardOrientation.Heading(diamond, 2));
        Match(BoardOrientation.Orient(Shift(moved, 3), BoardOrientation.Heading(diamond, 2), 2), moved);

        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Reject(() => BoardOrientation.Orient(unit, invalid, aspect));
        foreach (double invalid in new[] { 0, -1, double.NaN, double.PositiveInfinity })
        {
            Reject(() => BoardOrientation.Orient(unit, 0, invalid));
            Reject(() => BoardOrientation.Heading(unit, invalid));
        }
        Point2[][] invalidCorners = [[], unit[..3], [.. unit, new(.5, .5)],
            [unit[0], unit[1], unit[1], unit[3]],
            [unit[0], unit[2], unit[1], unit[3]],
            [new(0, 0), new(1, 0), new(.2, .2), new(0, 1)],
            [new(0, 0), new(1, 0), new(2, 0), new(3, 0)],
            [new(double.NaN, 0), unit[1], unit[2], unit[3]]];
        foreach (var invalid in invalidCorners)
        {
            Reject(() => BoardOrientation.Orient(invalid, 0, aspect));
            Reject(() => BoardOrientation.Heading(invalid, aspect));
        }
        Reject(() => BoardOrientation.Orient(null!, 0, aspect));
        Reject(() => BoardOrientation.Heading(null!, aspect));
    }

    private static Point2[] CameraCornerOrder(Point2[] points)
    {
        var byY = points.OrderBy(point => point.Y).ToArray();
        var upper = byY[..2].OrderBy(point => point.X).ToArray();
        var lower = byY[2..].OrderBy(point => point.X).ToArray();
        return [upper[0], upper[1], lower[1], lower[0]];
    }

    private static Point2[] Shift(Point2[] points, int first) =>
        Enumerable.Range(0, 4).Select(index => points[(first + index) % 4]).ToArray();

    private static void Match(Point2[] actual, Point2[] expected)
    {
        Require(actual.Length == expected.Length, "Corner count changed.");
        for (int index = 0; index < actual.Length; index++) Close(actual[index], expected[index]);
    }

    private static void Close(Point2 actual, Point2 expected)
    {
        Close(actual.X, expected.X);
        Close(actual.Y, expected.Y);
    }

    private static void Close(double actual, double expected) =>
        Require(double.IsFinite(actual) && Math.Abs(actual - expected) < 1e-8,
            $"Board orientation mismatch: {actual} instead of {expected}.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new Exception("Invalid board orientation input was accepted.");
    }
}
