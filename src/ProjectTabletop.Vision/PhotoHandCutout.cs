namespace ProjectTabletop.Vision;

/// <summary>
/// An actual camera photograph rectified to board coordinates. Pixels are tightly
/// packed, top-down, straight-alpha BGRA8; all exterior pixels are transparent.
/// The historical field names also support arbitrary objects: PalmAnchor is the
/// sprite's rotation anchor, and MiddleFingerDirection is its unit inward-facing
/// axis (positive Y points down). Object captures use their center and an upward
/// axis; detected hands use the palm and middle-finger knuckle-to-tip direction.
/// </summary>
public sealed record PhotoHandCutout(int Width, int Height, byte[] BgraPixels,
    PixelPoint PalmAnchor, PixelPoint MiddleFingerDirection)
{
    public const int BoardPixels = 1000;
}
