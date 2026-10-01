using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // Five continuous lacquered drums: their color and fine engraved seams
    // belong to the static cabinet, beneath the independently moving symbols.
    private static void DrawSlotReelBed(CanvasDrawingSession ds, SlotSnapshot game, SlotLayout layout)
    {
        var window = layout.Window;
        bool ruby = game.InRespins;
        using var clip = CanvasGeometry.CreateRectangle(ds.Device, window);
        using var layer = ds.CreateLayer(1, clip);
        using var lacquer = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ruby ? ThemeColor(64, 7, 25) : ThemeColor(13, 22, 81) },
            new() { Position = .20f, Color = ruby ? ThemeColor(118, 18, 41) : ThemeColor(28, 42, 127) },
            new() { Position = .48f, Color = ruby ? ThemeColor(146, 26, 47) : ThemeColor(39, 56, 153) },
            new() { Position = .76f, Color = ruby ? ThemeColor(113, 15, 37) : ThemeColor(27, 38, 122) },
            new() { Position = 1, Color = ruby ? ThemeColor(63, 7, 24) : ThemeColor(12, 19, 69) }
        ]) { StartPoint = new(0, layout.Top), EndPoint = new(0, layout.Bottom) };
        ds.FillRectangle(window, lacquer);

        for (int reel = 0; reel < SlotGame.Reels; reel++)
        {
            float left = layout.Left + reel * layout.CellWidth;
            var drum = new Rect(left, layout.Top, layout.CellWidth, layout.Height);
            // Broad center reflection and shaded edges give each uninterrupted
            // strip a cylindrical surface without drawing rectangular cells.
            using var roundness = new CanvasLinearGradientBrush(ds.Device,
            [
                new() { Position = 0, Color = ThemeColor(2, 2, 12, 135) },
                new() { Position = .10f, Color = ThemeColor(3, 4, 15, 46) },
                new() { Position = .36f, Color = ruby ? ThemeColor(255, 153, 103, 13) : ThemeColor(154, 184, 255, 14) },
                new() { Position = .61f, Color = ruby ? ThemeColor(255, 196, 139, 27) : ThemeColor(142, 173, 255, 27) },
                new() { Position = .86f, Color = ThemeColor(2, 3, 17, 40) },
                new() { Position = 1, Color = ThemeColor(2, 2, 13, 138) }
            ]) { StartPoint = new(left, 0), EndPoint = new(left + layout.CellWidth, 0) };
            ds.FillRectangle(drum, roundness);
            DrawSlotDrumEngraving(ds, layout, reel, ruby);
        }

        // Soft occlusion at the axle recesses: no horizontal row boundaries.
        using var recess = new CanvasLinearGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = ThemeColor(0, 0, 6, 132) },
            new() { Position = .10f, Color = ThemeColor(0, 0, 6, 20) },
            new() { Position = .30f, Color = ThemeColor(0, 0, 6, 0) },
            new() { Position = .75f, Color = ThemeColor(0, 0, 6, 0) },
            new() { Position = .94f, Color = ThemeColor(0, 0, 6, 42) },
            new() { Position = 1, Color = ThemeColor(0, 0, 6, 160) }
        ]) { StartPoint = new(0, layout.Top), EndPoint = new(0, layout.Bottom) };
        ds.FillRectangle(window, recess);
        for (int reel = 1; reel < SlotGame.Reels; reel++)
        {
            float x = layout.Left + reel * layout.CellWidth;
            float unit = 1 / layout.Aspect;
            ds.DrawLine(x - unit, layout.Top, x - unit, layout.Bottom, ThemeColor(2, 3, 10, 225), 2.3f * unit);
            ds.DrawLine(x, layout.Top, x, layout.Bottom, ThemeColor(163, 110, 49, 185), .9f * unit);
            ds.DrawLine(x + .65f * unit, layout.Top, x + .65f * unit, layout.Bottom, ThemeColor(255, 224, 159, 135), .55f * unit);
            ds.DrawLine(x + 1.35f * unit, layout.Top, x + 1.35f * unit, layout.Bottom, ThemeColor(19, 10, 10, 100), .5f * unit);
        }
    }

    private static void DrawSlotDrumEngraving(CanvasDrawingSession ds, SlotLayout layout, int reel, bool ruby)
    {
        float center = layout.ReelCenter(reel);
        float span = Math.Min(23, layout.CellWidth * layout.Aspect * .24f);
        Color ink = ruby ? ThemeColor(255, 185, 128, 13) : ThemeColor(167, 190, 255, 13);
        foreach (float fraction in new[] { .16f, .50f, .84f })
        {
            float y = layout.Top + layout.Height * fraction;
            using var vine = new CanvasPathBuilder(ds.Device);
            // Paired fine scrolls describe an etched brocade, quiet enough that
            // ordinary symbols remain the only high-contrast marks on a reel.
            foreach (int side in new[] { -1, 1 })
            {
                Vector2 Point(float x, float dy) => new(center + x * side / layout.Aspect, y + dy);
                vine.BeginFigure(Point(0, -27));
                vine.AddCubicBezier(Point(span * .10f, -12), Point(span, -20), Point(span, -4));
                vine.AddCubicBezier(Point(span, 8), Point(span * .20f, 10), Point(span * .22f, 0));
                vine.AddCubicBezier(Point(span * .23f, -7), Point(span * .66f, -5), Point(span * .56f, 1));
                vine.EndFigure(CanvasFigureLoop.Open);
                vine.BeginFigure(Point(0, 27));
                vine.AddCubicBezier(Point(span * .10f, 12), Point(span, 20), Point(span, 4));
                vine.EndFigure(CanvasFigureLoop.Open);
            }
            using var engraving = CanvasGeometry.CreatePath(vine);
            ds.DrawGeometry(engraving, ink, .55f / MathF.Sqrt(layout.Aspect));
        }
    }

    private static void DrawSlotReelJewelry(CanvasDrawingSession ds, SlotLayout layout, bool ruby)
    {
        Color jewel = ruby ? ThemeColor(182, 21, 43) : ThemeColor(36, 83, 173);
        foreach (float y in new[] { layout.Top - 9, layout.Bottom + 9 })
            for (int seam = 1; seam < SlotGame.Reels; seam++)
            {
                var center = new Vector2(layout.Left + seam * layout.CellWidth, y);
                var prior = ds.Transform;
                ds.Transform = Matrix3x2.CreateScale(1 / layout.Aspect, 1, center) * prior;
                try
                {
                    // Inset gemstones and symmetric curls decorate the metal
                    // rail; all detail remains inside the existing frame band.
                    ds.FillEllipse(center + new Vector2(0, .8f), 6.8f, 5.2f, ThemeColor(31, 15, 8));
                    ds.FillEllipse(center, 6.2f, 4.6f, ThemeColor(239, 194, 101));
                    if (ruby)
                    {
                        ds.FillEllipse(center, 4.5f, 3.1f, jewel);
                        ds.DrawEllipse(center, 4.7f, 3.25f, ThemeColor(102, 56, 18), .65f);
                        ds.DrawLine(center + new Vector2(-2.9f, -1.5f), center + new Vector2(.9f, -1.8f),
                            ThemeColor(255, 246, 197, 210), .75f);
                    }
                    else DrawSlotFrameSapphire(ds, center);
                    foreach (int side in new[] { -1, 1 })
                    {
                        using var curl = new CanvasPathBuilder(ds.Device);
                        curl.BeginFigure(center + new Vector2(side * 7, 0));
                        curl.AddCubicBezier(center + new Vector2(side * 12, -4), center + new Vector2(side * 21, -4),
                            center + new Vector2(side * 23, 0));
                        curl.AddCubicBezier(center + new Vector2(side * 18, 3), center + new Vector2(side * 13, 3),
                            center + new Vector2(side * 15, -.5f));
                        curl.EndFigure(CanvasFigureLoop.Open);
                        using var line = CanvasGeometry.CreatePath(curl);
                        ds.DrawGeometry(line, ThemeColor(87, 48, 20), 1.5f);
                        ds.DrawGeometry(line, ThemeColor(255, 225, 153, 215), .55f);
                    }
                }
                finally { ds.Transform = prior; }
            }
    }

    private static readonly Color[] SlotSapphireCrown =
    [
        ThemeColor(53, 122, 217), ThemeColor(5, 28, 102), ThemeColor(12, 58, 158), ThemeColor(6, 17, 68),
        ThemeColor(16, 64, 162), ThemeColor(66, 167, 235), ThemeColor(135, 218, 255), ThemeColor(38, 97, 189)
    ];
    private static readonly Color[] SlotSapphireReflections =
    [
        ThemeColor(16, 67, 171), ThemeColor(29, 113, 211), ThemeColor(7, 21, 67), ThemeColor(41, 88, 189),
        ThemeColor(4, 19, 73), ThemeColor(15, 51, 145), ThemeColor(64, 155, 224), ThemeColor(192, 237, 255)
    ];
    private static readonly Color[] SlotSapphireTable =
    [
        ThemeColor(11, 48, 122), ThemeColor(29, 97, 194), ThemeColor(10, 34, 93), ThemeColor(4, 13, 47),
        ThemeColor(29, 91, 183), ThemeColor(67, 178, 234), ThemeColor(21, 82, 175), ThemeColor(6, 24, 83)
    ];

    private static void DrawSlotFrameSapphire(CanvasDrawingSession ds, Vector2 center)
    {
        // A brilliant oval cut: sixteen girdle edges meet eight table corners,
        // with split crown facets reflecting light through the dark blue core.
        // The caller already supplies physical coordinates; the gold setting
        // and this static detail stay inside the original frame footprint.
        Span<Vector2> girdle = stackalloc Vector2[16];
        Span<Vector2> table = stackalloc Vector2[8];
        var tableCenter = center + new Vector2(-.2f, -.12f);
        for (int edge = 0; edge < girdle.Length; edge++)
        {
            float angle = -MathF.PI / 2 + edge * MathF.Tau / girdle.Length;
            girdle[edge] = center + new Vector2(4.65f * MathF.Cos(angle), 3.2f * MathF.Sin(angle));
        }
        for (int corner = 0; corner < table.Length; corner++)
        {
            float angle = -MathF.PI / 2 + corner * MathF.Tau / table.Length;
            table[corner] = tableCenter + new Vector2(2.25f * MathF.Cos(angle), 1.55f * MathF.Sin(angle));
        }
        ds.FillEllipse(center, 4.8f, 3.35f, ThemeColor(4, 13, 39));
        for (int sector = 0; sector < table.Length; sector++)
        {
            int next = (sector + 1) % table.Length;
            var start = girdle[sector * 2];
            var middle = girdle[sector * 2 + 1];
            var end = girdle[(sector * 2 + 2) % girdle.Length];
            using var crownA = CanvasGeometry.CreatePolygon(ds.Device, [start, middle, table[sector]]);
            using var crownB = CanvasGeometry.CreatePolygon(ds.Device, [middle, end, table[next]]);
            using var reflection = CanvasGeometry.CreatePolygon(ds.Device, [middle, table[next], table[sector]]);
            using var core = CanvasGeometry.CreatePolygon(ds.Device, [tableCenter, table[sector], table[next]]);
            ds.FillGeometry(crownA, SlotSapphireCrown[sector]);
            ds.FillGeometry(crownB, SlotSapphireCrown[next]);
            ds.FillGeometry(reflection, SlotSapphireReflections[sector]);
            ds.FillGeometry(core, SlotSapphireTable[sector]);
        }
        ds.DrawEllipse(center, 4.85f, 3.4f, ThemeColor(102, 56, 18), .6f);
        ds.DrawLine(center + new Vector2(-3.25f, -1.6f), center + new Vector2(-1.8f, -2.75f),
            ThemeColor(212, 244, 255, 220), .45f);
        var glint = center + new Vector2(-2.6f, -1.85f);
        ds.DrawLine(glint - new Vector2(.65f, 0), glint + new Vector2(.65f, 0), ThemeColor(245, 253, 255), .45f);
        ds.DrawLine(glint - new Vector2(0, .55f), glint + new Vector2(0, .55f), ThemeColor(245, 253, 255), .45f);
    }

    // A held prize sits in an engraved gold bezel rather than a rounded tile.
    // Rooted in cell geometry, this stays circular at every board aspect and
    // automatically becomes smaller when Expand exposes five rows.
    private static void DrawSlotPrizeSocket(CanvasDrawingSession ds, Rect cell, SlotLayout layout, float strength)
    {
        strength = Math.Clamp(strength, 0, 1);
        var center = new Vector2((float)(cell.X + cell.Width / 2), (float)(cell.Y + cell.Height / 2));
        float radius = (float)Math.Min(cell.Height, cell.Width * layout.Aspect) * .44f;
        if (radius <= 2) return;
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(1 / layout.Aspect, 1, center) * previous;
        try
        {
            using (var halo = new CanvasRadialGradientBrush(ds.Device, ThemeColor(255, 166, 52, (byte)(25 + strength * 65)),
                ThemeColor(255, 113, 24, 0)) { Center = center, RadiusX = radius * 1.20f, RadiusY = radius * 1.20f })
                ds.FillCircle(center, radius * 1.20f, halo);
            ds.FillCircle(center + new Vector2(0, radius * .055f), radius * 1.015f, ThemeColor(20, 3, 7, 210));
            using var metal = new CanvasLinearGradientBrush(ds.Device,
            [
                new() { Position = 0, Color = ThemeColor(255, 236, 171) },
                new() { Position = .18f, Color = ThemeColor(202, 140, 52) },
                new() { Position = .36f, Color = ThemeColor(255, 222, 123) },
                new() { Position = .52f, Color = ThemeColor(133, 73, 22) },
                new() { Position = .76f, Color = ThemeColor(223, 167, 73) },
                new() { Position = 1, Color = ThemeColor(255, 224, 139) }
            ]) { StartPoint = center - new Vector2(radius * .65f, radius), EndPoint = center + new Vector2(radius * .6f, radius) };
            ds.FillCircle(center, radius, metal);
            ds.DrawCircle(center, radius, ThemeColor(61, 28, 9), Math.Max(.65f, radius * .015f));
            ds.DrawCircle(center, radius * .958f, ThemeColor(255, 241, 181, 200), Math.Max(.55f, radius * .013f));
            using var well = new CanvasRadialGradientBrush(ds.Device,
                ThemeColor(114, 41, 15), ThemeColor(47, 12, 12))
            { Center = center - new Vector2(radius * .15f, radius * .22f), RadiusX = radius, RadiusY = radius };
            ds.FillCircle(center, radius * .866f, well);
            ds.DrawCircle(center, radius * .874f, ThemeColor(82, 38, 13), Math.Max(.65f, radius * .02f));
            ds.DrawCircle(center, radius * .842f, ThemeColor(244, 187, 86, 150), .6f);
            for (int index = 0; index < 40; index++)
            {
                float angle = index * MathF.Tau / 40;
                var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                float inner = index % 5 == 0 ? .886f : .909f;
                ds.DrawLine(center + direction * radius * inner, center + direction * radius * .942f,
                    ThemeColor(89, 44, 14, 190), Math.Max(.55f, radius * .012f));
            }
            if (strength > .05f)
            {
                var point = center + new Vector2(-radius * .60f, -radius * .72f);
                float arm = 2 + strength * radius * .11f;
                ds.DrawLine(point - new Vector2(arm, 0), point + new Vector2(arm, 0),
                    ThemeColor(255, 248, 209, (byte)(strength * 210)), .8f);
                ds.DrawLine(point - new Vector2(0, arm), point + new Vector2(0, arm),
                    ThemeColor(255, 248, 209, (byte)(strength * 210)), .8f);
            }
        }
        finally { ds.Transform = previous; }
    }
}
