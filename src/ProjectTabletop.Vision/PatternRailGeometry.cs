namespace ProjectTabletop.Vision;

/// <summary>A stationary dotted input rail, mapped from its canonical raster to the camera.
/// Corners are top-left, top-right, bottom-right, bottom-left in rail coordinates, including after board rotation.</summary>
public sealed record PatternRailGeometry(long Revision, IReadOnlyList<PixelPoint> CameraCorners)
{
    public const int ReferenceWidth = Columns * CellWidth;
    public const int ReferenceHeight = 64;
    public const int Columns = 49;
    public const int CellWidth = 20;
    public const int DotRadius = 5;
    public const int DotY1 = 22;
    public const int DotY2 = 42;
    public static int ColumnCenter(int column) => column * CellWidth + CellWidth / 2;
    // Sparse wider colon pairs make a whole-cell image shift observable without adding a moving reference.
    // Four ordinary pairs at each end keep the complete 49-column pattern mirror-symmetric.
    public static int DotY1ForColumn(int column) => column % 8 == 4 ? 15 : DotY1;
    public static int DotY2ForColumn(int column) => column % 8 == 4 ? 49 : DotY2;
}
