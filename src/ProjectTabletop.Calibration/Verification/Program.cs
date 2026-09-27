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

// The camera sees this board upside down and the lens bends points away from a
// single homography. Spots near the board edges should reduce extrapolation error.
static Point2 DistortedCamera(Point2 projector)
{
    var dx = projector.X - 0.5;
    var dy = projector.Y - 0.5;
    return new Point2(1700 - 1450 * projector.X + 160 * dx * dx * dx,
                      950 - 850 * projector.Y + 95 * dy * dy * dy);
}
Point2[] centralProjector = [new(0.32, 0.32), new(0.68, 0.32),
                             new(0.68, 0.68), new(0.32, 0.68)];
Point2[] physicalProjector = [new(0.9, 0.9), new(0.1, 0.9),
                              new(0.1, 0.1), new(0.9, 0.1)];
Point2[] physicalCamera = physicalProjector.Select(DistortedCamera).ToArray();
var provisionalRegistration = Homography.FromFourPoints(
    centralProjector.Select(DistortedCamera).ToArray(), centralProjector);
var nearTargets = NearEdgeRegistrationPlan.Create(physicalCamera, provisionalRegistration);
if (nearTargets.Length != 4 || nearTargets.Any(target =>
        target.ProjectorPosition.X is < 0.05 or > 0.95 ||
        target.ProjectorPosition.Y is < 0.05 or > 0.95))
    throw new Exception("Near-edge targets left the safe projector field.");
Point2[] boardUv = [new(0.15, 0.15), new(0.85, 0.15),
                    new(0.85, 0.85), new(0.15, 0.85)];
var cameraToBoard = Homography.FromFourPoints(physicalCamera,
    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]);
for (var index = 0; index < 4; index++)
    Close(cameraToBoard.Transform(nearTargets[index].CameraEstimate), boardUv[index]);
var nearRegistration = Homography.FromFourPoints(
    nearTargets.Select(target => DistortedCamera(target.ProjectorPosition)).ToArray(),
    nearTargets.Select(target => target.ProjectorPosition).ToArray());
static double MaxCornerError(Homography mapping, Point2[] camera, Point2[] projector) =>
    Enumerable.Range(0, 4).Max(index =>
    {
        var actual = mapping.Transform(camera[index]);
        var expected = projector[index];
        return Math.Sqrt(Math.Pow(actual.X - expected.X, 2) +
                         Math.Pow(actual.Y - expected.Y, 2));
    });
var provisionalError = MaxCornerError(provisionalRegistration, physicalCamera, physicalProjector);
var nearError = MaxCornerError(nearRegistration, physicalCamera, physicalProjector);
if (provisionalError < 0.006 || nearError >= provisionalError * 0.75)
    throw new Exception($"Near-edge registration failed to reduce synthetic corner error " +
                        $"({provisionalError:F4} -> {nearError:F4}).");

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
