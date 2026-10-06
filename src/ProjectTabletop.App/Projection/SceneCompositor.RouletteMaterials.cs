using System.Numerics;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection.RouletteRendering;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private CanvasDevice? _rouletteMaterialDevice;
    private CanvasBitmap? _rouletteBurlBitmap;
    private PixelShaderEffect<RouletteWoodShader>? _rouletteWoodShader;

    private void DrawRouletteBurlSurface(CanvasDrawingSession ds, bool cone, double wheelAngle = 0, bool nearSideOnly = false)
    {
        if (!PrepareRouletteResources(ds.Device)) return;
        var burl = _rouletteBurlBitmap!;
        _rouletteWoodShader ??= new PixelShaderEffect<RouletteWoodShader>();
        _rouletteWoodShader.Sources[0] = burl;
        double angle = double.IsFinite(wheelAngle) ? wheelAngle % Math.Tau : 0;
        var transform = ds.Transform;
        float xScale = new Vector2(transform.M11, transform.M12).Length();
        float yScale = new Vector2(transform.M21, transform.M22).Length();
        float edgeWidth = .70f / Math.Max(1, Math.Min(xScale, yScale));
        _rouletteWoodShader.ConstantBuffer = new RouletteWoodShader(
            new Float2((float)burl.Size.Width, (float)burl.Size.Height),
            new Float2((float)Math.Cos(angle), (float)Math.Sin(angle)), cone ? 1 : 0, nearSideOnly ? 1 : 0, edgeWidth);
        // Direct2D evaluates at the destination's native raster. In the fixed
        // bowl cache its existing 4× transform supplies 1600px quality; the moving
        // cone uses the board's physical raster without a per-frame bitmap.
        ds.DrawImage(_rouletteWoodShader, new Rect(0, 0, 400, 400), new Rect(0, 0, 400, 400),
            1, CanvasImageInterpolation.HighQualityCubic);
    }

    private void DisposeRouletteMaterials()
    {
        _rouletteWoodShader?.Dispose(); _rouletteWoodShader = null;
        _rouletteBurlBitmap = null;
        _rouletteMaterialDevice = null;
    }
}
