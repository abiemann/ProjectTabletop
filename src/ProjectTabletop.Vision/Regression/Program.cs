using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

const int width = 512, height = 384;
PixelPoint[] localOutline =
[
    new(-34, -19), new(15, -19), new(39, 0),
    new(15, 19), new(-34, 19), new(-23, 0)
];

var settings = new VisionSettings
{
    SegmentationMode = SegmentationMode.Brightness,
    BrightnessThreshold = 100,
    PatternBrightnessThreshold = 240,
    MinimumAreaPixels = 400,
    MaximumAreaFraction = 0.06,
    MorphologyKernelSize = 3,
    UnknownDistanceThreshold = 7.0,
    MinimumClassMargin = 0.20,
    PatternPoseWeight = 0.35
};

using var engine = new VisionEngine(settings);
for (int i = 0; i < 5; i++)
{
    Enroll("A", new PixelPoint(120 + i * 13, 112 + i * 8), i * 36);
    Enroll("B", new PixelPoint(308 - i * 9, 128 + i * 7), i * 36 + 12);
}

TrainingReport training = engine.Train();
if (training.PieceCount != 2 || training.CaptureCount != 10)
    throw new Exception("Unexpected training set size.");

string profileDir = Path.Combine(Path.GetTempPath(), "ProjectTabletop.Vision.Regression." + Guid.NewGuid().ToString("N"));
engine.Save(profileDir);
if (Directory.GetFiles(Path.Combine(profileDir, "captures"), "*.png").Length != 10)
    throw new Exception("Full original capture PNG files were not saved.");
if (!File.Exists(Path.Combine(profileDir, "vision-svm.yml")) ||
    !File.ReadAllText(Path.Combine(profileDir, "vision-profile.json")).Contains("PngFileName"))
    throw new Exception("Model or capture annotation metadata was not saved.");
using var loaded = VisionEngine.Load(profileDir);

using var evaluationImage = NewFrame();
GroundTruthPiece truthA = DrawPiece(evaluationImage, "A", new PixelPoint(138, 203), 47);
GroundTruthPiece truthB = DrawPiece(evaluationImage, "B", new PixelPoint(369, 154), 133);
DrawPiece(evaluationImage, "unknown", new PixelPoint(267, 303), 21);
var evaluation = new EvaluationFrame("two-pieces-plus-unknown", width, height, width * 4,
    Bytes(evaluationImage), [truthA, truthB]);
EvaluationReport result = loaded.Evaluate([evaluation]);
Console.WriteLine($"Trained classes: {training.PieceCount}; captures: {training.CaptureCount}; " +
    $"leave-one-out identity accuracy: {training.LeaveOneOutIdentityAccuracy:P1}");
Console.WriteLine($"Evaluation: expected={result.ExpectedPieceCount}, found={result.FoundPieceCount}, " +
    $"correct={result.CorrectIdentityCount}, false positives={result.FalsePositiveCount}, " +
    $"center error={result.MeanCenterErrorPixels:F2}px, angle error={result.MeanAngleErrorDegrees:F2}°, " +
    $"outline IoU={result.MeanOutlineIntersectionOverUnion:F3}");
foreach (EvaluationCaseResult item in result.Cases)
    Console.WriteLine($"{item.PieceId}: found={item.Found}, identity={item.CorrectIdentity}, " +
        $"center={item.CenterErrorPixels:F2}px, angle={item.AngleErrorDegrees:F2}°, IoU={item.OutlineIntersectionOverUnion:F3}");

if (result.ExpectedPieceCount != 2 || result.CorrectIdentityCount != 2 ||
    result.FalsePositiveCount != 0 || result.MeanCenterErrorPixels > 5 ||
    result.MeanAngleErrorDegrees > 10 || result.MeanOutlineIntersectionOverUnion < 0.8)
    throw new Exception("Synthetic multi-piece regression failed.");

CheckBoardDetection();
CheckProjectorFieldIsNotBoard();
CheckUniformIllumination();
BoardDetection whiteHardwareBoard = CheckHardwareWhiteScan();
CheckHardwareAmbientScan(whiteHardwareBoard);
CheckCalibrationSpot();
if (args.Length == 1)
{
    using Mat actual = Cv2.ImRead(args[0], ImreadModes.Unchanged);
    using Mat bgra = new();
    Cv2.CvtColor(actual, bgra, ColorConversionCodes.BGR2BGRA);
    BoardDetection? observed = BoardDetector.Detect(bgra.Width, bgra.Height,
        (int)bgra.Step(), BytesOf(bgra));
    Console.WriteLine($"Captured board: {(observed is null ? "not found" :
        string.Join(", ", observed.Corners.Select(p => $"({p.X:F0},{p.Y:F0})")))}");
    BoardDetection? field = BoardDetector.DetectProjectedField(bgra.Width, bgra.Height,
        (int)bgra.Step(), BytesOf(bgra));
    BoardDetection? scanned = BoardDetector.DetectUniformIllumination(bgra.Width, bgra.Height,
        (int)bgra.Step(), BytesOf(bgra));
    Console.WriteLine($"Uniform scan field: {Format(field)}; cardboard: {Format(scanned)}");
}

string Format(BoardDetection? detection) => detection is null ? "not found" :
    string.Join(", ", detection.Corners.Select(p => $"({p.X:F0},{p.Y:F0})"));

void CheckUniformIllumination()
{
    const int sceneWidth = 960, sceneHeight = 540;
    using Mat image = new(sceneHeight, sceneWidth, MatType.CV_8UC4,
        new Scalar(20, 20, 20, 255));
    Point[] field = [new(100, 38), new(872, 42), new(880, 500), new(95, 496)];
    Point[] board = [new(205, 152), new(804, 163), new(791, 386), new(194, 377)];
    Cv2.FillConvexPoly(image, field, new Scalar(170, 80, 35, 255));
    Cv2.FillConvexPoly(image, board, new Scalar(214, 211, 207, 255));
    BoardDetection? fieldFound = BoardDetector.DetectProjectedField(sceneWidth, sceneHeight,
        sceneWidth * 4, BytesOf(image));
    BoardDetection? boardFound = BoardDetector.DetectUniformIllumination(sceneWidth, sceneHeight,
        sceneWidth * 4, BytesOf(image));
    if (!Near(fieldFound, field, 18) || !Near(boardFound, board, 18))
        throw new Exception($"White-scan corners failed: field={Format(fieldFound)}, board={Format(boardFound)}");

    using Mat noBoard = new(sceneHeight, sceneWidth, MatType.CV_8UC4,
        new Scalar(20, 20, 20, 255));
    Cv2.FillConvexPoly(noBoard, field, new Scalar(170, 80, 35, 255));
    if (BoardDetector.DetectUniformIllumination(sceneWidth, sceneHeight,
        sceneWidth * 4, BytesOf(noBoard)) is not null)
        throw new Exception("Uniform projector field alone was mislabeled as cardboard.");

    using Mat darkBoard = new(sceneHeight, sceneWidth, MatType.CV_8UC4,
        new Scalar(20, 20, 20, 255));
    Cv2.FillConvexPoly(darkBoard, field, new Scalar(205, 205, 205, 255));
    Cv2.FillConvexPoly(darkBoard, board, new Scalar(81, 78, 75, 255));
    if (!Near(BoardDetector.DetectUniformIllumination(sceneWidth, sceneHeight,
        sceneWidth * 4, BytesOf(darkBoard)), board, 18))
        throw new Exception("Darker cardboard under uniform illumination was missed.");
    Console.WriteLine("White-scan regression: independent wide cardboard and light-field quads, " +
        "bright and dark cardboard; field-only rejected");
}

BoardDetection CheckHardwareWhiteScan()
{
    string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "white-cardboard-full-field.png");
    using Mat original = Cv2.ImRead(path, ImreadModes.Color);
    if (original.Empty()) throw new Exception($"Missing white-scan hardware regression frame: {path}");
    using Mat camera = new();
    Cv2.CvtColor(original, camera, ColorConversionCodes.BGR2BGRA);
    byte[] pixels = BytesOf(camera);
    BoardDetection? field = BoardDetector.DetectProjectedField(camera.Width, camera.Height,
        (int)camera.Step(), pixels);
    BoardDetection? board = BoardDetector.DetectUniformIllumination(camera.Width, camera.Height,
        (int)camera.Step(), pixels);
    Point[] expectedField = [new(368, 106), new(1879, 84), new(1895, 934), new(378, 956)];
    Point[] expectedBoard = [new(624, 106), new(1690, 93), new(1707, 931), new(632, 949)];
    if (!Near(field, expectedField, 16) || !Near(board, expectedBoard, 16))
        throw new Exception($"Physical cardboard hardware regression failed: " +
            $"field={Format(field)}, cardboard={Format(board)}");
    Console.WriteLine($"Hardware white-scan regression: field={Format(field)}; " +
        $"cardboard={Format(board)}");
    return board!;
}

void CheckHardwareAmbientScan(BoardDetection whiteBoard)
{
    string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ambient-cardboard-black-output.png");
    using Mat original = Cv2.ImRead(path, ImreadModes.Color);
    if (original.Empty()) throw new Exception($"Missing ambient cardboard regression frame: {path}");
    using Mat camera = new();
    Cv2.CvtColor(original, camera, ColorConversionCodes.BGR2BGRA);
    BoardDetection? ambient = BoardDetector.DetectAmbientBoard(camera.Width, camera.Height,
        (int)camera.Step(), BytesOf(camera));
    Point[] expectedBoard = [new(624, 106), new(1690, 93), new(1707, 931), new(632, 949)];
    if (!Near(ambient, expectedBoard, 16) || ambient is null ||
        Enumerable.Range(0, 4).Any(i =>
            Math.Sqrt(Math.Pow(ambient.Corners[i].X - whiteBoard.Corners[i].X, 2) +
                Math.Pow(ambient.Corners[i].Y - whiteBoard.Corners[i].Y, 2)) > 14))
        throw new Exception($"Black-output ambient board did not match white-scan board: {Format(ambient)}");
    Console.WriteLine($"Hardware ambient regression: cardboard={Format(ambient)}; " +
        "all corners agree with white scan within 14 px");
}

void CheckCalibrationSpot()
{
    const int sceneWidth = 960, sceneHeight = 540;
    using Mat white = new(sceneHeight, sceneWidth, MatType.CV_8UC4,
        new Scalar(225, 218, 208, 255));
    using Mat spot = white.Clone();
    Point center = new(638, 197);
    Cv2.Circle(spot, center, 22, new Scalar(45, 43, 40, 255), -1);
    CalibrationSpotDetection? found = BoardDetector.DetectDarkCalibrationSpot(
        sceneWidth, sceneHeight, sceneWidth * 4, BytesOf(white), BytesOf(spot));
    if (found is null || Math.Abs(found.Center.X - center.X) > 4 ||
        Math.Abs(found.Center.Y - center.Y) > 4 || found.RadiusPixels < 12)
        throw new Exception($"Dark calibration spot failed: {found}");
    if (BoardDetector.DetectDarkCalibrationSpot(sceneWidth, sceneHeight, sceneWidth * 4,
        BytesOf(white), BytesOf(white)) is not null)
        throw new Exception("Unchanged frames produced a false calibration spot.");
    using Mat shiftedExposure = spot.Clone();
    shiftedExposure.ConvertTo(shiftedExposure, MatType.CV_8UC4, 1, -12);
    CalibrationSpotDetection? afterExposureShift = BoardDetector.DetectDarkCalibrationSpot(
        sceneWidth, sceneHeight, sceneWidth * 4, BytesOf(white), BytesOf(shiftedExposure));
    if (afterExposureShift is null || Math.Abs(afterExposureShift.Center.X - center.X) > 4 ||
        Math.Abs(afterExposureShift.Center.Y - center.Y) > 4)
        throw new Exception("Calibration spot was lost after global exposure shift.");
    Console.WriteLine("Dark-spot regression: camera centroid within 4 px, exposure shift tolerated; " +
        "unchanged frame rejected");
}

bool Near(BoardDetection? actual, Point[] expected, double tolerance) =>
    actual is not null && actual.Corners.Length == 4 &&
    Enumerable.Range(0, 4).All(i =>
        Math.Sqrt(Math.Pow(actual.Corners[i].X - expected[i].X, 2) +
            Math.Pow(actual.Corners[i].Y - expected[i].Y, 2)) <= tolerance);

void CheckBoardDetection()
{
    const int boardWidth = 640, boardHeight = 480;
    using Mat image = new(boardHeight, boardWidth, MatType.CV_8UC4,
        new Scalar(25, 28, 35, 255));
    Point[] board = [new(105, 48), new(520, 62), new(502, 427), new(90, 411)];
    Point[] grid = [new(154, 89), new(470, 98), new(454, 371), new(137, 359)];
    Cv2.FillConvexPoly(image, board, new Scalar(165, 170, 172, 255));
    Cv2.FillConvexPoly(image, grid, new Scalar(180, 115, 75, 255));
    for (int i = 1; i < 7; i++)
    {
        double t = i / 7.0;
        Point left = Lerp(grid[0], grid[3], t), right = Lerp(grid[1], grid[2], t);
        Point top = Lerp(grid[0], grid[1], t), bottom = Lerp(grid[3], grid[2], t);
        Cv2.Line(image, left, right, new Scalar(70, 60, 50, 255), 1);
        Cv2.Line(image, top, bottom, new Scalar(70, 60, 50, 255), 1);
    }
    BoardDetection? found = BoardDetector.Detect(boardWidth, boardHeight,
        boardWidth * 4, BytesOf(image));
    if (found is null || found.Corners.Length != 4 ||
        Enumerable.Range(0, 4).Any(i =>
            Math.Sqrt(Math.Pow(found.Corners[i].X - board[i].X, 2) +
                Math.Pow(found.Corners[i].Y - board[i].Y, 2)) > 12))
        throw new Exception("Projected-grid board corner regression failed.");

    using Mat noBoard = new(boardHeight, boardWidth, MatType.CV_8UC4,
        new Scalar(25, 28, 35, 255));
    Cv2.FillConvexPoly(noBoard, grid, new Scalar(180, 115, 75, 255));
    if (BoardDetector.Detect(boardWidth, boardHeight, boardWidth * 4,
        BytesOf(noBoard)) is not null)
        throw new Exception("Projection alone was misidentified as the white board.");

    Console.WriteLine($"Board regression: four corners within 12 px; confidence={found.Confidence:F2}; " +
        "projection-only frame rejected");
}

void CheckProjectorFieldIsNotBoard()
{
    const int sceneWidth = 960, sceneHeight = 540;
    using Mat image = new(sceneHeight, sceneWidth, MatType.CV_8UC4,
        new Scalar(29, 24, 28, 255));
    Point[] projectorField = [new(90, 50), new(870, 50), new(870, 490), new(90, 490)];
    Point[] squareGrid = [new(281, 65), new(680, 65), new(680, 465), new(281, 465)];
    Cv2.FillConvexPoly(image, projectorField, new Scalar(160, 65, 30, 255));
    Cv2.FillConvexPoly(image, squareGrid, new Scalar(255, 202, 201, 255));
    if (BoardDetector.Detect(sceneWidth, sceneHeight, sceneWidth * 4,
        BytesOf(image)) is not null)
        throw new Exception("Projector's 16:9 light field was misidentified as the square board.");
    Console.WriteLine("Board regression: 16:9 projector field around square grid rejected");
}

Point Lerp(Point start, Point end, double t) => new(
    (int)Math.Round(start.X + (end.X - start.X) * t),
    (int)Math.Round(start.Y + (end.Y - start.Y) * t));

byte[] BytesOf(Mat image)
{
    int size = checked(image.Width * image.Height * 4);
    byte[] data = new byte[size];
    Marshal.Copy(image.Data, data, 0, size);
    return data;
}

void Enroll(string id, PixelPoint center, double angle)
{
    using var frame = NewFrame();
    GroundTruthPiece truth = DrawPiece(frame, id, center, angle);
    PixelPoint front = Rotate(new PixelPoint(39, 0), center, angle);
    engine.AddLabeledCapture(id, width, height, width * 4, Bytes(frame), truth.Outline, front);
}

Mat NewFrame() => new(height, width, MatType.CV_8UC4, new Scalar(20, 20, 20, 255));

GroundTruthPiece DrawPiece(Mat image, string id, PixelPoint center, double angle)
{
    PixelPoint[] outline = localOutline.Select(p => Rotate(p, center, angle)).ToArray();
    Cv2.FillPoly(image, [outline.Select(p => new Point((int)Math.Round(p.X), (int)Math.Round(p.Y)))],
        new Scalar(180, 180, 180, 255));
    PixelPoint[] dots = id switch
    {
        "A" => [new PixelPoint(-16, -10), new PixelPoint(21, 0)],
        "B" => [new PixelPoint(-22, 1), new PixelPoint(6, -14), new PixelPoint(6, 14)],
        _ => []
    };
    foreach (PixelPoint dot in dots)
    {
        PixelPoint location = Rotate(dot, center, angle);
        Cv2.Circle(image, new Point((int)Math.Round(location.X), (int)Math.Round(location.Y)),
            7, new Scalar(255, 255, 255, 255), -1);
    }
    Moments moments = Cv2.Moments(outline.Select(p => new Point((int)Math.Round(p.X), (int)Math.Round(p.Y))));
    PixelPoint actualCenter = new(moments.M10 / moments.M00, moments.M01 / moments.M00);
    PixelPoint front = Rotate(new PixelPoint(39, 0), center, angle);
    double frontAngle = (Math.Atan2(front.Y - actualCenter.Y, front.X - actualCenter.X) * 180 / Math.PI + 360) % 360;
    return new GroundTruthPiece(id, actualCenter, frontAngle, outline);
}

PixelPoint Rotate(PixelPoint p, PixelPoint center, double angle)
{
    double radians = angle * Math.PI / 180;
    return new PixelPoint(center.X + p.X * Math.Cos(radians) - p.Y * Math.Sin(radians),
        center.Y + p.X * Math.Sin(radians) + p.Y * Math.Cos(radians));
}

byte[] Bytes(Mat image)
{
    var bytes = new byte[width * height * 4];
    Marshal.Copy(image.Data, bytes, 0, bytes.Length);
    return bytes;
}
