using System.Numerics;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private CanvasDevice? _crownDeedPieceDevice;
    private CanvasBitmap?[]? _crownDeedPieceBitmaps;
    private Rect[]? _crownDeedPieceSourceBounds;

    private bool EnsureCrownDeedPieces(CanvasDevice device) => PrepareCrownDeedResources(device);

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
        if (!EnsureCrownDeedPieces(ds.Device) || _crownDeedPieceBitmaps![pieceIndex] is not { } bitmap) return;
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
            ds.DrawImage(bitmap,
                new Rect(center.X - width / 2, center.Y - height / 2, width, height), source,
                1, CanvasImageInterpolation.HighQualityCubic);
        }
        finally { ds.Transform = previous; }
    }

    private void DisposeCrownDeedPieces()
    {
        _crownDeedPieceBitmaps = null;
        _crownDeedPieceSourceBounds = null;
        _crownDeedPieceDevice = null;
    }
}
