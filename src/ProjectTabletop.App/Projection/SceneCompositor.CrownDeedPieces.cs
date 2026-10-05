using System.Numerics;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private static readonly string[] CrownDeedPieceFiles =
        ["hat", "car", "shoe", "dog", "gun", "iron", "wheelbarrow", "steamship"];
    private CanvasDevice? _crownDeedPieceDevice;
    private CanvasBitmap[]? _crownDeedPieceBitmaps;
    private Rect[]? _crownDeedPieceSourceBounds;

    private void EnsureCrownDeedPieces(CanvasDevice device)
    {
        if (_crownDeedPieceDevice == device && _crownDeedPieceBitmaps is not null) return;
        DisposeCrownDeedPieces();
        var bitmaps = new List<CanvasBitmap>();
        var bounds = new List<Rect>();
        try
        {
            foreach (string file in CrownDeedPieceFiles)
            {
                string path = Path.Combine(AppContext.BaseDirectory, "Assets", "CrownDeed", "Pieces", file + ".png");
                var bitmap = CanvasBitmap.LoadAsync(device, path, 96).AsTask().GetAwaiter().GetResult();
                bitmaps.Add(bitmap);
                int width = (int)bitmap.SizeInPixels.Width, height = (int)bitmap.SizeInPixels.Height;
                byte[] pixels = bitmap.GetPixelBytes();
                int left = width, top = height, right = -1, bottom = -1;
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (pixels[(y * width + x) * 4 + 3] > 0)
                    {
                        left = Math.Min(left, x); top = Math.Min(top, y);
                        right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                    }
                if (right < left) throw new InvalidDataException("Empty Crown & Deed piece: " + file);
                // Keep the original pixels and every antialiased edge. A source
                // rectangle removes only transparent padding, once at load time.
                left = Math.Max(0, left - 2); top = Math.Max(0, top - 2);
                right = Math.Min(width - 1, right + 2); bottom = Math.Min(height - 1, bottom + 2);
                bounds.Add(new Rect(left, top, right - left + 1, bottom - top + 1));
            }
            _crownDeedPieceBitmaps = bitmaps.ToArray();
            _crownDeedPieceSourceBounds = bounds.ToArray();
            _crownDeedPieceDevice = device;
        }
        catch
        {
            foreach (var bitmap in bitmaps) bitmap.Dispose();
            throw;
        }
    }

    // The art's forward axis is screen up (0,-1). Aim along the physical
    // centre ray before applying uniform art correction on a rectangular board.
    internal static float CrownDeedPieceHeading(Vector2 center, double boardAspect)
    {
        double aspect = double.IsFinite(boardAspect) ? Math.Clamp(boardAspect, .2, 5) : 1;
        var direction = new Vector2((float)((500 - center.X) * aspect), 500 - center.Y);
        return direction.LengthSquared() < .000001f ? 0 : MathF.Atan2(direction.Y, direction.X) + MathF.PI / 2;
    }

    private void DrawCrownDeedPiece(CanvasDrawingSession ds, Vector2 center, float radius,
        int pieceIndex, int colorIndex, bool active, double boardAspect = 1, bool faceCenter = true)
    {
        if ((uint)pieceIndex >= (uint)MonopolyGame.PieceNames.Count)
            throw new ArgumentOutOfRangeException(nameof(pieceIndex));
        EnsureCrownDeedPieces(ds.Device);
        using var correction = new MonopolyArtAspect(ds, center, boardAspect);
        // Ownership colour stays in a small ground marker; the metal itself
        // keeps its silver finish and fine engraved surface.
        ds.FillEllipse(center + new Vector2(0, radius * .46f), radius * .65f, radius * .26f,
            ThemeColor(0, 6, 8, 145));
        if (active)
            ds.DrawEllipse(center + new Vector2(0, radius * .45f), radius * .75f, radius * .29f,
                MonopolyGold, Math.Max(.65f, radius * .07f));
        ds.FillCircle(center + new Vector2(0, radius * .89f), Math.Max(1.1f, radius * .12f),
            MonopolyPlayerColor(colorIndex));
        var source = _crownDeedPieceSourceBounds![pieceIndex];
        double scale = radius * 2 / Math.Max(source.Width, source.Height);
        double width = source.Width * scale, height = source.Height * scale;
        var previous = ds.Transform;
        if (faceCenter) ds.Transform = Matrix3x2.CreateRotation(CrownDeedPieceHeading(center, boardAspect), center) * previous;
        try
        {
            ds.DrawImage(_crownDeedPieceBitmaps![pieceIndex],
                new Rect(center.X - width / 2, center.Y - height / 2, width, height), source,
                1, CanvasImageInterpolation.HighQualityCubic);
        }
        finally { ds.Transform = previous; }
    }

    private void DisposeCrownDeedPieces()
    {
        if (_crownDeedPieceBitmaps is { } bitmaps)
            foreach (var bitmap in bitmaps) bitmap.Dispose();
        _crownDeedPieceBitmaps = null;
        _crownDeedPieceSourceBounds = null;
        _crownDeedPieceDevice = null;
    }
}
