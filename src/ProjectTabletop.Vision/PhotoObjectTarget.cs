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
    // Leave a one-percent guard at the physical sides; reserve the title above
    // and controls below. Rendering and segmentation use this capture rectangle.
    public const int CaptureLeft = 10;
    public const int CaptureTop = 60;
    public const int CaptureRight = 990;
    public const int CaptureBottom = 720;
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
    // Pale faces can match the board under white light, whether recovered from
    // separate rim/ink components or acquired as one complete silhouette. Keep
    // stable measured contrast separate from the photographed silhouette.
    public IReadOnlyList<byte>? ContrastEvidence { get; }
    public bool HasRecoveredSurface { get; internal init; }

    internal PhotoObjectTarget(int left, int top, int width, int height, byte[] alpha, int foregroundArea,
        byte[]? contrastEvidence = null)
    {
        Left = left; Top = top; Width = width; Height = height;
        _alpha = Array.AsReadOnly((byte[])alpha.Clone());
        if (contrastEvidence is not null)
        {
            if (contrastEvidence.Length != alpha.Length)
                throw new ArgumentException("Contrast evidence must match the silhouette dimensions.", nameof(contrastEvidence));
            ContrastEvidence = Array.AsReadOnly((byte[])contrastEvidence.Clone());
            HasRecoveredSurface = true;
        }
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
