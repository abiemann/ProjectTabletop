using System.Numerics;

namespace ProjectTabletop.App.Projection.WaterGarden;

// Static, water-plane footprints. Keep this geometry independent of the entrance
// camera so collision moves with the pond rather than sliding under the artwork.
internal sealed class WaterGardenObstacles
{
    private const float FootprintDepthScale = .4f;
    private readonly byte[] _mask;
    private readonly float[] _distance;
    private readonly float _cellWidth, _cellHeight, _halfDiagonal;

    public WaterGardenObstacles(int width, int height, double aspect, byte[] mask)
    {
        ValidateGeometry(width, height, aspect);
        ArgumentNullException.ThrowIfNull(mask);
        if (mask.Length != checked(width * height))
            throw new ArgumentException("The obstacle mask must match the water field.", nameof(mask));
        Width = width;
        Height = height;
        Aspect = aspect;
        _cellWidth = (float)(aspect / width);
        _cellHeight = 1f / height;
        _halfDiagonal = .5f * MathF.Sqrt(_cellWidth * _cellWidth + _cellHeight * _cellHeight);
        _mask = new byte[mask.Length];
        _distance = new float[mask.Length];
        for (int i = 0; i < mask.Length; i++)
        {
            _mask[i] = mask[i] == 0 ? (byte)0 : (byte)1;
            _distance[i] = _mask[i] == 0 ? float.PositiveInfinity : 0;
        }
        BuildClearance();
    }

    public int Width { get; }
    public int Height { get; }
    public double Aspect { get; }
    public ReadOnlyMemory<byte> Mask => _mask;

    public bool IsSolid(int x, int y) =>
        x >= 0 && x < Width && y >= 0 && y < Height && _mask[y * Width + x] != 0;

    public bool IsSolid(Vector2 surface) => TryCell(surface, out int x, out int y) && IsSolid(x, y);

    // Distance is a conservative lower bound in physical pond units. Subtract
    // the solid cell's extent and the query's offset from its own cell centre.
    public float Clearance(Vector2 surface)
    {
        if (!TryCell(surface, out int x, out int y)) return 0;
        float dx = (surface.X * Width - x - .5f) * _cellWidth;
        float dy = (surface.Y * Height - y - .5f) * _cellHeight;
        return Math.Max(0, _distance[y * Width + x] - _halfDiagonal - MathF.Sqrt(dx * dx + dy * dy));
    }

    public bool IsPathClear(Vector2 from, Vector2 to)
    {
        if (!TryCell(from, out int x, out int y) || !TryCell(to, out int endX, out int endY) || IsSolid(x, y))
            return false;
        double dx = to.X - from.X, dy = to.Y - from.Y;
        int stepX = Math.Sign(dx), stepY = Math.Sign(dy);
        double deltaX = stepX == 0 ? double.PositiveInfinity : 1 / (Math.Abs(dx) * Width);
        double deltaY = stepY == 0 ? double.PositiveInfinity : 1 / (Math.Abs(dy) * Height);
        double nextX = stepX == 0 ? double.PositiveInfinity :
            ((x + (stepX > 0 ? 1 : 0)) / (double)Width - from.X) / dx;
        double nextY = stepY == 0 ? double.PositiveInfinity :
            ((y + (stepY > 0 ? 1 : 0)) / (double)Height - from.Y) / dy;
        // Traverse every intersected cell, including both sides of a corner;
        // point sampling can otherwise jump over a narrow part of a rock.
        while (x != endX || y != endY)
        {
            double crossingX = x == endX ? double.PositiveInfinity : nextX;
            double crossingY = y == endY ? double.PositiveInfinity : nextY;
            if (Math.Abs(crossingX - crossingY) < 1e-10)
            {
                if (IsSolid(x + stepX, y) || IsSolid(x, y + stepY)) return false;
                x += stepX; y += stepY;
                nextX += deltaX; nextY += deltaY;
            }
            else if (crossingX < crossingY) { x += stepX; nextX += deltaX; }
            else { y += stepY; nextY += deltaY; }
            if (IsSolid(x, y)) return false;
        }
        return true;
    }

    public static WaterGardenObstacles FromRockArtwork(int width, int height, double aspect,
        byte[] bgra, int imageWidth, int imageHeight, double opaqueFoot)
    {
        ValidateGeometry(width, height, aspect);
        ArgumentNullException.ThrowIfNull(bgra);
        if (imageWidth <= 0 || imageHeight <= 0 || bgra.Length != checked(imageWidth * imageHeight * 4))
            throw new ArgumentException("Rock pixels must match the artwork dimensions.", nameof(bgra));
        if (!double.IsFinite(opaqueFoot) || opaqueFoot < 0 || opaqueFoot > 1)
            throw new ArgumentOutOfRangeException(nameof(opaqueFoot));
        var mask = new byte[checked(width * height)];
        var feet = new int[imageWidth];
        for (int x = 0; x < imageWidth; x++)
            for (int y = imageHeight - 1; y >= 0; y--)
                if (bgra[(y * imageWidth + x) * 4 + 3] > 32)
                { feet[x] = y + 1; break; }
        var (first, second) = WaterGardenRockLayout.GetPlacements(
            aspect, imageWidth / (double)imageHeight, opaqueFoot);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var screen = WaterGardenView.SurfaceToScreen(new((x + .5f) / width, (y + .5f) / height));
            if (Contains(first, screen) || Contains(second, screen)) mask[y * width + x] = 1;
        }
        return new(width, height, aspect, mask);

        bool Contains(WaterGardenRockPlacement placement, Vector2 screen)
        {
            var bounds = placement.Bounds;
            double sourceX = (screen.X - bounds.X) / bounds.Width * imageWidth;
            if (sourceX < 0 || sourceX >= imageWidth) return false;
            int px = (int)sourceX, foot = feet[px];
            if (foot == 0) return false;
            double localY = (screen.Y - bounds.Y) / bounds.Height * imageHeight;
            double waterline = foot - placement.SubmergedHeight / bounds.Height * imageHeight;
            // Preserve the irregular contact contour, compressing raised stone
            // into its submerged base instead of obstructing the whole billboard.
            double sourceY = foot + (localY - waterline) / FootprintDepthScale;
            return sourceY >= 0 && sourceY < imageHeight &&
                bgra[((int)sourceY * imageWidth + px) * 4 + 3] > 32;
        }
    }

    private void BuildClearance()
    {
        // Uniform 8-neighbour cost is no larger than any physical cell step,
        // so this two-pass chessboard distance never overstates safe clearance.
        float step = Math.Min(_cellWidth, _cellHeight);
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int i = y * Width + x;
            float distance = _distance[i];
            if (x > 0) distance = Math.Min(distance, _distance[i - 1] + step);
            if (y > 0)
            {
                distance = Math.Min(distance, _distance[i - Width] + step);
                if (x > 0) distance = Math.Min(distance, _distance[i - Width - 1] + step);
                if (x + 1 < Width) distance = Math.Min(distance, _distance[i - Width + 1] + step);
            }
            _distance[i] = distance;
        }
        for (int y = Height - 1; y >= 0; y--)
        for (int x = Width - 1; x >= 0; x--)
        {
            int i = y * Width + x;
            float distance = _distance[i];
            if (x + 1 < Width) distance = Math.Min(distance, _distance[i + 1] + step);
            if (y + 1 < Height)
            {
                distance = Math.Min(distance, _distance[i + Width] + step);
                if (x > 0) distance = Math.Min(distance, _distance[i + Width - 1] + step);
                if (x + 1 < Width) distance = Math.Min(distance, _distance[i + Width + 1] + step);
            }
            _distance[i] = distance;
        }
    }

    private bool TryCell(Vector2 surface, out int x, out int y)
    {
        x = y = 0;
        if (!float.IsFinite(surface.X) || !float.IsFinite(surface.Y) ||
            surface.X < 0 || surface.X > 1 || surface.Y < 0 || surface.Y > 1) return false;
        x = Math.Min(Width - 1, (int)(surface.X * (double)Width));
        y = Math.Min(Height - 1, (int)(surface.Y * (double)Height));
        return true;
    }

    private static void ValidateGeometry(int width, int height, double aspect)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (!double.IsFinite(aspect) || aspect <= 0) throw new ArgumentOutOfRangeException(nameof(aspect));
    }
}
