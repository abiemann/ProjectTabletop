using System.Numerics;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using ProjectTabletop.App.Projection.WaterGarden;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // Script outlines define shallow trenches in the sand after the camera
    // settles. Cache their grain and lighting at the board's native density;
    // drawing the title never copies the live scene or reads pixels back.
    private DateTimeOffset _waterIntroStartedAt;
    private CanvasDevice? _waterTitleDevice;
    private float _waterTitleAspect;
    private CanvasRenderTarget? _waterTitleLeft, _waterTitleRight;
    private Rect _waterTitleLeftBounds, _waterTitleRightBounds;
    private Vector2 _waterTitleRasterScale;

    private void ResetWaterGardenIntro(DateTimeOffset now)
    {
        _waterIntroStartedAt = now;
        _waterRenderedFrame = null;
        _waterVisualRevision++;
    }

    private void DrawWaterGardenTitle(CanvasDrawingSession ds, DateTimeOffset now)
    {
        if (_waterIntroStartedAt == default) return;
        double elapsed = (now - _waterIntroStartedAt).TotalSeconds - WaterGardenView.EntranceDurationSeconds;
        if (elapsed <= 0) return;

        float aspect = (float)PaintBoardAspect();
        EnsureWaterGardenTitle(ds.Device, aspect);
        // Finish Water, briefly lift the imaginary stick, then write Garden.
        DrawWaterGardenScriptWord(ds, _waterTitleLeft!, _waterTitleLeftBounds,
            (float)Math.Clamp((elapsed - .12) / 1.65, 0, 1));
        DrawWaterGardenScriptWord(ds, _waterTitleRight!, _waterTitleRightBounds,
            (float)Math.Clamp((elapsed - 1.95) / 1.95, 0, 1));
    }

    private void EnsureWaterGardenTitle(CanvasDevice device, float aspect)
    {
        var size = _waterFrameTarget!.SizeInPixels;
        var rasterScale = new Vector2(size.Width / (float)BoardSurfaceSize,
            size.Height / (float)BoardSurfaceSize);
        if (_waterTitleLeft is not null && _waterTitleRight is not null &&
            _waterTitleDevice == device && _waterTitleAspect == aspect &&
            _waterTitleRasterScale == rasterScale) return;
        DisposeWaterGardenTitle();
        _waterTitleLeft = CreateWaterGardenSandWord(device, "Water", aspect, 180, rasterScale,
            out _waterTitleLeftBounds);
        _waterTitleRight = CreateWaterGardenSandWord(device, "Garden", aspect, 820, rasterScale,
            out _waterTitleRightBounds);
        _waterTitleDevice = device;
        _waterTitleAspect = aspect;
        _waterTitleRasterScale = rasterScale;
    }

    private static CanvasRenderTarget CreateWaterGardenSandWord(CanvasDevice device, string word,
        float aspect, float centerX, Vector2 rasterScale, out Rect bounds)
    {
        using var letters = CreateWaterGardenScriptWord(device, word, aspect, centerX);
        var ink = letters.ComputeBounds();
        const float padding = 5;
        bounds = new Rect(ink.X - padding / aspect, ink.Y - padding,
            ink.Width + padding * 2 / aspect, ink.Height + padding * 2);
        int width = Math.Max(1, (int)Math.Ceiling(bounds.Width * rasterScale.X));
        int height = Math.Max(1, (int)Math.Ceiling(bounds.Height * rasterScale.Y));
        using var mask = new CanvasRenderTarget(device, width, height, 96);
        using (var drawing = mask.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            drawing.Transform = Matrix3x2.CreateTranslation(-(float)bounds.X, -(float)bounds.Y) *
                Matrix3x2.CreateScale(rasterScale);
            drawing.FillGeometry(letters, Color.FromArgb(255, 255, 255, 255));
        }
        using var rounded = new GaussianBlurEffect
        {
            Source = mask, BlurAmount = .8f * rasterScale.Y, BorderMode = EffectBorderMode.Soft
        };
        using var groove = new PixelShaderEffect<SandWritingShader>();
        groove.Sources[0] = rounded;
        groove.ConstantBuffer = new SandWritingShader(rasterScale.Y);
        var shaded = new CanvasRenderTarget(device, width, height, 96);
        try
        {
            using var drawing = shaded.CreateDrawingSession();
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            drawing.DrawImage(groove, Vector2.Zero, new Rect(0, 0, width, height),
                1, CanvasImageInterpolation.Linear, CanvasComposite.Copy);
            return shaded;
        }
        catch { shaded.Dispose(); throw; }
    }

    private static CanvasGeometry CreateWaterGardenScriptWord(CanvasDevice device, string word,
        float aspect, float centerX)
    {
        using var format = new CanvasTextFormat
        {
            FontFamily = "Segoe Script", FontSize = 96, FontWeight = FontWeights.Normal,
            WordWrapping = CanvasWordWrapping.NoWrap
        };
        using var layout = new CanvasTextLayout(device, word, format, 700, 160);
        using var source = CanvasGeometry.CreateText(layout);
        var ink = source.ComputeBounds();
        // Scale uniformly in the physical projection. The board's normalized x
        // axis is stretched by its aspect when rasterized, so compensate here.
        float height = 83;
        float scale = Math.Min(height / (float)ink.Height,
            270 * aspect / (float)ink.Width);
        float scaleX = scale / aspect;
        float width = (float)ink.Width * scaleX;
        float x = centerX - width / 2 - (float)ink.X * scaleX;
        float y = 37 - (float)ink.Y * scale;
        return source.Transform(Matrix3x2.CreateScale(scaleX, scale) *
            Matrix3x2.CreateTranslation(x, y));
    }

    private static void DrawWaterGardenScriptWord(CanvasDrawingSession ds, CanvasRenderTarget letters,
        Rect bounds, float progress)
    {
        if (progress <= 0) return;
        if (progress < 1)
        {
            // A narrow slanted front exposes the connected script continuously,
            // like a stick moving through each letter, rather than fading a word in.
            // Sweep beyond both padded edges, so the final slanted corner and
            // its sandy lip are already revealed when the clip is removed.
            const float slant = 12;
            float front = (float)bounds.Left - slant + ((float)bounds.Width + 2 * slant) * progress;
            float upper = front - slant, lower = front + slant;
            using var reveal = CanvasGeometry.CreatePolygon(ds.Device,
            [
                new Vector2((float)bounds.Left, (float)bounds.Top),
                new Vector2(upper, (float)bounds.Top),
                new Vector2(lower, (float)bounds.Bottom),
                new Vector2((float)bounds.Left, (float)bounds.Bottom)
            ]);
            using var clip = ds.CreateLayer(1, reveal);
            Paint();
        }
        else Paint();
        void Paint() => ds.DrawImage(letters, bounds, new Rect(0, 0,
            letters.SizeInPixels.Width, letters.SizeInPixels.Height));
    }

    private void DisposeWaterGardenTitle()
    {
        _waterTitleLeft?.Dispose(); _waterTitleLeft = null;
        _waterTitleRight?.Dispose(); _waterTitleRight = null;
        _waterTitleDevice = null;
        _waterTitleAspect = 0;
        _waterTitleRasterScale = default;
        _waterTitleLeftBounds = _waterTitleRightBounds = default;
    }
}
