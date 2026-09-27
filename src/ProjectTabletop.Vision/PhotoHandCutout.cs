namespace ProjectTabletop.Vision;

/// <summary>
/// An actual camera photograph rectified to board coordinates. Pixels are tightly
/// packed, top-down, straight-alpha BGRA8; all exterior pixels are transparent.
/// PalmAnchor is in sprite pixels. MiddleFingerDirection is a unit vector in the
/// same axes, from middle-finger knuckle to tip (positive Y points down).
/// </summary>
public sealed record PhotoHandCutout(int Width, int Height, byte[] BgraPixels,
    PixelPoint PalmAnchor, PixelPoint MiddleFingerDirection)
{
    public const int BoardPixels = 1000;
}
