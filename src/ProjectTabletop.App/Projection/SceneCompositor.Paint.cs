using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private readonly Func<DateTimeOffset> _paintClock;
    private readonly List<PaintBloom> _paintBlooms = [];
    private CanvasRenderTarget? _paintSettledTarget;
    private long _paintSessionRevision = -1, _paintRevision;
    private BoardScreen _paintPreviousScreen;
    private long _paintDropCount, _paintSettledCount;
    private DateTimeOffset _paintLastDropAt;
    private readonly List<(Point2 Center, DateTimeOffset Time)> _paintRecentDrops = [];
    private int _paintDropsAtLastTimestamp;
    private const double PaintSpreadSeconds = 8;
    private const int MaximumActivePaintBlooms = 24;

    internal sealed record PaintDiagnostics(long SessionRevision, long Revision, long DropCount,
        int ActiveDrops, long SettledDrops, int NativeWidth, int NativeHeight, DateTimeOffset LastDropAt);

    internal PaintDiagnostics GetPaintDiagnostics()
    {
        lock (_gate)
        {
            SyncPaintSession();
            return new(_paintSessionRevision, _paintRevision, _paintDropCount, _paintBlooms.Count,
                _paintSettledCount, (int)(_paintSettledTarget?.SizeInPixels.Width ?? 0),
                (int)(_paintSettledTarget?.SizeInPixels.Height ?? 0), _paintLastDropAt);
        }
    }

    public void ShowPaint()
    {
        lock (_gate)
        {
            CancelBoardReveal();
            _blackOutput = false;
            _boardSession.ShowPaint();
            SyncPhotoCopySession();
            SyncPaintSession();
        }
    }

    /// <summary>Add a fresh camera obstruction in calibrated board coordinates.</summary>
    public bool AddPaintDrop(Point2 boardUv, double radiusUv, DateTimeOffset sourceTime)
    {
        lock (_gate)
        {
            SyncPaintSession();
            var now = _paintClock();
            if (_disposed || _boardSession.Screen != BoardScreen.Paint || _blackOutput || _boardSetup ||
                _calibrationTarget >= 0 || IsBoardRevealActive || _boardSurfaceMap is null ||
                _paintBlooms.Count >= MaximumActivePaintBlooms * 2 ||
                !double.IsFinite(boardUv.X) || !double.IsFinite(boardUv.Y) || !double.IsFinite(radiusUv) || radiusUv <= 0 ||
                boardUv.X < .01 || boardUv.X > .99 || boardUv.Y < .19 || boardUv.Y > .99 ||
                sourceTime > now + TimeSpan.FromMilliseconds(50) || now - sourceTime > TimeSpan.FromMilliseconds(500) ||
                sourceTime < _paintLastDropAt || sourceTime == _paintLastDropAt && _paintDropsAtLastTimestamp >= 2)
                return false;
            _paintRecentDrops.RemoveAll(item => sourceTime - item.Time >= TimeSpan.FromMilliseconds(900));
            double aspect = PaintBoardAspect();
            if (_paintRecentDrops.Any(item =>
                Math.Pow((boardUv.X - item.Center.X) * aspect, 2) + Math.Pow(boardUv.Y - item.Center.Y, 2) < .075 * .075))
                return false;
            if (_paintRecentDrops.Count >= 64) return false;
            _paintDropsAtLastTimestamp = sourceTime == _paintLastDropAt ? _paintDropsAtLastTimestamp + 1 : 1;
            _paintLastDropAt = sourceTime;
            _paintRecentDrops.Add((boardUv, sourceTime));
            _paintDropCount++;
            _paintBlooms.Add(new PaintBloom(boardUv, Math.Clamp(radiusUv, .035, .11) * 1.65,
                sourceTime, (int)_paintDropCount));
            _paintRevision++;
            return true;
        }
    }

    public void ResetPaint()
    {
        lock (_gate)
        {
            DisposePaintResources();
            DisposePaintReference();
            _paintDropCount = _paintSettledCount = 0;
            _paintLastDropAt = default;
            _paintRecentDrops.Clear();
            _paintDropsAtLastTimestamp = 0;
            _paintRevision++;
            _renderedBoardState = null;
        }
    }

    private void SyncPaintSession()
    {
        if (_paintSessionRevision == _boardSession.Revision && _paintPreviousScreen == _boardSession.Screen) return;
        if (_paintPreviousScreen == BoardScreen.Paint || _boardSession.Screen == BoardScreen.Paint) ResetPaint();
        _paintSessionRevision = _boardSession.Revision;
        _paintPreviousScreen = _boardSession.Screen;
    }

    private void DisposePaintResources()
    {
        _paintSettledTarget?.Dispose();
        _paintSettledTarget = null;
        foreach (var bloom in _paintBlooms) bloom.Dispose();
        _paintBlooms.Clear();
    }

    private long PaintVisualRevision(DateTimeOffset now)
    {
        SyncPaintSession();
        if (_boardSession.Screen != BoardScreen.Paint) return 0;
        // The settled canvas is static; only wet paint asks for another render.
        long tick = _paintBlooms.Count > 0 ? now.UtcTicks / (TimeSpan.TicksPerSecond / 30) : 0;
        return unchecked(_paintRevision * 1000000007 + tick);
    }

    private double PaintBoardAspect()
    {
        if (_boardSurfaceMap is null) return 1;
        Point2 left = _boardSurfaceMap.Transform(new(0, .5)), right = _boardSurfaceMap.Transform(new(1, .5));
        Point2 top = _boardSurfaceMap.Transform(new(.5, 0)), bottom = _boardSurfaceMap.Transform(new(.5, 1));
        double width = Math.Sqrt(Math.Pow((right.X - left.X) * _displayAspect, 2) + Math.Pow(right.Y - left.Y, 2));
        double height = Math.Sqrt(Math.Pow((bottom.X - top.X) * _displayAspect, 2) + Math.Pow(bottom.Y - top.Y, 2));
        return height > 1e-8 && double.IsFinite(width / height) ? Math.Clamp(width / height, .2, 5) : 1;
    }

    private void DrawPaintSurface(CanvasDrawingSession ds, DateTimeOffset now)
    {
        SyncPaintSession();
        ds.FillRectangle(new Rect(0, 0, BoardSurfaceSize, BoardSurfaceSize), PaintColor(3, 5, 12));
        EnsurePaintSettledTarget(ds.Device);
        double aspect = PaintBoardAspect();
        int settleCount = 0;
        while (settleCount < _paintBlooms.Count &&
            ((now - _paintBlooms[settleCount].CreatedAt).TotalSeconds >= PaintSpreadSeconds ||
             _paintBlooms.Count - settleCount > MaximumActivePaintBlooms)) settleCount++;
        if (settleCount > 0)
        {
            using var settled = _paintSettledTarget!.CreateDrawingSession();
            settled.Transform = BoardRasterTransform(_paintSettledTarget);
            using var clip = settled.CreateLayer(1, new Rect(0, 180, BoardSurfaceSize, BoardSurfaceSize - 180));
            for (int i = 0; i < settleCount; i++)
            {
                DrawPaintBloom(settled, _paintBlooms[i], now, aspect);
                _paintBlooms[i].Dispose();
            }
            _paintBlooms.RemoveRange(0, settleCount);
            _paintSettledCount += settleCount;
            _paintRevision++;
        }
        var pixels = _paintSettledTarget!.SizeInPixels;
        ds.DrawImage(_paintSettledTarget, new Rect(0, 0, BoardSurfaceSize, BoardSurfaceSize),
            new Rect(0, 0, pixels.Width, pixels.Height));
        using (ds.CreateLayer(1, new Rect(0, 180, BoardSurfaceSize, BoardSurfaceSize - 180)))
            foreach (var bloom in _paintBlooms) DrawPaintBloom(ds, bloom, now, aspect);
    }

    private void EnsurePaintSettledTarget(CanvasDevice device)
    {
        if (_paintSettledTarget is { } existing && existing.Device == device &&
            existing.SizeInPixels.Width == _boardRasterPixels.Width && existing.SizeInPixels.Height == _boardRasterPixels.Height)
            return;
        var old = _paintSettledTarget;
        var next = new CanvasRenderTarget(device, _boardRasterPixels.Width, _boardRasterPixels.Height, 96);
        using (var drawing = next.CreateDrawingSession())
        {
            drawing.Clear(Colors.Transparent);
            if (old is not null && old.Device == device)
                drawing.DrawImage(old, new Rect(0, 0, next.SizeInPixels.Width, next.SizeInPixels.Height),
                    new Rect(0, 0, old.SizeInPixels.Width, old.SizeInPixels.Height), 1, CanvasImageInterpolation.HighQualityCubic);
        }
        _paintSettledTarget = next;
        old?.Dispose();
    }

    private static void DrawPaintBloom(CanvasDrawingSession ds, PaintBloom bloom, DateTimeOffset now, double aspect)
    {
        bloom.EnsureGeometry(ds.Device);
        float age = (float)Math.Clamp((now - bloom.CreatedAt).TotalSeconds, 0, PaintSpreadSeconds);
        float flow = 1 - MathF.Exp(-age / 2.35f);
        float radius = (float)bloom.Radius * BoardSurfaceSize * (.25f + .75f * flow);
        float turn = bloom.Phase + .07f * flow;
        Matrix3x2 previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(radius / 100) * Matrix3x2.CreateRotation(turn) *
            Matrix3x2.CreateScale((float)(1 / aspect), 1) *
            Matrix3x2.CreateTranslation((float)bloom.Center.X * BoardSurfaceSize, (float)bloom.Center.Y * BoardSurfaceSize) * previous;
        try
        {
            var palette = PaintPalettes[(bloom.Seed - 1) % PaintPalettes.Length];
            // Translucent pigment, not opaque discs: overlapping blooms retain
            // their underpainting and mix at the fine bands and feathered edges.
            using var pigment = new CanvasRadialGradientBrush(ds.Device,
            [
                new() { Position = 0, Color = PaintAlpha(palette.Main, 200) },
                new() { Position = .32f, Color = PaintAlpha(palette.Light, 205) },
                new() { Position = .72f, Color = PaintAlpha(palette.Main, 185) },
                new() { Position = 1, Color = PaintAlpha(palette.Dark, 45) }
            ]) { Center = new(-9, -5), RadiusX = 119, RadiusY = 115 };
            ds.FillGeometry(bloom.Outline!, pigment);
            for (int i = 0; i < bloom.Bands!.Length; i++)
            {
                Color main = i % 7 == 0 ? PaintColor(218, 194, 39) : i % 4 == 0 ? palette.Dark : palette.Light;
                using var ribbon = new CanvasLinearGradientBrush(ds.Device,
                    PaintAlpha(main, 190), PaintAlpha(i % 4 == 0 ? palette.Main : palette.Light, 10))
                {
                    StartPoint = bloom.BandStarts![i], EndPoint = bloom.BandEnds![i]
                };
                ds.FillGeometry(bloom.Bands[i], ribbon);
            }
            for (int i = 0; i < bloom.Ribbons!.Length; i++)
            {
                var color = i % 7 == 0 ? palette.Light : i % 3 == 0 ? palette.Dark : palette.Main;
                ds.DrawGeometry(bloom.Ribbons[i], PaintAlpha(color, (byte)(i % 7 == 0 ? 108 : 47)),
                    i % 7 == 0 ? .24f : .35f + (i % 4) * .24f);
                if (i % 4 == 0)
                    ds.DrawGeometry(bloom.Ribbons[i], PaintAlpha(palette.Light, 96), .16f);
            }
            ds.DrawGeometry(bloom.Feathers!, PaintAlpha(palette.Light, 80), .20f);
            ds.FillGeometry(bloom.Grain!, PaintAlpha(palette.Light, 84));
            bool metallic = bloom.Seed % 3 != 0;
            for (int layer = 0; layer < bloom.Dust!.Length; layer++)
                ds.FillGeometry(bloom.Dust[layer], metallic
                    ? PaintColor((byte)(layer == 0 ? 185 : 250), (byte)(layer == 0 ? 124 : 207),
                        (byte)(layer == 0 ? 39 : 103), (byte)(layer == 2 ? 245 : 195))
                    : PaintAlpha(palette.Light, (byte)(layer == 2 ? 175 : 95)));
        }
        finally { ds.Transform = previous; }
    }

    private readonly record struct PaintPalette(Color Main, Color Light, Color Dark);
    private static readonly PaintPalette[] PaintPalettes =
    [
        new(PaintColor(16, 145, 199), PaintColor(64, 237, 220), PaintColor(14, 37, 120)),
        new(PaintColor(174, 208, 18), PaintColor(247, 242, 48), PaintColor(18, 103, 68)),
        new(PaintColor(52, 70, 205), PaintColor(91, 168, 247), PaintColor(20, 17, 95)),
        new(PaintColor(238, 153, 19), PaintColor(255, 232, 124), PaintColor(106, 53, 29)),
        new(PaintColor(18, 166, 119), PaintColor(163, 233, 132), PaintColor(10, 70, 99)),
        new(PaintColor(128, 56, 185), PaintColor(218, 146, 233), PaintColor(34, 33, 105))
    ];
    private static Color PaintColor(byte r, byte g, byte b, byte a = 255) => Color.FromArgb(a, r, g, b);
    private static Color PaintAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private sealed class PaintBloom(Point2 center, double radius, DateTimeOffset createdAt, int seed) : IDisposable
    {
        public Point2 Center { get; } = center;
        public double Radius { get; } = radius;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public int Seed { get; } = seed;
        public float Phase { get; } = seed * 2.39996323f;
        public CanvasGeometry? Outline;
        public CanvasGeometry[]? Bands, Ribbons, Dust;
        public Vector2[]? BandStarts, BandEnds;
        public CanvasGeometry? Grain, Feathers;
        private CanvasDevice? _device;

        public void EnsureGeometry(CanvasDevice device)
        {
            if (_device == device && Outline is not null) return;
            Dispose();
            _device = device;
            Outline = ClosedContour(device, 1, 0);
            var random = new Random(Seed * 8191);
            Bands = new CanvasGeometry[27];
            BandStarts = new Vector2[Bands.Length];
            BandEnds = new Vector2[Bands.Length];
            for (int i = 0; i < Bands.Length; i++)
            {
                using var path = new CanvasPathBuilder(device);
                float angle = i * MathF.Tau / Bands.Length + .18f * MathF.Sin(i * 2.7f + Seed);
                float width = .020f + (float)random.NextDouble() * .12f;
                for (int side = 0; side < 2; side++)
                for (int j = 0; j <= 80; j++)
                {
                    float r = -.12f + (side == 0 ? j : 80 - j) / 80f * 1.12f;
                    float a = StreamAngle(angle, r) + (side == 0 ? -1 : 1) * width *
                        (.30f + .70f * MathF.Sin((r + .12f) / 1.12f * MathF.PI));
                    var p = FlowPoint(a, r);
                    if (side == 0 && j == 0) { path.BeginFigure(p); BandStarts[i] = p; }
                    else path.AddLine(p);
                    if (side == 0 && j == 65) BandEnds[i] = p;
                }
                path.EndFigure(CanvasFigureLoop.Closed);
                Bands[i] = CanvasGeometry.CreatePath(path);
            }
            Ribbons = new CanvasGeometry[79];
            for (int i = 0; i < Ribbons.Length; i++)
            {
                using var path = new CanvasPathBuilder(device);
                float angle = i * MathF.Tau / Ribbons.Length + .035f * MathF.Sin(i * 3.7f + Seed);
                for (int j = 0; j <= 64; j++)
                {
                    float r = -.11f + j / 64f * 1.09f;
                    float a = StreamAngle(angle, r);
                    Vector2 p = FlowPoint(a, r);
                    if (j == 0) path.BeginFigure(p); else path.AddLine(p);
                }
                path.EndFigure(CanvasFigureLoop.Open);
                Ribbons[i] = CanvasGeometry.CreatePath(path);
            }
            using (var path = new CanvasPathBuilder(device))
            {
                for (int i = 0; i < 310; i++)
                {
                    float angle = (float)random.NextDouble() * MathF.Tau;
                    float start = .67f + (float)random.NextDouble() * .18f;
                    float end = .98f + (float)random.NextDouble() * .09f;
                    for (int j = 0; j <= 24; j++)
                    {
                        float r = start + (end - start) * j / 24;
                        var p = FlowPoint(StreamAngle(angle, r), r);
                        if (j == 0) path.BeginFigure(p); else path.AddLine(p);
                    }
                    path.EndFigure(CanvasFigureLoop.Open);
                }
                Feathers = CanvasGeometry.CreatePath(path);
            }
            using (var path = new CanvasPathBuilder(device))
            {
                for (int i = 0; i < 10500; i++)
                {
                    float a = (float)random.NextDouble() * MathF.Tau;
                    float r = MathF.Sqrt((float)random.NextDouble());
                    var p = FlowPoint(StreamAngle(a, r), r);
                    float size = .05f + (float)random.NextDouble() * .13f;
                    path.BeginFigure(p + new Vector2(-size, size));
                    path.AddLine(p + new Vector2(size, -size));
                    path.AddLine(p + new Vector2(size, size));
                    path.EndFigure(CanvasFigureLoop.Closed);
                }
                Grain = CanvasGeometry.CreatePath(path);
            }
            Dust = new CanvasGeometry[3];
            for (int layer = 0; layer < 3; layer++)
            {
                using var path = new CanvasPathBuilder(device);
                int count = layer == 2 ? 250 : 2200;
                for (int i = 0; i < count; i++)
                {
                    // Denser metallic deposits ride one side of the curling
                    // flow instead of covering the paint in uniform glitter.
                    float a = (float)(random.NextDouble() * Math.Tau);
                    if (i % 3 != 0) a = -.7f + (float)random.NextDouble() * 2.1f;
                    float r = .94f * MathF.Sqrt((float)random.NextDouble());
                    a += (1 - r) * 1.2f;
                    var p = FlowPoint(a, r);
                    float size = layer == 2 ? .22f + (float)random.NextDouble() * .55f : .07f + (float)random.NextDouble() * .20f;
                    path.BeginFigure(p + new Vector2(-size, size * .7f));
                    path.AddLine(p + new Vector2(size * .75f, -size));
                    path.AddLine(p + new Vector2(size, size * .4f));
                    path.EndFigure(CanvasFigureLoop.Closed);
                }
                Dust[layer] = CanvasGeometry.CreatePath(path);
            }
        }

        private float StreamAngle(float angle, float radius) => angle +
            1.75f * MathF.Pow(MathF.Max(0, 1 - radius), 1.4f) +
            .18f * MathF.Sin(radius * 13 + angle * 3 + Seed) +
            .055f * MathF.Sin(radius * 41 + angle * 9);

        private CanvasGeometry ClosedContour(CanvasDevice device, float radius, float curl)
        {
            using var path = new CanvasPathBuilder(device);
            for (int i = 0; i < 720; i++)
            {
                float a = i * MathF.Tau / 720;
                var p = FlowPoint(a + curl * (1 - radius), radius);
                if (i == 0) path.BeginFigure(p); else path.AddLine(p);
            }
            path.EndFigure(CanvasFigureLoop.Closed);
            return CanvasGeometry.CreatePath(path);
        }

        private Vector2 FlowPoint(float angle, float radius)
        {
            float seedPhase = Seed * 1.173f;
            float feather = .036f * MathF.Sin(angle * 37 + seedPhase) + .018f * MathF.Sin(angle * 71 - seedPhase);
            float edge = 1 + .16f * MathF.Sin(angle * 5 + seedPhase) + .09f * MathF.Sin(angle * 9 - seedPhase * 2) +
                .075f * MathF.Sin(angle * 13 + .8f * MathF.Sin(angle * 7)) + feather;
            float r = 100 * radius * (1 + (edge - 1) * (.28f + .72f * radius));
            float curled = angle + .14f * MathF.Sin(angle * 3 + seedPhase) * radius +
                .07f * MathF.Sin(angle * 11 + seedPhase) * radius * radius;
            return new(r * MathF.Cos(curled) + 7 * radius * MathF.Sin(angle * 2 + seedPhase),
                r * MathF.Sin(curled) + 5 * radius * MathF.Cos(angle * 3 - seedPhase));
        }

        public void Dispose()
        {
            Outline?.Dispose(); Outline = null;
            Grain?.Dispose(); Grain = null;
            Feathers?.Dispose(); Feathers = null;
            foreach (var geometry in (Bands ?? []).Concat(Ribbons ?? []).Concat(Dust ?? [])) geometry.Dispose();
            Bands = Ribbons = Dust = null;
            BandStarts = BandEnds = null;
            _device = null;
        }
    }
}
