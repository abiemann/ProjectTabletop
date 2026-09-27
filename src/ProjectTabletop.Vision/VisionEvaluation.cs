using OpenCvSharp;

namespace ProjectTabletop.Vision;

public sealed partial class VisionEngine
{
    /// <summary>
    /// Re-run the current detector on annotated BGRA frames. Save the input frames and
    /// annotations alongside the profile to make this a repeatable regression check.
    /// Errors are reported in camera pixels/degrees; stage millimeters need calibration.
    /// </summary>
    public EvaluationReport Evaluate(IEnumerable<EvaluationFrame> frames)
    {
        lock (_gate) return EvaluateCore(frames);
    }

    private EvaluationReport EvaluateCore(IEnumerable<EvaluationFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        int frameCount = 0, expected = 0, found = 0, correct = 0, falsePositives = 0;
        var cases = new List<EvaluationCaseResult>();
        var centerErrors = new List<double>();
        var angleErrors = new List<double>();
        var overlaps = new List<double>();
        foreach (EvaluationFrame frame in frames)
        {
            frameCount++;
            IReadOnlyList<PieceDetection> detections = Detect(frame.Width, frame.Height,
                frame.Stride, frame.Bgra);
            var unmatched = detections.ToList();
            double maximumMatchDistance = Math.Max(30, 0.10 * Math.Sqrt(
                frame.Width * (double)frame.Width + frame.Height * (double)frame.Height));
            foreach (GroundTruthPiece truth in frame.Pieces)
            {
                expected++;
                PieceDetection? nearest = unmatched.OrderBy(d => Distance(d.Center, truth.Center))
                    .FirstOrDefault();
                if (nearest is null || Distance(nearest.Center, truth.Center) > maximumMatchDistance)
                {
                    cases.Add(new EvaluationCaseResult(frame.FrameId, truth.PieceId, false,
                        false, null, null, null));
                    continue;
                }
                unmatched.Remove(nearest);
                found++;
                bool identityCorrect = nearest.PieceId == truth.PieceId;
                if (identityCorrect) correct++;
                double centerError = Distance(nearest.Center, truth.Center);
                double angleError = Math.Abs((nearest.AngleDegrees - truth.AngleDegrees + 540) % 360 - 180);
                double overlap = OutlineIoU(frame.Width, frame.Height, nearest.Outline, truth.Outline);
                if (identityCorrect)
                {
                    centerErrors.Add(centerError);
                    angleErrors.Add(angleError);
                    overlaps.Add(overlap);
                }
                cases.Add(new EvaluationCaseResult(frame.FrameId, truth.PieceId, true,
                    identityCorrect, centerError, angleError, overlap));
            }
            falsePositives += unmatched.Count;
        }
        return new EvaluationReport(frameCount, expected, found, correct, falsePositives,
            centerErrors.Count == 0 ? 0 : centerErrors.Average(),
            angleErrors.Count == 0 ? 0 : angleErrors.Average(),
            overlaps.Count == 0 ? 0 : overlaps.Average(), cases);
    }

    private static double Distance(PixelPoint a, PixelPoint b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double OutlineIoU(int width, int height,
        IReadOnlyList<PixelPoint> predicted, IReadOnlyList<PixelPoint> actual)
    {
        if (predicted.Count < 3 || actual.Count < 3) return 0;
        using var a = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
        using var b = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
        Cv2.FillPoly(a, [predicted.Select(VisionFeatures.ToCvPoint)], Scalar.White);
        Cv2.FillPoly(b, [actual.Select(VisionFeatures.ToCvPoint)], Scalar.White);
        using var intersection = new Mat();
        using var union = new Mat();
        Cv2.BitwiseAnd(a, b, intersection);
        Cv2.BitwiseOr(a, b, union);
        int unionPixels = Cv2.CountNonZero(union);
        return unionPixels == 0 ? 0 : Cv2.CountNonZero(intersection) / (double)unionPixels;
    }
}
