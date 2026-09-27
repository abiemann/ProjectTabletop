using ProjectTabletop.Calibration;

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

Console.WriteLine("Calibration verification passed.");
