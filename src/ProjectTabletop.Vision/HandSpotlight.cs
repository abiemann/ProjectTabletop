using System.Diagnostics.CodeAnalysis;

namespace ProjectTabletop.Vision;

/// <summary>
/// A hand-sized light in normalized projector coordinates. Radius is a fraction
/// of output height so its rendered width and height are equal in projector pixels.
/// </summary>
public sealed record HandSpotlight(PixelPoint Center, double Radius)
{
    /// <summary>
    /// Maps all 21 camera landmarks through a row-major camera-to-projector
    /// homography. Fits the thumb and fingers, with a margin for flesh and movement
    /// between camera frames; the wrist may lie outside this tighter circle.
    /// The renderer remains responsible for board clipping.
    /// </summary>
    public static bool TryCreate(HandDetection? hand, IReadOnlyList<double>? cameraToProjector,
        double projectorAspectRatio, [NotNullWhen(true)] out HandSpotlight? spotlight)
    {
        spotlight = null;
        if (hand?.Landmarks is not { Count: 21 } points ||
            !double.IsFinite(hand.Confidence) || hand.Confidence is < 0 or > 1 ||
            !double.IsFinite(hand.RightHandProbability) || hand.RightHandProbability is < 0 or > 1 ||
            cameraToProjector is not { Count: 9 } || cameraToProjector.Any(value => !double.IsFinite(value)) ||
            !double.IsFinite(projectorAspectRatio) || projectorAspectRatio <= 0) return false;

        // A homography may be multiplied by any nonzero scalar. Normalize it
        // before checking denominators so equivalent calibrations behave alike.
        double scale = cameraToProjector.Max(value => Math.Abs(value));
        if (scale == 0) return false;
        double[] h = cameraToProjector.Select(value => value / scale).ToArray();
        double determinant = h[0] * (h[4] * h[8] - h[5] * h[7]) -
            h[1] * (h[3] * h[8] - h[5] * h[6]) + h[2] * (h[3] * h[7] - h[4] * h[6]);
        if (determinant == 0) return false;

        var mapped = new PixelPoint[points.Count];
        int denominatorSign = 0;
        for (int index = 0; index < points.Count; index++)
        {
            PixelPoint point = points[index];
            if (!Finite(point)) return false;
            double denominator = h[6] * point.X + h[7] * point.Y + h[8];
            double denominatorScale = Math.Abs(h[6] * point.X) + Math.Abs(h[7] * point.Y) + Math.Abs(h[8]);
            if (!double.IsFinite(denominator) || Math.Abs(denominator) <= 1e-9 * denominatorScale)
                return false;
            int sign = Math.Sign(denominator);
            if (denominatorSign != 0 && sign != denominatorSign) return false;
            denominatorSign = sign;
            mapped[index] = new(
                (h[0] * point.X + h[1] * point.Y + h[2]) / denominator * projectorAspectRatio,
                (h[3] * point.X + h[4] * point.Y + h[5]) / denominator);
            if (!Finite(mapped[index])) return false;
        }

        // Excluding the wrist from the fit moves the light toward the fingers
        // without reducing their coverage margin. Keep it for hand-scale checks.
        PixelPoint[] fingers = mapped.Skip(1).ToArray();
        PixelPoint center = new((fingers.Min(point => point.X) + fingers.Max(point => point.X)) / 2,
            (fingers.Min(point => point.Y) + fingers.Max(point => point.Y)) / 2);
        double palmSpan = Math.Max(Distance(mapped[0], mapped[9]), Distance(mapped[5], mapped[17]));
        double landmarkRadius = fingers.Max(point => Distance(center, point));
        if (palmSpan <= 1e-6 || landmarkRadius <= 1e-6) return false;
        double radius = landmarkRadius * 1.04 + palmSpan * .025;
        if (!double.IsFinite(radius) || radius > .45) return false;

        spotlight = new(new(center.X / projectorAspectRatio, center.Y), radius);
        return true;
    }

    private static bool Finite(PixelPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
    private static double Distance(PixelPoint first, PixelPoint second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));
}
