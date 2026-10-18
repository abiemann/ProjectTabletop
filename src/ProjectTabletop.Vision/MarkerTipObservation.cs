namespace ProjectTabletop.Vision;

/// <summary>A measured marker component in raw, unmirrored camera pixels, not a predicted contact.</summary>
public sealed record MarkerTipObservation(PixelPoint Center, double RadiusPixels, double AreaPixels, double Score)
{
    /// <summary>The measured black bar's axis and thickness, when available.</summary>
    public BlackBarGeometry? Bar { get; init; }
}

public sealed record MarkerTipDetectionResult(IReadOnlyList<MarkerTipObservation> Candidates, string Reason)
{
    /// <summary>Physical, individually validated strips for bounded continuation of a measured pair.</summary>
    public IReadOnlyList<MarkerTipObservation> SupportingBars { get; init; } = [];
    public IReadOnlyList<PixelPoint> AmbiguousMarkerCenters { get; init; } = [];
    public string Source { get; init; } = "global";
}

public sealed record MarkerTipDetectionOptions(PixelPoint? PreferredCenter = null,
    IReadOnlyList<MarkerProjectionFrame>? ProjectionFrames = null, DateTimeOffset FrameTime = default);

public static class MarkerTipGeometry
{
    public static double Distance(PixelPoint first, PixelPoint second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));
}
