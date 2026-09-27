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
if (args.Length == 1)
{
    using Mat actual = Cv2.ImRead(args[0], ImreadModes.Unchanged);
    using Mat bgra = new();
    Cv2.CvtColor(actual, bgra, ColorConversionCodes.BGR2BGRA);
    BoardDetection? observed = BoardDetector.Detect(bgra.Width, bgra.Height,
        (int)bgra.Step(), BytesOf(bgra));
    Console.WriteLine($"Captured board: {(observed is null ? "not found" :
        string.Join(", ", observed.Corners.Select(p => $"({p.X:F0},{p.Y:F0})")))}");
}

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
