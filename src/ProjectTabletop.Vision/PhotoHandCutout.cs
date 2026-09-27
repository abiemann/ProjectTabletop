namespace ProjectTabletop.Vision;

/// <summary>
/// A segmented photograph in board coordinates, or an unresized original-camera
/// crop when CameraGeometry is present. Pixels are tightly packed, top-down,
/// straight-alpha BGRA8; all exterior pixels are transparent.
/// The historical field names also support arbitrary objects: PalmAnchor is the
/// sprite's rotation anchor, and MiddleFingerDirection is its unit inward-facing
/// axis (positive Y points down). Object captures use their center and an upward
/// axis; detected hands use the palm and middle-finger knuckle-to-tip direction.
/// </summary>
public sealed record PhotoHandCutout(int Width, int Height, byte[] BgraPixels,
    PixelPoint PalmAnchor, PixelPoint MiddleFingerDirection)
{
    public const int BoardPixels = 1000;
    /// <summary>Top-left pixel of a segmented cutout in the normalized board image.</summary>
    public PixelPoint? BoardOrigin { get; init; }
    /// <summary>Present when pixels are an unresized crop of the original camera photograph.</summary>
    public PhotoCopyCameraGeometry? CameraGeometry { get; init; }
}
