using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private CanvasDevice? _duckHuntFeatherDevice;
    private CanvasCommandList[]? _duckHuntFeatherArtwork;

    public object DuckHuntFeatherStatus
    {
        get
        {
            lock (_gate)
            {
                var feathers = _boardSession.DuckHuntFeathers;
                return new { count = feathers.Count, animating = feathers.IsAnimating };
            }
        }
    }

    internal void DrawDuckHuntFeathers(CanvasDrawingSession drawing, double aspect)
    {
        var feathers = _boardSession.DuckHuntFeathers;
        if (feathers.Count == 0) return;
        PrepareDuckHuntFeathers(drawing.Device);
        var artwork = _duckHuntFeatherArtwork!;
        var previous = drawing.Transform;
        try
        {
            for (int index = 0; index < feathers.Count; index++)
            {
                var feather = feathers.GetPose(index);
                float scale = (float)feather.Length * BoardSurfaceSize / 100;
                // Rotate in physical space before compensating for the board's
                // independently normalized axes. Flutter narrows only the vane.
                drawing.Transform = Matrix3x2.CreateScale(scale * (float)feather.WidthScale, scale) *
                    Matrix3x2.CreateRotation((float)feather.Angle) *
                    Matrix3x2.CreateScale((float)(1 / aspect), 1) *
                    Matrix3x2.CreateTranslation((float)feather.U * BoardSurfaceSize,
                        (float)feather.V * BoardSurfaceSize) * previous;
                drawing.DrawImage(artwork[feather.Tint]);
            }
        }
        finally { drawing.Transform = previous; }
    }

    private void PrepareDuckHuntFeathers(CanvasDevice device)
    {
        if (_duckHuntFeatherDevice == device && _duckHuntFeatherArtwork is not null) return;
        DisposeDuckHuntFeathers();
        var artwork = new CanvasCommandList[3];
        try
        {
            using var vanePath = new CanvasPathBuilder(device);
            vanePath.BeginFigure(2.5f, -48);
            vanePath.AddCubicBezier(new(-8, -42), new(-15.5f, -24), new(-13.5f, -6));
            vanePath.AddLine(-10.4f, -11);
            vanePath.AddLine(-12.4f, -1);
            vanePath.AddCubicBezier(new(-11, 12), new(-6.4f, 23), new(-.5f, 31));
            AddFeatherOuterVane(vanePath);
            vanePath.EndFigure(CanvasFigureLoop.Closed);
            using var vane = CanvasGeometry.CreatePath(vanePath);

            using var lightPath = new CanvasPathBuilder(device);
            lightPath.BeginFigure(2.5f, -48);
            lightPath.AddCubicBezier(new(1, -28), new(-.2f, -4), new(-.5f, 31));
            AddFeatherOuterVane(lightPath);
            lightPath.EndFigure(CanvasFigureLoop.Closed);
            using var light = CanvasGeometry.CreatePath(lightPath);

            using var shaftPath = new CanvasPathBuilder(device);
            shaftPath.BeginFigure(.8f, 49);
            shaftPath.AddCubicBezier(new(-1.7f, 30), new(-.8f, -18), new(2.5f, -46));
            shaftPath.EndFigure(CanvasFigureLoop.Open);
            using var shaft = CanvasGeometry.CreatePath(shaftPath);

            using var barbPath = new CanvasPathBuilder(device);
            AddBarb(0, 22, -7.5f, 9); AddBarb(0, 20, 10, 5);
            AddBarb(0, 12, -10.5f, -4); AddBarb(0, 10, 12, -7);
            AddBarb(.2f, 2, -11.5f, -17); AddBarb(.2f, 0, 11, -20);
            AddBarb(.6f, -9, -10, -27); AddBarb(.6f, -11, 8.5f, -30);
            AddBarb(1, -20, -7, -35); AddBarb(1, -22, 5.8f, -39);
            AddBarb(1.6f, -31, -3.5f, -42); AddBarb(1.6f, -33, 3.6f, -44);
            using var barbs = CanvasGeometry.CreatePath(barbPath);

            for (int tint = 0; tint < artwork.Length; tint++)
            {
                artwork[tint] = new CanvasCommandList(device);
                using var drawing = artwork[tint].CreateDrawingSession();
                var palette = DuckHuntFeatherPalette(tint);
                drawing.FillGeometry(vane, palette.Vane);
                drawing.FillGeometry(light, palette.Light);
                drawing.DrawGeometry(vane, palette.Barbs, .75f);
                drawing.DrawGeometry(barbs, palette.Barbs, .85f);
                drawing.DrawGeometry(shaft, palette.Shaft, 1.1f);
            }
            _duckHuntFeatherArtwork = artwork;
            _duckHuntFeatherDevice = device;

            void AddBarb(float x, float y, float endX, float endY)
            {
                barbPath.BeginFigure(x, y);
                barbPath.AddCubicBezier(new(endX * .5f, y - 4),
                    new(endX * .9f, endY + 4), new(endX, endY));
                barbPath.EndFigure(CanvasFigureLoop.Open);
            }
        }
        catch
        {
            foreach (var image in artwork) image?.Dispose();
            throw;
        }
    }

    private static void AddFeatherOuterVane(CanvasPathBuilder path)
    {
        path.AddCubicBezier(new(7.5f, 24), new(14, 10), new(14, -3.5f));
        path.AddLine(10.5f, 1);
        path.AddLine(13.3f, -9.5f);
        path.AddCubicBezier(new(11.5f, -26.5f), new(7.8f, -39), new(2.5f, -48));
    }

    private static (Color Vane, Color Light, Color Shaft, Color Barbs) DuckHuntFeatherPalette(int tint) => tint switch
    {
        // Soft, non-clipped highlights keep these decorative feathers distinct
        // from the laser detector's bright centre and saturated colour fringe.
        0 => (Color.FromArgb(255, 217, 208, 185), Color.FromArgb(255, 235, 230, 213),
            Color.FromArgb(255, 132, 124, 102), Color.FromArgb(255, 172, 162, 136)),
        1 => (Color.FromArgb(255, 161, 166, 158), Color.FromArgb(255, 204, 207, 197),
            Color.FromArgb(255, 94, 106, 99), Color.FromArgb(255, 132, 142, 132)),
        _ => (Color.FromArgb(255, 135, 118, 91), Color.FromArgb(255, 182, 164, 132),
            Color.FromArgb(255, 88, 83, 67), Color.FromArgb(255, 113, 106, 87))
    };

    private void DisposeDuckHuntFeathers()
    {
        if (_duckHuntFeatherArtwork is { } artwork)
            foreach (var image in artwork) image.Dispose();
        _duckHuntFeatherArtwork = null;
        _duckHuntFeatherDevice = null;
    }
}
