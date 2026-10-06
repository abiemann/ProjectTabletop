using ProjectTabletop.Calibration;

// Homography, clipping, five-spot registration and session persistence checks.
internal static class CalibrationCoreRegression
{
    public static async Task RunAsync()
    {
        static void Close(Point2 actual, Point2 expected, double tolerance = 1e-7)
        {
            if (Math.Abs(actual.X - expected.X) > tolerance || Math.Abs(actual.Y - expected.Y) > tolerance)
                throw new Exception($"Expected {expected}; got {actual}.");
        }

        static void Reject(Action action)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            throw new Exception("Expected invalid calibration points to be rejected.");
        }

        Point2[] source = [new(100, 100), new(1100, 100), new(1100, 900), new(100, 900)];
        Point2[] destination = [new(500, 50), new(2500, 50), new(2500, 1650), new(500, 1650)];
        var affine = Homography.FromFourPoints(source, destination);
        for (var index = 0; index < 4; index++) Close(affine.Transform(source[index]), destination[index]);
        Close(affine.Transform(new Point2(600, 500)), new Point2(1500, 850));
        Close(affine.InverseTransform(new Point2(1500, 850)), new Point2(600, 500));

        // The fourth destination corner gives this mapping perspective, rather than a simple affine scale.
        Point2[] trapezoid = [new(500, 50), new(2500, 250), new(2200, 1650), new(750, 1550)];
        var projective = Homography.FromFourPoints(source, trapezoid);
        for (var index = 0; index < 4; index++) Close(projective.Transform(source[index]), trapezoid[index]);
        var interior = new Point2(723.5, 411.25);
        Close(projective.InverseTransform(projective.Transform(interior)), interior);
        Close(projective.Inverse().Transform(projective.Transform(interior)), interior);

        Reject(() => Homography.FromFourPoints(source, [source[0], source[1], source[1], source[3]]));
        Reject(() => Homography.FromFourPoints(source, [new(0, 0), new(100, 100), new(0, 100), new(100, 0)]));
        Reject(() => Homography.FromFourPoints(source, [new(0, 0), new(100, 0), new(100, 100), new(double.NaN, 100)]));

        // The board clip region is the single geometry used to bound all projected media.
        // Check its perspective edges, the inclusive boundary, and a caller-mutated input.
        Point2[] insetCorners = [new(0.12, 0.18), new(0.88, 0.12),
                                 new(0.81, 0.84), new(0.19, 0.78)];
        var clip = ProjectionClipRegion.FromCorners(insetCorners);
        insetCorners[0] = new Point2(0, 0);
        Close(clip.Corners[0], new Point2(0.12, 0.18));
        Close(new Point2(clip.MinX, clip.MinY), new Point2(0.12, 0.12));
        Close(new Point2(clip.MaxX, clip.MaxY), new Point2(0.88, 0.84));
        if (!clip.Contains(new Point2(0.5, 0.5)) ||
            !clip.Contains(clip.Corners[0]) ||
            !clip.Contains(new Point2(0.5, 0.15)) ||
            // These points are inside the axis-aligned bounds but outside the
            // slanted board edges; a rectangular crop would wrongly light them.
            clip.Contains(new Point2(0.5, 0.13)) ||
            clip.Contains(new Point2(0.87, 0.5)) ||
            clip.Contains(new Point2(0.5, 0.83)) ||
            clip.Contains(new Point2(0.13, 0.5)) ||
            clip.Contains(new Point2(double.NaN, 0.5)))
            throw new Exception("Perspective board clipping accepted exterior media or rejected board media.");
        Reject(() => ProjectionClipRegion.FromCorners([new(0, 0), new(1, 0), new(1, 1)]));
        Reject(() => ProjectionClipRegion.FromCorners([new(0, 0), new(1, 0), new(1, 1), new(1.01, 1)]));
        Reject(() => ProjectionClipRegion.FromCorners([new(0, 0), new(1, 0), new(1, 1), new(double.NaN, 1)]));
        Reject(() => ProjectionClipRegion.FromCorners([new(0, 0), new(0, 1), new(1, 1), new(1, 0)]));
        Reject(() => ProjectionClipRegion.FromCorners([new(0, 0), new(1, 0), new(0.3, 0.3), new(0, 1)]));
        Reject(() => ProjectionClipRegion.FromCorners([new(0, 0), new(1, 0), new(1, 0), new(0, 1)]));

        // Registration projects four ordered alignment spots, then independently checks
        // the center. Observed spot order, rather than webcam corner order, determines
        // orientation, including a camera that sees the board upside down.
        Point2[] registrationTargets = [new(0.32, 0.32), new(0.68, 0.32),
            new(0.68, 0.68), new(0.32, 0.68), new(0.5, 0.5)];
        if (BoardRegistration.SpotCount != registrationTargets.Length ||
            BoardRegistration.MaximumCenterError != 0.015)
            throw new Exception("Board setup must use five spots and retain its center-check tolerance.");
        for (var index = 0; index < registrationTargets.Length; index++)
            Close(BoardRegistration.SpotPosition(index), registrationTargets[index]);
        Reject(() => BoardRegistration.SpotPosition(-1));
        Reject(() => BoardRegistration.SpotPosition(BoardRegistration.SpotCount));

        Point2[] unitCorners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        Point2[][] cameraViews = [
            unitCorners,
            [new(1600, 900), new(200, 900), new(200, 100), new(1600, 100)],
            [new(220, 120), new(1500, 230), new(1390, 1000), new(400, 870)]
        ];
        foreach (var cameraCorners in cameraViews)
        {
            var projectorToCamera = Homography.FromFourPoints(unitCorners, cameraCorners);
            var observedSpots = registrationTargets.Select(projectorToCamera.Transform).ToArray();
            var registration = BoardRegistration.FitAndValidate(observedSpots, out var centerError);
            if (centerError > 1e-10)
                throw new Exception($"An exact five-spot calibration has center error {centerError}.");
            foreach (var target in registrationTargets.Concat(
                         [new Point2(0.1, 0.15), new Point2(0.9, 0.85)]))
                Close(registration.Transform(projectorToCamera.Transform(target)), target);
        }

        // A slightly noisy center may pass validation, but must not move the mapping
        // already determined by the first four measurements.
        var noisyCenterSpots = registrationTargets.ToArray();
        noisyCenterSpots[4] = new(0.51, 0.508);
        var validatedRegistration = BoardRegistration.FitAndValidate(noisyCenterSpots,
            out var acceptedCenterError);
        if (Math.Abs(acceptedCenterError - Math.Sqrt(0.01 * 0.01 + 0.008 * 0.008)) > 1e-10)
            throw new Exception("Center validation did not report the independent measurement error.");
        Close(validatedRegistration.Transform(new Point2(0.1, 0.9)), new Point2(0.1, 0.9));

        var wrongCenterSpots = registrationTargets.ToArray();
        wrongCenterSpots[4] = new(0.516, 0.5);
        var rejectedCenter = false;
        try { BoardRegistration.FitAndValidate(wrongCenterSpots, out _); }
        catch (InvalidOperationException) { rejectedCenter = true; }
        if (!rejectedCenter)
            throw new Exception("A fifth spot outside the center tolerance was accepted.");

        Reject(() => BoardRegistration.FitAndValidate(registrationTargets[..4], out _));
        Reject(() => BoardRegistration.FitAndValidate(
            [.. registrationTargets, new Point2(0.5, 0.5)], out _));
        Reject(() => BoardRegistration.FitAndValidate(null!, out _));
        Reject(() => BoardRegistration.FitAndValidate(
            [registrationTargets[0], registrationTargets[1], registrationTargets[1],
             registrationTargets[3], registrationTargets[4]], out _));
        Reject(() => BoardRegistration.FitAndValidate(
            [registrationTargets[0], registrationTargets[2], registrationTargets[1],
             registrationTargets[3], registrationTargets[4]], out _));
        Reject(() => BoardRegistration.FitAndValidate(
            [.. registrationTargets[..4], new Point2(double.NaN, 0.5)], out _));

        var board = new PlaneCalibration(source, destination);
        var pieceTop = new PlaneCalibration(source, trapezoid);
        var session = new CalibrationSession("camera", 1920, 1080, "projector", 3840, 2160,
            board, pieceTop, 5);
        Close(session.MapCameraToProjector(interior, CalibrationPlane.PieceTop), projective.Transform(interior));
        var file = Path.Combine(Path.GetTempPath(), $"projecttabletop-calibration-{Guid.NewGuid():N}.json");
        try
        {
            await CalibrationSessionStore.SaveAsync(file, session);
            var loaded = await CalibrationSessionStore.LoadAsync(file);
            if (!loaded.MatchesHardware("camera", 1920, 1080, "projector", 3840, 2160) ||
                loaded.PieceTopHeightMillimeters != 5)
                throw new Exception("Session metadata did not survive persistence.");
            Close(loaded.MapCameraToProjector(interior, CalibrationPlane.Board), affine.Transform(interior));
            Close(loaded.MapCameraToProjector(interior, CalibrationPlane.PieceTop), projective.Transform(interior));
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}
