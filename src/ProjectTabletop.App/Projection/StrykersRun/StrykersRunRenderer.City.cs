using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection.StrykersRun;

internal sealed partial class StrykersRunRenderer
{
    private readonly PixelShaderEffect<StrykersRunCityShader> _city = new();

    // Both scenery layers use the interpolated road distance, so losing contact
    // and reaching a boss stop the city together with the road without a snap.
    internal void DrawCityBackground(CanvasDrawingSession ds, float distance)
    {
        var city = _images!.Image("city-ruins.png")!;
        _city.ConstantBuffer = new StrykersRunCityShader(
            new Float2((float)city.Size.Width, (float)city.Size.Height), distance);
        ds.DrawImage(_city, new Rect(0, 0, Width, Height),
            new Rect(0, 0, city.Size.Width, city.Size.Height));
    }
}
