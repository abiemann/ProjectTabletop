using ProjectTabletop.Vision;

internal static class HandAcquisitionHintRegression
{
    private static readonly HandAcquisitionHint Original = new(new(804, 367, 281, 281),
        new(944, 508), 75, new(2026, 9, 28, 17, 18, 55, TimeSpan.Zero), .11, .21, .41);

    public static void Run()
    {
        var fitted = Original with { CandidateBounds = new(862, 263, 176, 264) };
        var constrained = fitted.ConstrainToFrame(1920, 1080);
        Require(constrained.ValidatedCandidateBounds == fitted.CandidateBounds &&
            constrained.CandidateBounds == fitted.CandidateBounds,
            "A measured palm connected to the original caption lost its native candidate geometry.");
        PreserveOriginalEvidence(constrained);
        var center = constrained.IlluminationCenter;
        double opaqueRadius = constrained.IlluminationRadiusPixels * .82;
        var bounds = constrained.ValidatedCandidateBounds!.Value;
        foreach (var corner in new PixelPoint[] { new(bounds.X, bounds.Y),
            new(bounds.X + bounds.Width, bounds.Y), new(bounds.X, bounds.Y + bounds.Height),
            new(bounds.X + bounds.Width, bounds.Y + bounds.Height) })
            Require(Distance(center, corner) <= opaqueRadius,
                "Fitted acquisition assistance leaves connected foreground outside its opaque core.");
        Require(Distance(center, Original.Center) + Original.RadiusPixels * .64 <= opaqueRadius + 1e-9,
            "Moving acquisition assistance lost the original measured caption core.");

        foreach (var malformed in InvalidCandidates())
        {
            var hint = Original with { CandidateBounds = malformed };
            Require(hint.ValidatedCandidateBounds is null,
                $"Malformed or detached foreground geometry became a fitted light: {malformed}.");
            CheckFallback(hint.ConstrainToFrame(1920, 1080));
        }
        CheckFallback(Original.ConstrainToFrame(1920, 1080));

        // A camera can crop a connected shape or be too small for its measured
        // context. Keep the original caption crop rather than silently clip the
        // palm/core union into a different inference and lighting geometry.
        var beyondFrame = Original with { CandidateBounds = new(862, 263, 176, 300) };
        Require(beyondFrame.ValidatedCandidateBounds is not null,
            "The frame-boundary fixture is malformed before native frame validation.");
        CheckFallback(beyondFrame.ConstrainToFrame(1920, 550));
        var exceedsCap = Original with { CandidateBounds = new(724, 278, 440, 400) };
        Require(exceedsCap.ValidatedCandidateBounds is not null,
            "The bounded crop-cap fixture is malformed before native frame validation.");
        CheckFallback(exceedsCap.ConstrainToFrame(1920, 700));
        var unionExceedsCap = Original with { CandidateBounds = new(990, 400, 403, 150) };
        Require(unionExceedsCap.ValidatedCandidateBounds is not null &&
            unionExceedsCap.CandidateBounds.Value.Width < 700 * .60,
            "The core-union fixture must fit by itself and touch the original control core.");
        CheckFallback(unionExceedsCap.ConstrainToFrame(1920, 700));

        Console.WriteLine("Hand acquisition hint geometry: native connected palm and original caption core " +
            "covered by opaque assistance; malformed/detached/overflowing candidates, frame clipping and " +
            "60% context-cap failures preserve original evidence and fallback geometry.");
    }

    private static IEnumerable<HandTrackingBounds> InvalidCandidates()
    {
        yield return new(-1, 263, 176, 264);
        yield return new(862, -1, 176, 264);
        yield return new(862, 263, 0, 264);
        yield return new(862, 263, 176, 0);
        yield return new(862, 263, -176, 264);
        yield return new(862, 263, 176, -264);
        yield return new(30, 30, 176, 264);
        yield return new(800, 263, 563, 264);
        yield return new(862, 0, 176, 563);
        yield return new(double.MaxValue, 263, double.MaxValue, 264);
        yield return new(862, double.MaxValue, 176, double.MaxValue);
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            yield return new(value, 263, 176, 264);
            yield return new(862, value, 176, 264);
            yield return new(862, 263, value, 264);
            yield return new(862, 263, 176, value);
        }
    }

    private static void CheckFallback(HandAcquisitionHint hint)
    {
        Require(hint.CandidateBounds is null && hint.ValidatedCandidateBounds is null &&
            hint.IlluminationCenter == Original.Center && hint.IlluminationRadiusPixels == Original.RadiusPixels,
            "Rejected candidate geometry did not restore the original caption illumination.");
        PreserveOriginalEvidence(hint);
    }

    private static void PreserveOriginalEvidence(HandAcquisitionHint hint) => Require(
        hint with { CandidateBounds = null } == Original,
        "Fitted assistance changed original search bounds, trigger geometry, source time or measured coverage.");

    private static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(
        Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
