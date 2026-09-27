using System.Collections.ObjectModel;

namespace ProjectTabletop.Vision;

/// <summary>
/// Immutable silhouette measured on an unlit, neutral capture field. Coordinates
/// and the cropped straight alpha are in the 1000 by 1000 rectified board space.
/// The silhouette is reused under the spotlight; its projected light is never
/// segmented into a new object. This target assumes the subject stays stationary.
/// </summary>
public sealed class PhotoObjectTarget
{
    // Leave a one-percent guard at the physical sides/bottom; reserve the upper
    // controls. Rendering and segmentation must use this same capture rectangle.
    public const int CaptureLeft = 10;
    public const int CaptureTop = 230;
    public const int CaptureRight = 990;
    public const int CaptureBottom = 990;
    private readonly ReadOnlyCollection<byte> _alpha;
    public int Left { get; }
    public int Top { get; }
    public int Width { get; }
    public int Height { get; }
    public PixelPoint Center { get; }
    /// <summary>Solid white radius, including surrounding background for presence checks.</summary>
    public double SpotlightRadius { get; }
    /// <summary>Shape-aware solid white core in rectified board coordinates.</summary>
    public PhotoObjectSpotlight Spotlight { get; }
    public int ForegroundArea { get; }
    public IReadOnlyList<byte> Alpha => _alpha;

    internal PhotoObjectTarget(int left, int top, int width, int height, byte[] alpha, int foregroundArea)
    {
        Left = left; Top = top; Width = width; Height = height;
        _alpha = Array.AsReadOnly((byte[])alpha.Clone());
        ForegroundArea = foregroundArea;
        Center = new(left + (width - 1) / 2.0, top + (height - 1) / 2.0);
        SpotlightRadius = Math.Sqrt(width * width + height * height) / 2 + 28;
        Spotlight = PhotoObjectSpotlight.Create(left, top, width, height, alpha, Center, SpotlightRadius);
    }
}

public enum PhotoObjectTargetState
{
    Present,
    MissingOrMoved,
    Occluded,
    Unavailable
}
