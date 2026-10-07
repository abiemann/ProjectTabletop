using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using ProjectTabletop.App.Projection.WaterGarden;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // The title is painted on the sand above the far rim only after the camera
    // has settled. Segoe Script ships with Windows, so the installer does not
    // depend on a separately licensed or installed font asset.
    private DateTimeOffset _waterIntroStartedAt;
    private CanvasDevice? _waterTitleDevice;
    private float _waterTitleAspect;
    private CanvasGeometry? _waterTitleLeft, _waterTitleRight;
    private Rect _waterTitleLeftInk, _waterTitleRightInk;

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
        // The brief stagger makes the two words read as one handwritten title.
        DrawWaterGardenScriptWord(ds, _waterTitleLeft!, _waterTitleLeftInk,
            (float)Math.Clamp((elapsed - .12) / 1.65, 0, 1));
        DrawWaterGardenScriptWord(ds, _waterTitleRight!, _waterTitleRightInk,
            (float)Math.Clamp((elapsed - .54) / 1.95, 0, 1));
    }

    private void EnsureWaterGardenTitle(CanvasDevice device, float aspect)
    {
        if (_waterTitleLeft is not null && _waterTitleRight is not null &&
            _waterTitleDevice == device && _waterTitleAspect == aspect) return;
        DisposeWaterGardenTitle();
        _waterTitleLeft = CreateWaterGardenScriptWord(device, "Water", aspect, 180);
        _waterTitleRight = CreateWaterGardenScriptWord(device, "Garden", aspect, 820);
        _waterTitleLeftInk = _waterTitleLeft.ComputeBounds();
        _waterTitleRightInk = _waterTitleRight.ComputeBounds();
        _waterTitleDevice = device;
        _waterTitleAspect = aspect;
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

    private static void DrawWaterGardenScriptWord(CanvasDrawingSession ds, CanvasGeometry letters,
        Rect ink, float progress)
    {
        if (progress <= 0) return;
        if (progress < 1)
        {
            // A narrow slanted front exposes the connected script continuously,
            // like a pen moving through each letter, rather than fading a word in.
            float front = (float)(ink.Left + ink.Width * progress);
            float upper = Math.Max((float)ink.Left, front - 12);
            float lower = Math.Min((float)ink.Right, front + 12);
            using var reveal = CanvasGeometry.CreatePolygon(ds.Device,
            [
                new Vector2((float)ink.Left - 3, (float)ink.Top - 3),
                new Vector2(upper, (float)ink.Top - 3),
                new Vector2(lower, (float)ink.Bottom + 3),
                new Vector2((float)ink.Left - 3, (float)ink.Bottom + 3)
            ]);
            using var clip = ds.CreateLayer(1, reveal);
            PaintWaterGardenScriptInk(ds, letters);
        }
        else PaintWaterGardenScriptInk(ds, letters);
    }

    private static void PaintWaterGardenScriptInk(CanvasDrawingSession ds, CanvasGeometry letters)
    {
        // A restrained dark keyline keeps white lettering legible against the
        // pale sand, while the face of the script remains white.
        ds.DrawGeometry(letters, Color.FromArgb(135, 70, 65, 52), 2.8f);
        ds.FillGeometry(letters, Color.FromArgb(255, 255, 255, 255));
    }

    private void DisposeWaterGardenTitle()
    {
        _waterTitleLeft?.Dispose(); _waterTitleLeft = null;
        _waterTitleRight?.Dispose(); _waterTitleRight = null;
        _waterTitleDevice = null;
        _waterTitleAspect = 0;
        _waterTitleLeftInk = _waterTitleRightInk = default;
    }
}
