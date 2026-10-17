using System.Numerics;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection.StrykersRun;

internal sealed partial class StrykersRunRenderer
{
    private CanvasBitmap? _heroScarf;
    private readonly Rect[] _heroScarfCells = new Rect[4];
    // Absolute body-atlas coordinates; the existing actor cells crop each pose
    // independently, so subtract that crop before positioning the cloth root.
    private static readonly Vector2[] HeroNeckAnchors =
        [new(321, 173), new(835, 172), new(1319, 174), new(1832, 173)];
    private static readonly Vector2[] ScarfNeckAnchors =
        [new(473, 324), new(469, 323), new(469, 323), new(468, 323)];
    private const float ScarfArtworkScale = .55f;

    private void LoadHeroScarf(BitmapAssetSet images)
    {
        var body = images.Image("hero-body.png")!;
        _heroScarf = images.Image("hero-scarf.png")!;
        if (body.SizeInPixels.Width != 2172 || body.SizeInPixels.Height != 724 ||
            _heroScarf.SizeInPixels.Width != 2048 || _heroScarf.SizeInPixels.Height != 768)
            throw new InvalidDataException("The hero and scarf atlases must match their measured neck anchors.");
        // Full equal cells retain each cloth pose's measured attachment point.
        for (int frame = 0; frame < _heroScarfCells.Length; frame++)
            _heroScarfCells[frame] = new(frame * 512, 0, 512, 768);
    }

    private void DrawHeroScarf(CanvasSpriteBatch batch, int frame, Rect bodyDestination, float side,
        bool shadow = false)
    {
        // The cloth briefly turns edge-on while changing sides. The body, gun,
        // feet baseline and run-cycle selection are never mirrored or altered.
        if (Math.Abs(side) < .001f) return;
        Rect bodySource = _heroCells[frame];
        float scaleX = (float)(bodyDestination.Width / bodySource.Width);
        float scaleY = (float)(bodyDestination.Height / bodySource.Height);
        Vector2 neck = HeroNeckAnchors[frame] - new Vector2((float)bodySource.X, (float)bodySource.Y);
        Vector2 destination = new((float)bodyDestination.X + neck.X * scaleX,
            (float)bodyDestination.Y + neck.Y * scaleY);
        var transform = Matrix3x2.CreateTranslation(-ScarfNeckAnchors[frame]) *
            Matrix3x2.CreateScale(-side * scaleX * ScarfArtworkScale, scaleY * ScarfArtworkScale) *
            Matrix3x2.CreateTranslation(destination);
        batch.DrawFromSpriteSheet(_heroScarf!, transform, _heroScarfCells[frame],
            shadow ? new Vector4(0, 0, 0, .42f) : Vector4.One);
    }
}
