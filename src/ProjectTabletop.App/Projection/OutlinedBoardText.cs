using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

// A reusable, cached glyph outline for text over detailed board scenery.
// The owner retains the format and disposes this cache on device/asset release.
internal sealed class OutlinedBoardText(CanvasTextFormat format, Color faceColor) : IDisposable
{
    private CanvasGeometry? _letters;
    private string? _text;
    private double _width, _height, _aspect;

    public void Draw(CanvasDrawingSession drawing, string text, Rect bounds, double aspect,
        float opacity = 1)
    {
        if (opacity <= 0) return;
        if (_letters is null || _text != text || _width != bounds.Width ||
            _height != bounds.Height || _aspect != aspect)
        {
            float size = format.FontSize;
            try
            {
                format.FontSize = size * (float)Math.Min(1, aspect);
                using var layout = new CanvasTextLayout(drawing.Device, text, format,
                    (float)(bounds.Width * aspect), (float)bounds.Height);
                var letters = CanvasGeometry.CreateText(layout);
                _letters?.Dispose();
                _letters = letters;
                _text = text;
                _width = bounds.Width; _height = bounds.Height; _aspect = aspect;
            }
            finally { format.FontSize = size; }
        }
        var previous = drawing.Transform;
        try
        {
            drawing.Transform = Matrix3x2.CreateScale((float)(1 / aspect), 1) *
                Matrix3x2.CreateTranslation((float)bounds.X, (float)bounds.Y) * previous;
            // Stroke is centred on the glyph edge; the face fill covers its
            // inner half. Keep the visible black rim two raster pixels wide.
            float scale = new Vector2(drawing.Transform.M21, drawing.Transform.M22).Length() * drawing.Dpi / 96;
            byte alpha = (byte)Math.Clamp((int)Math.Round(opacity * 255), 0, 255);
            drawing.DrawGeometry(_letters, Color.FromArgb(alpha, 0, 0, 0), 4 / Math.Max(.001f, scale));
            drawing.FillGeometry(_letters, Color.FromArgb((byte)(alpha * faceColor.A / 255),
                faceColor.R, faceColor.G, faceColor.B));
        }
        finally { drawing.Transform = previous; }
    }

    public void Dispose()
    {
        _letters?.Dispose();
        _letters = null;
    }
}
