using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // Authored opaque coin faces in the 1700 x 925 master. Rear anchors stay
    // outside the raised foreground contour, so each uses its own baseline.
    private static readonly (float X, float Y, bool Front)[] SlotNestSparkleAnchors =
    [
        (180, 545, false), (285, 505, false), (380, 555, false),
        (1230, 504, false), (1400, 540, false), (1525, 557, false),
        (554, 644, true), (610, 575, true), (706, 558, true),
        (790, 614, true), (930, 589, true), (1055, 629, true)
    ];

    private void DrawSlotEggSurroundings(CanvasDrawingSession ds, Vector2 center, float aspect,
        Color color, int index, float strength)
    {
        // These lights share the live egg layer. Their softly moving falloff
        // illuminates the surrounding cavern without drawing a socket or ring.
        float breath = .84f + .16f * MathF.Sin(_slotVfxTime * 1.15f + index * 2.3f);
        float radiusX = (59 + 2 * MathF.Sin(_slotVfxTime * .72f + index)) / aspect;
        float glow = strength * breath;
        using var halo = new CanvasRadialGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = WithAlpha(color, (byte)(glow * 165)) },
            new() { Position = .42f, Color = WithAlpha(color, (byte)(glow * 165)) },
            new() { Position = .68f, Color = WithAlpha(color, (byte)(glow * 100)) },
            new() { Position = .9f, Color = WithAlpha(color, (byte)(glow * 20)) },
            new() { Position = 1, Color = WithAlpha(color, 0) }
        ]) { Center = center, RadiusX = radiusX, RadiusY = 49 };
        ds.FillEllipse(center, radiusX, 49, halo);

        var inner = center + new Vector2(5 * MathF.Sin(_slotVfxTime * .83f + index * 1.7f) / aspect, 10);
        using var radiance = new CanvasRadialGradientBrush(ds.Device,
            WithAlpha(color, (byte)(glow * 65)), WithAlpha(color, 0))
        { Center = inner, RadiusX = 38 / aspect, RadiusY = 37 };
        ds.FillEllipse(inner, 38 / aspect, 37, radiance);

        // A few dim rising flecks connect the colored light with the hoard.
        // Hatched dragons retain their separate, stronger activation motes.
        for (int mote = 0; mote < 4; mote++)
        {
            float phase = (_slotVfxTime * (.13f + mote * .013f) + index * .23f + mote * .271f) % 1;
            float side = mote % 2 == 0 ? -1 : 1;
            float x = side * (33 + mote * 3) + MathF.Sin(phase * 5 + index + mote) * 5;
            var point = new Vector2(center.X + x / aspect, center.Y + 42 - phase * 79);
            float opacity = MathF.Sin(phase * MathF.PI) * glow;
            ds.FillEllipse(point, 1.1f / aspect, 1.1f, WithAlpha(color, (byte)(opacity * 90)));
            ds.DrawLine(point, point + new Vector2(-.4f / aspect, 2.8f),
                WithAlpha(color, (byte)(opacity * 40)), .6f);
        }
    }

    private void DrawSlotHeaderCoinPile(CanvasDrawingSession ds, Vector2 center, float aspect, int index)
    {
        if (_slotCoinPileArtwork is null) return;
        // Painted bounds of the 1700 x 925 transparent master. Keeping the
        // canvas's empty upper area out of the fit preserves the broad mound.
        double sourceScaleX = _slotCoinPileArtwork.Size.Width / 1700;
        double sourceScaleY = _slotCoinPileArtwork.Size.Height / 925;
        var source = new Rect(22 * sourceScaleX, 306 * sourceScaleY,
            1656 * sourceScaleX, 374 * sourceScaleY);
        var area = new Rect(center.X - 57 / aspect, 214, 114 / aspect, 30);
        double fit = Math.Min(area.Width * aspect / source.Width, area.Height / source.Height);
        double width = source.Width * fit / aspect, height = source.Height * fit;
        var pile = new Rect(center.X - width / 2, area.Bottom - height, width, height);
        ds.DrawImage(_slotCoinPileArtwork, pile, source, 1, CanvasImageInterpolation.HighQualityCubic);
        // Rising flecks remain behind the shell or emerged guardian.
        DrawSlotCoinNestSparkles(ds, center, aspect, index, frontPass: false);
    }

    private static void DrawSlotNestContact(CanvasDrawingSession ds, Vector2 center, float aspect)
    {
        var contact = new Vector2(center.X, 225);
        using var shadow = new CanvasRadialGradientBrush(ds.Device,
            ThemeColor(30, 14, 3, 95), ThemeColor(30, 14, 3, 0))
        { Center = contact, RadiusX = 25 / aspect, RadiusY = 6 };
        ds.FillEllipse(contact, 25 / aspect, 6, shadow);
    }

    private void DrawSlotHeaderCoinFront(CanvasDrawingSession ds, Vector2 center, float aspect, int index)
    {
        if (_slotCoinPileArtwork is null) return;
        double sx = _slotCoinPileArtwork.Size.Width / 1700, sy = _slotCoinPileArtwork.Size.Height / 925;
        var source = new Rect(22 * sx, 306 * sy, 1656 * sx, 374 * sy);
        float scale = 114f / 1656;
        var pile = new Rect(center.X - 57 / aspect, 244 - 374 * scale - 6, 114 / aspect, 374 * scale);
        // The front lip is drawn after the shell/guardian, using the original
        // painted coins. Its cut follows whole coin faces, never a straight
        // horizontal slice through the artwork.
        using var front = SlotNestFrontGeometry(ds.Device);
        using var clip = front.Transform(Matrix3x2.CreateTranslation(-22, -306)
            * Matrix3x2.CreateScale(scale / aspect, scale)
            * Matrix3x2.CreateTranslation((float)pile.X, (float)pile.Y));
        using (ds.CreateLayer(1, clip))
            ds.DrawImage(_slotCoinPileArtwork, pile, source, 1, CanvasImageInterpolation.HighQualityCubic);

        DrawSlotCoinNestSparkles(ds, center, aspect, index, frontPass: true);
    }

    private void DrawSlotCoinNestSparkles(CanvasDrawingSession ds, Vector2 center, float aspect,
        int index, bool frontPass)
    {
        const float sourceScale = 114f / 1656;
        // Two independently hashed schedules give each pile unequal pauses,
        // positions, durations and strengths without consuming game randomness.
        // Inspect the previous block too: a rising fleck can cross its boundary.
        for (int stream = 0; stream < 2; stream++)
        {
            float window = stream == 0 ? 2.8f : 3.9f;
            float clock = _slotVfxTime + index * .79f + stream * 1.13f;
            int block = (int)MathF.Floor(clock / window);
            for (int eventBlock = block - 1; eventBlock <= block; eventBlock++)
            {
                int seed = unchecked(eventBlock * 193 + index * 977 + stream * 421 + 6013);
                float start = eventBlock * window + .15f + SlotRandom(seed + 17) * (window - .65f);
                float age = clock - start;
                if (age < 0 || age > 1.2f) continue;
                int anchor = Math.Min(SlotNestSparkleAnchors.Length - 1,
                    (int)(SlotRandom(seed + 31) * SlotNestSparkleAnchors.Length));
                var (x, y, front) = SlotNestSparkleAnchors[anchor];
                var point = new Vector2(center.X + (x - 850) * sourceScale / aspect,
                    (front ? 238 : 244) + (y - 680) * sourceScale);
                if (frontPass)
                {
                    float duration = .34f + SlotRandom(seed + 53) * .32f;
                    if (age >= duration) continue;
                    float flash = MathF.Sin(age / duration * MathF.PI);
                    flash *= flash * (stream == 0 ? .98f : .78f);
                    float size = (front ? .48f : .53f) + SlotRandom(seed + 71) * .12f;
                    // This high foreground coin touches the shell's base.
                    if (anchor == 8) size = Math.Min(size, .48f);
                    DrawSlotNestSparkle(ds, point, aspect, flash, size,
                        SlotRandom(seed + 89) * 1.4f);
                }
                else if (!front)
                {
                    // Small flank flecks lift a few units, then disappear.
                    // The egg pass naturally occludes any path near its edge.
                    int count = SlotRandom(seed + 101) > .65f ? 2 : 1;
                    for (int grain = 0; grain < count; grain++)
                    {
                        float duration = .5f + SlotRandom(seed + grain * 37 + 113) * .25f;
                        float fraction = (age - .06f - grain * .11f) / duration;
                        if (fraction <= 0 || fraction >= 1) continue;
                        float random = SlotRandom(seed + grain * 59 + 137);
                        var drift = new Vector2((random - .5f) * 3 * fraction / aspect,
                            -(5 + random * 4) * fraction);
                        float light = MathF.Sin(fraction * MathF.PI);
                        DrawSlotNestSparkle(ds, point + drift, aspect, light * light * .7f,
                            .13f + random * .05f, random * 1.4f + fraction * .3f);
                    }
                }
            }
        }
    }

    private static void DrawSlotNestSparkle(CanvasDrawingSession ds, Vector2 point, float aspect,
        float flash, float size, float angle)
    {
        if (flash <= .005f) return;
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(size) * Matrix3x2.CreateRotation(angle)
            * Matrix3x2.CreateScale(1 / aspect, 1) * Matrix3x2.CreateTranslation(point) * previous;
        try { DrawSlotHoardGlint(ds, Vector2.Zero, 1, flash); }
        finally { ds.Transform = previous; }
    }

    private static CanvasGeometry SlotNestFrontGeometry(CanvasDevice device)
    {
        // Traced in the 1700 x 925 master: follow the silhouettes of three
        // complete foreground coins and retain the connected gold below them.
        return CanvasGeometry.CreatePolygon(device,
        [
            new(486, 634), new(501, 620), new(519, 613), new(540, 609),
            new(539, 589), new(541, 572), new(550, 556), new(565, 542),
            new(586, 534), new(607, 528), new(628, 525), new(647, 527),
            new(664, 534), new(676, 545), new(689, 539), new(708, 530),
            new(730, 524), new(750, 522), new(752, 496), new(751, 478),
            new(751, 461), new(757, 447), new(770, 435), new(789, 428),
            new(806, 427), new(824, 431), new(843, 440), new(860, 453),
            new(875, 469), new(886, 488), new(892, 507), new(889, 525),
            new(879, 539), new(891, 531), new(914, 522), new(934, 522),
            new(953, 528), new(971, 540), new(984, 556), new(993, 575),
            new(996, 595), new(991, 615), new(1007, 619), new(1028, 611),
            new(1057, 608), new(1083, 610), new(1100, 619), new(1104, 638),
            new(1091, 652), new(1073, 663), new(1028, 678), new(928, 680),
            new(824, 680), new(727, 680), new(622, 680), new(535, 678),
            new(505, 670), new(491, 654)
        ]);
    }

    private static void DrawSlotHoardGlint(CanvasDrawingSession ds, Vector2 point, float aspect, float flash)
    {
        if (flash <= .005f) return;
        float radius = 5 + flash * 3;
        using var bloom = new CanvasRadialGradientBrush(ds.Device,
            ThemeColor(255, 202, 91, (byte)(flash * 110)), ThemeColor(255, 179, 51, 0))
        { Center = point, RadiusX = radius / aspect, RadiusY = radius };
        ds.FillEllipse(point, radius / aspect, radius, bloom);

        float longRay = 2.5f + 4.5f * flash, shortRay = longRay * .68f;
        using var star = CanvasGeometry.CreatePolygon(ds.Device,
        [
            point + new Vector2(0, -longRay), point + new Vector2(.6f / aspect, -.65f),
            point + new Vector2(shortRay / aspect, 0), point + new Vector2(.6f / aspect, .65f),
            point + new Vector2(0, longRay), point + new Vector2(-.6f / aspect, .65f),
            point + new Vector2(-shortRay / aspect, 0), point + new Vector2(-.6f / aspect, -.65f)
        ]);
        ds.FillGeometry(star, ThemeColor(255, 242, 199, (byte)(flash * 245)));
        ds.FillEllipse(point, .8f / aspect, .8f, ThemeColor(255, 255, 239, (byte)(flash * 255)));
        ds.DrawLine(point + new Vector2(-2.6f / aspect, -2.6f), point + new Vector2(2.6f / aspect, 2.6f),
            ThemeColor(255, 213, 129, (byte)(flash * 95)), .45f);
    }
}
