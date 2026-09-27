namespace ProjectTabletop.Vision;

/// <summary>
/// The 21 MediaPipe hand landmarks in raw, unmirrored camera pixels. This is an
/// image observation, not a measurement of depth or contact with the board.
/// Confidence and handedness are model scores, not calibrated probabilities.
/// </summary>
public sealed record HandDetection(IReadOnlyList<PixelPoint> Landmarks,
    double Confidence, double RightHandProbability)
{
    public PixelPoint IndexTip => Landmarks[8];
}
