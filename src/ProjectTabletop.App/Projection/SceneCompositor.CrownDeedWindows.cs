using System.Numerics;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using ProjectTabletop.App.Projection.CrownDeedRendering;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private PixelShaderEffect<CrownDeedWindowsShader>? _crownDeedWindowsShader;
    private CanvasDevice? _crownDeedWindowsDevice;
    private CanvasGeometry? _crownDeedWindowsGeometry;
    private CanvasRenderTarget? _crownDeedWindowsMask;
    private CanvasRenderTarget? _crownDeedWindowsTarget;
    private long? _crownDeedWindowsLayerState;
    private DateTimeOffset? _crownDeedWindowsEpoch;

    // Windows share an ID across their separate glass panes. Tight batches
    // reduce draw calls without introducing any visible rectangular boundary.
    private static readonly Lazy<Rect[]> CrownDeedWindowPatches = new(() => CrownDeedWindowPanes
        .Select((panes, index) => (Panes: panes, Id: index + 1))
        .Where(window => CrownDeedWindowTimeline.IsAnimated(window.Id))
        .GroupBy(window =>
        {
            var points = window.Panes.SelectMany(pane => pane).ToArray();
            return (Side: points.Average(p => p.X) < 500 ? 0 : 1,
                Row: (int)(points.Average(p => p.Y) / 50));
        })
        .Select(group =>
        {
            var points = group.SelectMany(window => window.Panes).SelectMany(pane => pane).ToArray();
            double x = Math.Max(0, points.Min(p => p.X) - 2), y = Math.Max(0, points.Min(p => p.Y) - 2);
            return new Rect(x, y, Math.Min(1000, points.Max(p => p.X) + 2) - x,
                Math.Min(1000, points.Max(p => p.Y) + 2) - y);
        }).ToArray());

    private static CanvasGeometry CrownDeedWindowGeometry(CanvasDevice device, Vector2[][] panes)
    {
        using var path = new CanvasPathBuilder(device);
        foreach (var points in panes)
        {
            path.BeginFigure(points[0]);
            foreach (var point in points.Skip(1)) path.AddLine(point);
            path.EndFigure(CanvasFigureLoop.Closed);
        }
        return CanvasGeometry.CreatePath(path);
    }

    private bool EnsureCrownDeedWindowsResources(CanvasDevice device)
    {
        if (EnsureCrownDeedCity(device) is not { } city) return false;
        if (_crownDeedWindowsDevice != device) DisposeCrownDeedWindows();
        _crownDeedWindowsDevice = device;
        if (_crownDeedWindowsMask is null)
        {
            var windows = CrownDeedWindowPanes.Select((panes, index) => (Panes: panes, Id: index + 1))
                .Where(window => CrownDeedWindowTimeline.IsAnimated(window.Id))
                .Select(window => (Shape: CrownDeedWindowGeometry(device, window.Panes), window.Id)).ToArray();
            try
            {
                _crownDeedWindowsGeometry = windows[0].Shape.CombineWith(windows[0].Shape,
                    Matrix3x2.Identity, CanvasGeometryCombine.Union);
                foreach (var window in windows.Skip(1))
                {
                    var combined = _crownDeedWindowsGeometry.CombineWith(window.Shape,
                        Matrix3x2.Identity, CanvasGeometryCombine.Union);
                    _crownDeedWindowsGeometry.Dispose(); _crownDeedWindowsGeometry = combined;
                }
                var size = city.SizeInPixels;
                _crownDeedWindowsMask = new CanvasRenderTarget(device, size.Width, size.Height, 96);
                using var drawing = _crownDeedWindowsMask.CreateDrawingSession();
                drawing.Transform = Matrix3x2.CreateScale(size.Width / 1000f, size.Height / 1000f);
                drawing.Clear(Colors.Black);
                // Categorical IDs extend beyond the glass by 1.5 source pixels.
                // Separate passes retain whole IDs at antialiased coverage edges.
                drawing.Antialiasing = CanvasAntialiasing.Aliased;
                foreach (var window in windows)
                {
                    var identity = ThemeColor(0, (byte)(window.Id & 255), (byte)(window.Id >> 8));
                    drawing.DrawGeometry(window.Shape, identity, 3000f / size.Width);
                    drawing.FillGeometry(window.Shape, identity);
                }
                foreach (var window in windows)
                    drawing.FillGeometry(window.Shape,
                        ThemeColor(0, (byte)(window.Id & 255), (byte)(window.Id >> 8)));
                drawing.Antialiasing = CanvasAntialiasing.Antialiased;
                drawing.Blend = CanvasBlend.Add;
                drawing.FillGeometry(_crownDeedWindowsGeometry, Colors.Red);
            }
            finally { foreach (var window in windows) window.Shape.Dispose(); }
        }
        _crownDeedWindowsShader ??= new PixelShaderEffect<CrownDeedWindowsShader>();
        _crownDeedWindowsShader.Sources[0] = city;
        _crownDeedWindowsShader.Sources[1] = _crownDeedWindowsMask;
        return true;
    }

    private void ClearCrownDeedWindows(CanvasDrawingSession drawing)
    {
        if (!EnsureCrownDeedWindowsResources(drawing.Device)) return;
        var previous = drawing.Blend;
        try
        {
            drawing.Blend = CanvasBlend.Copy;
            drawing.FillGeometry(_crownDeedWindowsGeometry!, Colors.Transparent);
        }
        finally { drawing.Blend = previous; }
    }

    private void DrawCrownDeedWindows(CanvasDrawingSession drawing, DateTimeOffset now)
    {
        if (!EnsureCrownDeedWindowsResources(drawing.Device)) return;
        _crownDeedWindowsEpoch ??= now;
        var size = _crownDeedCityBitmap!.SizeInPixels;
        var scale = new Float2(size.Width / 1000f, size.Height / 1000f);
        float time = (float)Math.Max(0, (now - _crownDeedWindowsEpoch.Value).TotalSeconds);
        _crownDeedWindowsShader!.ConstantBuffer = new(scale, time, 1);
        foreach (var patch in CrownDeedWindowPatches.Value)
            drawing.DrawImage(_crownDeedWindowsShader, patch,
                new Rect(patch.X * scale.X, patch.Y * scale.Y, patch.Width * scale.X, patch.Height * scale.Y),
                1, CanvasImageInterpolation.Linear);
    }

    private CanvasRenderTarget? DrawCrownDeedWindowsLayer(CanvasDevice device, DateTimeOffset now)
    {
        if (!EnsureCrownDeedWindowsResources(device)) return null;
        _crownDeedWindowsEpoch ??= now;
        if (EnsureBoardRenderTarget(ref _crownDeedWindowsTarget, device)) _crownDeedWindowsLayerState = null;
        long frame = Math.Max(0, (now - _crownDeedWindowsEpoch.Value).Ticks) / (TimeSpan.TicksPerSecond / 60);
        if (_crownDeedWindowsLayerState != frame)
        {
            using var drawing = _crownDeedWindowsTarget!.CreateDrawingSession();
            drawing.Transform = BoardRasterTransform(_crownDeedWindowsTarget);
            drawing.Clear(Colors.Transparent);
            DrawCrownDeedWindows(drawing, now);
            _crownDeedWindowsLayerState = frame;
        }
        return _crownDeedWindowsTarget;
    }

    private void DisposeCrownDeedWindows()
    {
        _crownDeedWindowsShader?.Dispose(); _crownDeedWindowsShader = null;
        _crownDeedWindowsGeometry?.Dispose(); _crownDeedWindowsGeometry = null;
        _crownDeedWindowsMask?.Dispose(); _crownDeedWindowsMask = null;
        _crownDeedWindowsTarget?.Dispose(); _crownDeedWindowsTarget = null;
        _crownDeedWindowsDevice = null; _crownDeedWindowsLayerState = null;
        // A resize, GPU recreation or menu visit preserves the city's timeline.
    }
}
