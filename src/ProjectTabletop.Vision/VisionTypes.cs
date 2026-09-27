namespace ProjectTabletop.Vision;

/// <summary>Coordinates in the original camera image, in pixels.</summary>
public readonly record struct PixelPoint(double X, double Y);

public enum SegmentationMode
{
    /// <summary>Use a fixed grayscale threshold to find bright pieces.</summary>
    Brightness,
    /// <summary>Use an automatically selected grayscale threshold to find bright pieces.</summary>
    OtsuBright,
    /// <summary>Compare the frame with a captured empty-board image. Best for a fixed projected image.</summary>
    BackgroundDifference
}

/// <summary>
/// Detection settings are deliberately editable because the optical behavior must be
/// measured with the actual camera, lighting, projector content, and piece finish.
/// </summary>
public sealed class VisionSettings
{
    public SegmentationMode SegmentationMode { get; set; } = SegmentationMode.OtsuBright;
    public int BrightnessThreshold { get; set; } = 205;
    public int DifferenceThreshold { get; set; } = 35;
    public double MinimumAreaPixels { get; set; } = 300;
    public double MaximumAreaFraction { get; set; } = 0.12;
    public int MorphologyKernelSize { get; set; } = 3;
    public double UnknownDistanceThreshold { get; set; } = 7.0;
    public double MinimumClassMargin { get; set; } = 0.25;
    public int PatternBrightnessThreshold { get; set; } = 225;
    public double PatternPoseWeight { get; set; } = 0.20;
    public double MaximumPoseError { get; set; } = 1.0;
    /// <summary>Optional board region in camera pixels. Null uses the full frame.</summary>
    public PixelRect? RegionOfInterest { get; set; }
}

public readonly record struct PixelRect(int X, int Y, int Width, int Height);

public sealed record CaptureInfo(string CaptureId, string PieceId, int Width, int Height);

/// <param name="PieceId">Stable ID to use for media assignment.</param>
/// <param name="Center">Camera-pixel centroid of the fitted piece.</param>
/// <param name="AngleDegrees">Direction from center to the enrolled front anchor in camera coordinates.</param>
/// <param name="Outline">Fitted top outline in camera pixels, suitable for mapping to a projection mask.</param>
/// <param name="ObservedOutline">Detected contour before the model fit; useful for tuning.</param>
/// <param name="Confidence">Relative score from 0 to 1. Validate against actual footage before interpreting it as a probability.</param>
public sealed record PieceDetection(
    string PieceId,
    PixelPoint Center,
    double AngleDegrees,
    IReadOnlyList<PixelPoint> Outline,
    IReadOnlyList<PixelPoint> ObservedOutline,
    double Confidence,
    double AreaPixels);

public sealed record TrainingReport(
    int PieceCount,
    int CaptureCount,
    double? LeaveOneOutIdentityAccuracy,
    IReadOnlyDictionary<string, int> CapturesPerPiece);

public sealed record GroundTruthPiece(
    string PieceId,
    PixelPoint Center,
    double AngleDegrees,
    IReadOnlyList<PixelPoint> Outline);

/// <summary>One annotated frame for repeatable offline evaluation.</summary>
public sealed record EvaluationFrame(
    string FrameId,
    int Width,
    int Height,
    int Stride,
    byte[] Bgra,
    IReadOnlyList<GroundTruthPiece> Pieces);

public sealed record EvaluationCaseResult(
    string FrameId,
    string PieceId,
    bool Found,
    bool CorrectIdentity,
    double? CenterErrorPixels,
    double? AngleErrorDegrees,
    double? OutlineIntersectionOverUnion);

public sealed record EvaluationReport(
    int FrameCount,
    int ExpectedPieceCount,
    int FoundPieceCount,
    int CorrectIdentityCount,
    int FalsePositiveCount,
    double MeanCenterErrorPixels,
    double MeanAngleErrorDegrees,
    double MeanOutlineIntersectionOverUnion,
    IReadOnlyList<EvaluationCaseResult> Cases);
