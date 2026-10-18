using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Windows.UI;

namespace ProjectTabletop.App.Projection.GlobeRendering;

/// <summary>Stationary fine stars rasterized once at the sky cache's native resolution.</summary>
internal static class GlobeStarField
{
    internal static int Draw(CanvasDrawingSession drawing, int width, int height, double aspect)
    {
        // One physical unit is the board's short edge. Raster reservations can
        // have a different aspect, so each axis needs its own pixels-per-unit.
        float physicalWidth = (float)Math.Max(1, aspect);
        float physicalHeight = (float)Math.Max(1, 1 / aspect);
        float xScale = width / physicalWidth, yScale = height / physicalHeight;
        float area = physicalWidth * physicalHeight;
        var random = new Random(0x474C4F42);
        using var brush = new CanvasSolidColorBrush(drawing.Device, Color.FromArgb(0, 0, 0, 0));
        var previousBlend = drawing.Blend;
        int count = 0;
        try
        {
            // Add light rather than replacing bright nebula pixels with a
            // faint star's darker colour. Positions never depend on the clock,
            // zoom or preview resolution, preserving the swipe reference.
            drawing.Blend = CanvasBlend.Add;
            int scatteredCount = (int)Math.Round(14000 * area);
            for (int i = 0; i < scatteredCount; i++)
                Star(Next() * physicalWidth, Next() * physicalHeight);

            // Broad, differently shaped associations add natural density
            // variation without arranging stars into rows or repeating tiles.
            int associations = (int)Math.Ceiling(11 * area);
            for (int association = 0; association < associations; association++)
            {
                float centerX = Next() * physicalWidth, centerY = Next() * physicalHeight;
                float spread = .065f + Next() * .15f;
                float squash = .35f + Next() * .65f;
                float angle = Next() * MathF.Tau;
                float cosine = MathF.Cos(angle), sine = MathF.Sin(angle);
                int members = 75 + random.Next(100);
                for (int i = 0; i < members; i++)
                {
                    float radius = MathF.Sqrt(-2 * MathF.Log(Math.Max(.000001f, Next()))) * spread;
                    float direction = Next() * MathF.Tau;
                    float x = radius * MathF.Cos(direction);
                    float y = radius * MathF.Sin(direction) * squash;
                    Star(centerX + x * cosine - y * sine, centerY + x * sine + y * cosine);
                }
            }
        }
        finally
        {
            drawing.Blend = previousBlend;
        }
        return count;

        float Next() => random.NextSingle();

        void Star(float x, float y)
        {
            // Consume the same appearance samples even for clipped stars, so
            // edge clipping cannot change the remaining star catalogue.
            float size = Next(), brightness = Next(), temperature = Next();
            float prominence = Next();
            if (x < 0 || x >= physicalWidth || y < 0 || y >= physicalHeight) return;
            var center = new Vector2(x * xScale, y * yScale);
            // Most cores span 0.8–2 pixels at a 2160-pixel short edge. A few
            // brighter points remain legible after camera downsampling, while
            // the fine majority resolves new detail at the projector's pixels.
            float radius = (.4f + .6f * size) / 2160;
            byte alpha = (byte)(70 + 185 * brightness * brightness);
            if (prominence > .96f)
            {
                radius = (.9f + .7f * size) / 2160;
                alpha = (byte)(195 + 60 * brightness);
            }
            byte red = temperature < .70f ? (byte)(205 + 50 * temperature / .70f) : (byte)255;
            byte green = temperature < .70f ? (byte)(225 + 30 * temperature / .70f)
                : (byte)(255 - 22 * (temperature - .70f) / .30f);
            byte blue = temperature < .70f ? (byte)255
                : (byte)(255 - 65 * (temperature - .70f) / .30f);
            if (prominence > .992f)
            {
                brush.Color = Color.FromArgb(5, red, green, blue);
                drawing.FillEllipse(center, radius * xScale * 3.3f, radius * yScale * 3.3f, brush);
                brush.Color = Color.FromArgb(12, red, green, blue);
                drawing.FillEllipse(center, radius * xScale * 1.9f, radius * yScale * 1.9f, brush);
            }
            brush.Color = Color.FromArgb(alpha, red, green, blue);
            drawing.FillEllipse(center, radius * xScale, radius * yScale, brush);
            count++;
        }
    }
}
