using System.Numerics;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.Vision;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // Both boards draw the detector's stationary reference. Orientation belongs
    // to the caller's transform; the dot layout and optical witnesses stay shared.
    private static void DrawPatternRail(CanvasDrawingSession drawing, Rect rectangle)
    {
        drawing.FillRectangle(rectangle, Color.FromArgb(255, 0, 0, 0));
        float radiusX = (float)rectangle.Width * PatternRailGeometry.DotRadius / PatternRailGeometry.ReferenceWidth;
        float radiusY = (float)rectangle.Height * PatternRailGeometry.DotRadius / PatternRailGeometry.ReferenceHeight;
        for (int column = 0; column < PatternRailGeometry.Columns; column++)
        {
            float x = (float)(rectangle.X + rectangle.Width * PatternRailGeometry.ColumnCenter(column) /
                PatternRailGeometry.ReferenceWidth);
            drawing.FillEllipse(new Vector2(x, (float)(rectangle.Y + rectangle.Height * PatternRailGeometry.DotY1ForColumn(column) /
                PatternRailGeometry.ReferenceHeight)), radiusX, radiusY, Color.FromArgb(255, 255, 255, 255));
            drawing.FillEllipse(new Vector2(x, (float)(rectangle.Y + rectangle.Height * PatternRailGeometry.DotY2ForColumn(column) /
                PatternRailGeometry.ReferenceHeight)), radiusX, radiusY, Color.FromArgb(255, 255, 255, 255));
        }
    }
}
