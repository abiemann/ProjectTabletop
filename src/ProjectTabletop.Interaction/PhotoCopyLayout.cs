namespace ProjectTabletop.Interaction;

/// <summary>
/// One hand image on the normalized square board. CenterU/V locate the source
/// image's anchor (normally its palm). RotationRadians rotates a
/// middle-finger-up source image (toward negative Y) in the board's Y-down axes.
/// Width and Height describe the unrotated image, preserving its aspect ratio.
/// </summary>
public readonly record struct PhotoCopyPlacement(double CenterU, double CenterV,
    double Width, double Height, double RotationRadians);

/// <summary>Places hand copies clockwise from the outside inward, each pointing at the board center.</summary>
public static class PhotoCopyLayout
{
    // Even dimensions leave four copies around the center, so every finger
    // has a defined inward direction. Overlapping stamps fill the square,
    // including its corners, rather than stopping at an inscribed circle.
    private const int CopiesPerSide = 24;
    public const int DefaultCount = CopiesPerSide * CopiesPerSide;

    public static IReadOnlyList<PhotoCopyPlacement> Create(double spriteWidthOverHeight = .72)
    {
        if (!double.IsFinite(spriteWidthOverHeight) || spriteWidthOverHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(spriteWidthOverHeight));

        // Stamps at the perimeter deliberately extend beyond the board. The
        // board surface clips them so the photo reaches every outside edge.
        const double longEdge = .18;
        double width = spriteWidthOverHeight <= 1 ? longEdge * spriteWidthOverHeight : longEdge;
        double height = spriteWidthOverHeight <= 1 ? longEdge : longEdge / spriteWidthOverHeight;
        var placements = new List<PhotoCopyPlacement>(DefaultCount);
        for (int inset = 0; inset < CopiesPerSide / 2; inset++)
        {
            int last = CopiesPerSide - 1 - inset;
            for (int column = inset; column <= last; column++) Add(column, inset);
            for (int row = inset + 1; row <= last; row++) Add(last, row);
            for (int column = last - 1; column >= inset; column--) Add(column, last);
            for (int row = last - 1; row > inset; row--) Add(inset, row);
        }
        return placements.AsReadOnly();

        void Add(int column, int row)
        {
            double u = column / (double)(CopiesPerSide - 1);
            double v = row / (double)(CopiesPerSide - 1);
            double rotation = Math.Atan2(.5 - v, .5 - u) + Math.PI / 2;
            placements.Add(new(u, v, width, height, rotation));
        }
    }
}
