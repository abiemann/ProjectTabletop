using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // Geometry stays rooted on the parcel's district band. Only the newly
    // accepted shop (or replacement hall) grows; earlier shops never shuffle.
    private static void DrawCrownDeedBuildings(CanvasDrawingSession ds, float width, int houses,
        float progress, Color groupColor, double boardAspect)
    {
        if (houses <= 0 || !float.IsFinite(width) || width <= 0) return;
        progress = float.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 1;
        using var aspect = new MonopolyArtAspect(ds, new(width / 2, 11), boardAspect);
        float scale = Math.Min(1, (width - 8) / 52);
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(scale, new Vector2(width / 2, 15)) * previous;
        try
        {
            if (houses >= 5)
            {
                if (progress < .25f)
                {
                    using var previousShops = ds.CreateLayer(1 - progress / .25f);
                    for (int i = 0; i < 4; i++)
                        CrownDeedTownhouse(ds, new(width / 2 - 19.5f + i * 12.5f, 17 - (i % 2) * .8f),
                            9.3f, 12.5f + (i % 2) * 1.2f, 1, groupColor, false);
                }
                if (progress >= .10f)
                    CrownDeedTownhouse(ds, new(width / 2 - 1.4f, 17), 39, 25,
                        Math.Clamp((progress - .10f) / .90f, 0, 1), groupColor, true);
                return;
            }
            for (int i = 0; i < houses; i++)
                CrownDeedTownhouse(ds, new(width / 2 - 19.5f + i * 12.5f, 17 - (i % 2) * .8f),
                    9.3f, 12.5f + (i % 2) * 1.2f, i == houses - 1 ? progress : 1, groupColor, false);
        }
        finally { ds.Transform = previous; }
    }

    private static void CrownDeedTownhouse(CanvasDrawingSession ds, Vector2 baseCenter, float width,
        float fullHeight, float progress, Color district, bool hall)
    {
        float x = baseCenter.X - width / 2, bottom = baseCenter.Y;
        float height = fullHeight * progress, top = bottom - height;
        float depthX = hall ? 5 : 2.8f, depthY = hall ? -3.5f : -2.2f;
        var ink = ThemeColor(48, 43, 39);
        var stone = ThemeColor(225, 209, 170);
        var brightStone = ThemeColor(251, 237, 196);
        var roof = ThemeColor(49, 66, 73);
        var gold = ThemeColor(202, 159, 88);
        using (var shadow = new CanvasRadialGradientBrush(ds.Device,
            ThemeColor(16, 25, 23, 100), ThemeColor(16, 25, 23, 0))
            { Center = baseCenter + new Vector2(2, 1), RadiusX = width * .72f, RadiusY = hall ? 5 : 3.2f })
            ds.FillEllipse(baseCenter + new Vector2(2, 1), width * .72f, hall ? 5 : 3.2f, shadow);

        // Chamfered stone apron and steps establish a three-dimensional base
        // before the walls rise. There is no rectangular effect mask.
        CrownDeedFacet(ds, ThemeColor(129, 128, 112),
            new(x - 1.5f, bottom + 1), new(x + width + 1, bottom + 1),
            new(x + width + depthX + 1, bottom + depthY), new(x + depthX - 1, bottom + depthY - .8f));
        ds.DrawLine(x - 1, bottom + 1, x + width + 1, bottom + 1, brightStone, .6f);
        if (progress <= 0) return;

        using var facade = new CanvasLinearGradientBrush(ds.Device, brightStone, ThemeColor(162, 147, 118))
        { StartPoint = new(x, top), EndPoint = new(x + width * .85f, bottom) };
        ds.FillRectangle(new Rect(x, top, width, height), facade);
        CrownDeedFacet(ds, ThemeColor(112, 118, 108), new(x + width, top), new(x + width + depthX, top + depthY),
            new(x + width + depthX, bottom + depthY), new(x + width, bottom));
        ds.DrawLine(x + width, top, x + width, bottom, ink, .45f);
        ds.DrawLine(x, top, x, bottom, brightStone, .6f);

        // Floor positions follow the newly extruded walls. Masonry courses
        // become visible as space opens, rather than stretching a finished icon.
        int floors = hall ? 3 : 2;
        for (int floor = 1; floor < floors; floor++)
        {
            float y = bottom - height * floor / floors;
            ds.DrawLine(x + .4f, y, x + width - .4f, y, ThemeColor(100, 100, 85, 120), .45f);
            ds.DrawLine(x + .4f, y - .6f, x + width - .4f, y - .6f, ThemeColor(251, 238, 205, 150), .45f);
        }
        float windowReveal = CrownDeedDetailReveal(progress, .08f, .36f);
        if (windowReveal > 0)
        {
            // The silhouette itself acquires its warm glazing; settled parts
            // draw directly so existing shops retain their exact native pixels.
            using var windowLayer = windowReveal < 1 ? ds.CreateLayer(windowReveal) : null;
            float windowHeight = Math.Min(hall ? 3.4f : 2.35f, height / (floors + .8f));
            int columns = hall ? 7 : 2;
            float spacing = width / (columns + 1);
            for (int floor = 0; floor < floors; floor++)
                for (int column = 1; column <= columns; column++)
                {
                    if (floor == 0 && (hall ? column == 4 : column == 1)) continue;
                    float wx = x + spacing * column, wy = bottom - (floor + .67f) * height / floors;
                    float ww = hall ? 2.5f : 1.55f;
                    ds.FillRoundedRectangle(new Rect(wx - ww / 2, wy - windowHeight, ww, windowHeight),
                        .35f, .35f, ink);
                    ds.FillRectangle(new Rect(wx - ww / 2 + .3f, wy - windowHeight + .35f,
                        Math.Max(.3f, ww - .6f), Math.Max(.25f, windowHeight - .7f)), ThemeColor(255, 202, 98));
                    ds.DrawLine(wx - ww / 2 - .35f, wy + .4f, wx + ww / 2 + .35f, wy + .4f, stone, .6f);
                    if (hall) ds.DrawLine(wx, wy - windowHeight, wx, wy, gold, .3f);
                }
            float doorWidth = hall ? 4 : 2.5f, doorHeight = Math.Min(hall ? 7 : 4.4f, height * .65f);
            float doorX = hall ? baseCenter.X : x + width * .32f;
            ds.FillRoundedRectangle(new Rect(doorX - doorWidth / 2, bottom - doorHeight, doorWidth, doorHeight),
                doorWidth / 2, doorWidth / 2, ThemeColor(50, 63, 56));
            ds.DrawRoundedRectangle(new Rect(doorX - doorWidth / 2, bottom - doorHeight, doorWidth, doorHeight),
                doorWidth / 2, doorWidth / 2, gold, .5f);
            ds.FillCircle(new(doorX + doorWidth * .20f, bottom - doorHeight * .42f), .32f, brightStone);
        }

        float roofHeight = (hall ? 6.3f : 4.4f) * progress;
        float ridgeY = top - roofHeight;
        float inset = hall ? 3.7f : 1.8f;
        // A shallow mansard has distinct lit and shaded planes, a narrow ridge,
        // slate seams and stone cornices instead of a flat colored triangle.
        CrownDeedFacet(ds, roof, new(x - .9f, top), new(x + width + .9f, top),
            new(x + width - inset, ridgeY), new(x + inset, ridgeY));
        CrownDeedFacet(ds, ThemeColor(30, 45, 52), new(x + width + .9f, top),
            new(x + width + depthX + .8f, top + depthY), new(x + width + depthX - inset, ridgeY + depthY),
            new(x + width - inset, ridgeY));
        CrownDeedFacet(ds, ThemeColor(105, 127, 127), new(x + inset, ridgeY),
            new(x + width - inset, ridgeY), new(x + width + depthX - inset, ridgeY + depthY),
            new(x + inset + depthX, ridgeY + depthY));
        for (int seam = 1; seam < (hall ? 7 : 3); seam++)
        {
            float u = seam / (hall ? 7f : 3f);
            ds.DrawLine(x + width * u, top - .4f, x + inset + (width - inset * 2) * u, ridgeY + .4f,
                ThemeColor(142, 158, 146, 150), .35f);
        }
        ds.DrawLine(x - 1, top + .2f, x + width + .9f, top + .2f, brightStone, hall ? 1.2f : .85f);
        ds.DrawLine(x + width + .9f, top + .2f, x + width + depthX + .8f, top + depthY + .2f, gold, .65f);
        ds.DrawLine(x + inset, ridgeY - .2f, x + width - inset, ridgeY - .2f, ThemeColor(197, 185, 140), .6f);

        // Small streetfront awnings carry the district identity without
        // repainting the building itself in board-game primary colors.
        float awningReveal = CrownDeedDetailReveal(progress, .20f, .55f);
        if (awningReveal > 0)
        {
            using var awningLayer = awningReveal < 1 ? ds.CreateLayer(awningReveal) : null;
            float awningY = bottom - Math.Min(height * .4f, hall ? 7.5f : 4.7f);
            float ax = x + width * .13f, aw = width * .74f;
            CrownDeedFacet(ds, district, new(ax, awningY), new(ax + aw, awningY),
                new(ax + aw + .45f, awningY + 1.4f), new(ax - .45f, awningY + 1.4f));
            ds.DrawLine(ax - .45f, awningY + 1.4f, ax + aw + .45f, awningY + 1.4f, brightStone, .45f);
        }
        float chimneyX = x + width * .77f;
        float chimneyTop = ridgeY + depthY - 2.3f * progress;
        float upperReveal = CrownDeedDetailReveal(progress, 0, .30f);
        using var upperLayer = upperReveal < 1 ? ds.CreateLayer(upperReveal) : null;
        ds.FillRectangle(new Rect(chimneyX, chimneyTop, hall ? 2.6f : 1.4f, 3.2f * progress), stone);
        ds.DrawLine(chimneyX - .4f, chimneyTop, chimneyX + (hall ? 3 : 1.8f), chimneyTop, ink, .55f);

        if (hall)
        {
            float cx = baseCenter.X, cy = ridgeY - 1.7f * progress;
            ds.FillRectangle(new Rect(cx - 3, cy - 3.5f * progress, 6, 5 * progress), stone);
            ds.FillCircle(new(cx, cy - 1.2f * progress), 1.8f * progress, ink);
            ds.DrawCircle(new(cx, cy - 1.2f * progress), 1.8f * progress, gold, .5f);
            ds.DrawLine(cx, cy - 1.2f * progress, cx, cy - 2.4f * progress, brightStone, .45f);
            ds.DrawLine(cx, cy - 1.2f * progress, cx + .9f * progress, cy - .8f * progress, brightStone, .45f);
            CrownDeedFacet(ds, ThemeColor(67, 95, 91), new(cx - 4, cy - 3.5f * progress),
                new(cx, cy - 7.8f * progress), new(cx + 4, cy - 3.5f * progress));
            ds.DrawLine(cx, cy - 7.8f * progress, cx, cy - 10 * progress, gold, .7f);
            ds.FillCircle(new(cx, cy - 10 * progress), .8f * progress, brightStone);
        }
    }

    private static float CrownDeedDetailReveal(float progress, float start, float finish)
    {
        float t = Math.Clamp((progress - start) / (finish - start), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static void CrownDeedFacet(CanvasDrawingSession ds, Color color, params Vector2[] vertices)
    {
        using var shape = CanvasGeometry.CreatePolygon(ds.Device, vertices);
        ds.FillGeometry(shape, color);
    }
}
