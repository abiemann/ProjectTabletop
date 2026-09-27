namespace ProjectTabletop.Vision;

/// <summary>
/// Stabilizes illumination geometry only. Call once per fresh observation of
/// the same hand, using elapsed camera-frame time, never the renderer's clock.
/// The result must not be used as hand landmarks or gesture input.
/// </summary>
public static class HandSpotlightSmoother
{
    private static readonly TimeSpan MaximumHistoryGap = TimeSpan.FromMilliseconds(350);

    public static HandSpotlight Smooth(HandSpotlight previous, HandSpotlight current,
        TimeSpan elapsed, double projectorAspectRatio)
    {
        // Returning the fresh fit also handles first observations represented
        // by missing/invalid history. Validation of the current fit stays with
        // HandSpotlight.TryCreate; do not manufacture geometry from bad input.
        if (!Valid(previous) || !Valid(current) ||
            !double.IsFinite(projectorAspectRatio) || projectorAspectRatio <= 0 ||
            elapsed <= TimeSpan.Zero || elapsed > MaximumHistoryGap)
            return current;

        double dx = (current.Center.X - previous.Center.X) * projectorAspectRatio;
        double dy = current.Center.Y - previous.Center.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(distance)) return current;

        // Measure motion in hand radii, so small/distant hands get the same
        // stabilization as large hands. Real movement catches up quickly;
        // slight fitted-center noise gets a longer time constant.
        double movement = Math.Clamp((distance / current.Radius - .03) / .22, 0, 1);
        double centerSeconds = .18 + (.025 - .18) * movement;
        double centerWeight = 1 - Math.Exp(-elapsed.TotalSeconds / centerSeconds);
        double lag = distance * (1 - centerWeight);

        // Center smoothing must never require a large flood around the hand.
        // Bound its lag, then cover the entire fresh fitted circle, including
        // its existing finger/flesh margin, from this slightly delayed center.
        lag = Math.Min(lag, current.Radius * .04);
        double remaining = distance > 0 ? lag / distance : 0;
        var center = new PixelPoint(
            current.Center.X - dx / projectorAspectRatio * remaining,
            current.Center.Y - dy * remaining);

        // Expansion is immediate for finger coverage. Only contraction is
        // filtered: an alternating fit no longer makes the light visibly
        // breathe, while an open-to-pinched hand settles to its smaller fit.
        // Decay targets the raw radius, avoiding accumulated padding.
        double radiusWeight = 1 - Math.Exp(-elapsed.TotalSeconds / .30);
        double radius = Math.Max(current.Radius + lag,
            previous.Radius + (current.Radius - previous.Radius) * radiusWeight);
        return new(center, radius);
    }

    private static bool Valid(HandSpotlight? light) => light is not null &&
        double.IsFinite(light.Center.X) && double.IsFinite(light.Center.Y) &&
        double.IsFinite(light.Radius) && light.Radius > 0;
}
