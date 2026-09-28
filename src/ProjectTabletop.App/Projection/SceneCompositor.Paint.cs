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
                boardUv.X < .01 || boardUv.X > .99 || boardUv.Y < .01 || boardUv.Y > .99 ||
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
            ResetPaintSave();
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
            using var clip = settled.CreateLayer(1, new Rect(0, 0, BoardSurfaceSize, BoardSurfaceSize));
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
        using (ds.CreateLayer(1, new Rect(0, 0, BoardSurfaceSize, BoardSurfaceSize)))
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
        float flow = 1 - MathF.Exp(-age / bloom.SpreadTime);
        float radius = (float)bloom.Radius * BoardSurfaceSize * (.25f + .75f * flow);
        Matrix3x2 previous = ds.Transform;
        // Unequal wetting rates relax into the final shape; the board's physical
        // aspect correction remains separate from intentional paint asymmetry.
        ds.Transform = Matrix3x2.CreateScale(radius / 100 * (.88f + .12f * flow), radius / 100) *
            Matrix3x2.CreateRotation(bloom.Phase) * Matrix3x2.CreateScale((float)(1 / aspect), 1) *
            Matrix3x2.CreateTranslation((float)bloom.Center.X * BoardSurfaceSize, (float)bloom.Center.Y * BoardSurfaceSize) * previous;
        try
        {
            var palette = PaintPalettes[(bloom.Seed - 1) % PaintPalettes.Length];
            using var pigment = new CanvasRadialGradientBrush(ds.Device,
            [
                new() { Position = 0, Color = PaintAlpha(palette.Light, 218) },
                new() { Position = .23f, Color = PaintAlpha(palette.Main, 220) },
                new() { Position = .73f, Color = PaintAlpha(palette.Main, 194) },
                new() { Position = 1, Color = PaintAlpha(palette.Dark, 155) }
            ]) { Center = bloom.Source, RadiusX = 155, RadiusY = 140 };
            ds.FillGeometry(bloom.Outline!, pigment);
            using (ds.CreateLayer(1, bloom.Outline))
            {
                for (int i = 0; i < bloom.Bands!.Length; i++)
                {
                    Color color = i % 5 == 0 && bloom.Metallic ? PaintColor(226, 176, 59) :
                        i % 3 == 0 ? palette.Dark : palette.Light;
                    using var ribbon = new CanvasLinearGradientBrush(ds.Device,
                        PaintAlpha(color, (byte)(i % 3 == 0 ? 140 : 165)), PaintAlpha(color, 8))
                    { StartPoint = bloom.BandStarts![i], EndPoint = bloom.BandEnds![i] };
                    ds.FillGeometry(bloom.Bands[i], ribbon);
                }
                for (int i = 0; i < bloom.Ribbons!.Length; i++)
                    ds.DrawGeometry(bloom.Ribbons[i], PaintAlpha(i % 3 == 0 ? palette.Light : palette.Dark,
                        (byte)(i % 3 == 0 ? 90 : 58)), i % 3 == 0 ? .20f : .42f);
                foreach (var cell in bloom.Cells)
                {
                    using var interior = new CanvasRadialGradientBrush(ds.Device,
                    [
                        new() { Position = 0, Color = PaintAlpha(palette.Dark, 202) },
                        new() { Position = .61f, Color = PaintAlpha(palette.Dark, 170) },
                        new() { Position = .87f, Color = PaintAlpha(palette.Main, 130) },
                        new() { Position = 1, Color = PaintAlpha(palette.Light, 185) }
                    ]) { Center = cell.Center + new Vector2(cell.Radius * .14f), RadiusX = cell.Radius, RadiusY = cell.Radius * .88f };
                    ds.FillGeometry(cell.Body, interior);
                    ds.DrawGeometry(cell.Rim, PaintAlpha(palette.Light, 205), .75f);
                    ds.DrawGeometry(cell.Rim, PaintColor(241, 255, 235, 90), .22f);
                }
                ds.FillGeometry(bloom.Grain!, PaintAlpha(palette.Light, bloom.Kind == PaintKind.Wash ? (byte)96 : (byte)38));
                for (int layer = 0; layer < bloom.Dust!.Length; layer++)
                    ds.FillGeometry(bloom.Dust[layer], PaintColor((byte)(layer == 0 ? 156 : 255),
                        (byte)(layer == 0 ? 104 : 219), (byte)(layer == 0 ? 29 : 123), (byte)(layer == 2 ? 240 : 175)));
            }
            if (bloom.Kind != PaintKind.Wash)
            {
                using var surface = ds.CreateLayer(1, bloom.Outline);
                Vector2 shine = bloom.Source + new Vector2(-18, -32);
                using var reflection = new CanvasRadialGradientBrush(ds.Device,
                    PaintColor(231, 248, 255, 58), PaintColor(231, 248, 255, 0))
                { Center = shine, RadiusX = 33, RadiusY = 7 };
                ds.FillEllipse(shine, 33, 7, reflection);
            }
            ds.DrawGeometry(bloom.Outline!, PaintAlpha(palette.Dark, 120), bloom.Kind == PaintKind.Wash ? .30f : 1.15f);
            if (bloom.Feathers is not null) ds.DrawGeometry(bloom.Feathers, PaintAlpha(palette.Light, 65), .17f);
            // Broken meniscus reflections, not a uniformly glowing perimeter.
            ds.DrawGeometry(bloom.Highlights!, PaintAlpha(palette.Light, 160), .80f);
            ds.DrawGeometry(bloom.Highlights!, PaintColor(241, 253, 255, 130), .23f);
            foreach (var bead in bloom.Beads)
            {
                float flight = Math.Clamp(age / .65f, 0, 1);
                Vector2 position = bead.Center * (.76f + .24f * flight);
                ds.FillEllipse(position, bead.Radius, bead.Radius * bead.Aspect, PaintAlpha(palette.Dark, 190));
                ds.FillEllipse(position - new Vector2(bead.Radius * .10f), bead.Radius * .84f,
                    bead.Radius * bead.Aspect * .84f, PaintAlpha(palette.Main, 235));
                ds.FillEllipse(position - new Vector2(bead.Radius * .29f), bead.Radius * .30f,
                    bead.Radius * .13f, PaintAlpha(palette.Light, 180));
            }
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
    private enum PaintKind { Pool, Wash, Splash, Cells, Pour }

    private sealed class PaintBloom : IDisposable
    {
        public Point2 Center { get; }
        public double Radius { get; }
        public DateTimeOffset CreatedAt { get; }
        public int Seed { get; }
        public PaintKind Kind { get; }
        public float Phase { get; }
        public float SpreadTime { get; }
        public Vector2 Source { get; }
        public bool Metallic { get; }
        public CanvasGeometry? Outline, Grain, Feathers, Highlights;
        public CanvasGeometry[]? Bands, Ribbons, Dust;
        public Vector2[]? BandStarts, BandEnds;
        public readonly List<(Vector2 Center, float Radius, float Aspect)> Beads = [];
        public readonly List<(Vector2 Center, float Radius, CanvasGeometry Body, CanvasGeometry Rim)> Cells = [];
        private readonly float _lobes, _roughness, _skew, _curl, _stretch, _detailPhase;
        private readonly (float Angle, float Width, float Length)[] _fingers;
        private CanvasDevice? _device;

        public PaintBloom(Point2 center, double radius, DateTimeOffset createdAt, int seed)
        {
            Center = center; CreatedAt = createdAt; Seed = seed;
            Kind = (PaintKind)((seed - 1) % 5);
            var random = new Random(unchecked(seed * 8191));
            float Next(float min, float max) => min + (float)random.NextDouble() * (max - min);
            Radius = radius * Next(.83f, 1.10f);
            Phase = Next(0, MathF.Tau);
            SpreadTime = Next(2.3f, 3.5f);
            _detailPhase = Next(0, MathF.Tau);
            _lobes = random.Next(2, 6);
            _roughness = Next(.05f, .13f);
            _skew = Next(-.22f, .25f);
            _curl = Next(-1.1f, 1.1f);
            _stretch = Kind == PaintKind.Pour ? Next(1.3f, 1.6f) : Next(.88f, 1.13f);
            Source = Kind == PaintKind.Wash ? new(-42, Next(-20, 20)) : new(Next(-25, 25), Next(-22, 22));
            Metallic = random.NextDouble() < .58;
            _fingers = Enumerable.Range(0, random.Next(6, 12)).Select(_ =>
                (Next(0, MathF.Tau), Next(.025f, .10f), Next(.18f, .56f))).ToArray();
        }

        public void EnsureGeometry(CanvasDevice device)
        {
            if (_device == device && Outline is not null) return;
            Dispose(); _device = device;
            var random = new Random(unchecked(Seed * 16547));
            float Next(float min, float max) => min + (float)random.NextDouble() * (max - min);
            Outline = Path(device, 540, t => FlowPoint(t * MathF.Tau, 1), true);
            int bandCount = Kind == PaintKind.Wash ? 25 : random.Next(9, 18);
            Bands = new CanvasGeometry[bandCount];
            BandStarts = new Vector2[bandCount]; BandEnds = new Vector2[bandCount];
            for (int i = 0; i < bandCount; i++)
            {
                float lane = (i + Next(.1f, .9f)) / bandCount;
                float width = Next(.003f, .029f);
                using var path = new CanvasPathBuilder(device);
                for (int side = 0; side < 2; side++)
                for (int j = 0; j <= 80; j++)
                {
                    float t = (side == 0 ? j : 80 - j) / 80f;
                    var p = StreamPoint(lane + (side == 0 ? -1 : 1) * width *
                        (.35f + .65f * MathF.Sin(t * MathF.PI)), t);
                    if (side == 0 && j == 0) { path.BeginFigure(p); BandStarts[i] = p; }
                    else path.AddLine(p);
                    if (side == 0 && j == 72) BandEnds[i] = p;
                }
                path.EndFigure(CanvasFigureLoop.Closed); Bands[i] = CanvasGeometry.CreatePath(path);
            }
            int ribbonCount = Kind == PaintKind.Wash ? 115 : random.Next(28, 52);
            Ribbons = new CanvasGeometry[ribbonCount];
            for (int i = 0; i < ribbonCount; i++)
            {
                float lane = Next(0, 1);
                Ribbons[i] = Path(device, 80, t => StreamPoint(lane, t), false);
            }
            if (Kind == PaintKind.Wash)
            {
                using var featherPath = new CanvasPathBuilder(device);
                for (int i = 0; i < 190; i++)
                {
                    float angle = Next(-1.7f, 1.7f);
                    for (int j = 0; j <= 16; j++)
                    {
                        float r = .84f + .21f * j / 16;
                        var p = FlowPoint(angle + .025f * MathF.Sin(r * 35 + i), r);
                        if (j == 0) featherPath.BeginFigure(p); else featherPath.AddLine(p);
                    }
                    featherPath.EndFigure(CanvasFigureLoop.Open);
                }
                Feathers = CanvasGeometry.CreatePath(featherPath);
            }
            // Pigment grains and metallic deposits follow a few flow seams.
            // Most wet pools have quiet interiors instead of uniform glitter.
            Grain = Particles(device, random, Kind == PaintKind.Wash ? 7800 : 1600, false, 0);
            Dust = new CanvasGeometry[Metallic ? 3 : 0];
            for (int layer = 0; layer < Dust.Length; layer++)
                Dust[layer] = Particles(device, random, layer == 2 ? 95 : 1250, true, layer);
            using (var highlightPath = new CanvasPathBuilder(device))
            {
                int arcs = Kind == PaintKind.Wash ? 2 : random.Next(3, 7);
                for (int i = 0; i < arcs; i++)
                {
                    float start = Next(3.2f, 5.9f), length = Next(.10f, .40f);
                    for (int j = 0; j <= 30; j++)
                    {
                        var p = FlowPoint(start + length * j / 30, .977f);
                        if (j == 0) highlightPath.BeginFigure(p); else highlightPath.AddLine(p);
                    }
                    highlightPath.EndFigure(CanvasFigureLoop.Open);
                }
                Highlights = CanvasGeometry.CreatePath(highlightPath);
            }
            if (Kind is PaintKind.Splash or PaintKind.Pool)
            {
                int count = Kind == PaintKind.Splash ? random.Next(14, 26) : random.Next(2, 6);
                for (int i = 0; i < count; i++)
                {
                    float a = Next(0, MathF.Tau);
                    Beads.Add((FlowPoint(a, Next(1.13f, 1.55f)), Next(.7f, Kind == PaintKind.Splash ? 4.4f : 2.3f), Next(.55f, 1.05f)));
                }
            }
            if (Kind is PaintKind.Cells or PaintKind.Pour)
            {
                int attempts = Kind == PaintKind.Cells ? 45 : 5;
                for (int i = 0; i < attempts && Cells.Count < 11; i++)
                {
                    var center = FlowPoint(Next(0, MathF.Tau), Next(.05f, .70f));
                    float radius = Next(7, Kind == PaintKind.Cells ? 25 : 13), phase = Next(0, MathF.Tau);
                    if (Cells.Any(cell => Vector2.Distance(cell.Center, center) < (cell.Radius + radius) * 1.1f)) continue;
                    Vector2 CellPoint(float a) => center + new Vector2(MathF.Cos(a), MathF.Sin(a) * .88f) *
                        radius * (1 + .08f * MathF.Sin(a * 3 + phase) + .045f * MathF.Sin(a * 5 - phase));
                    Cells.Add((center, radius, Path(device, 100, t => CellPoint(t * MathF.Tau), true),
                        Path(device, 45, t => CellPoint(3.4f + t * 1.8f), false)));
                }
            }
        }

        private CanvasGeometry Particles(CanvasDevice device, Random random, int count, bool metallic, int layer)
        {
            using var path = new CanvasPathBuilder(device);
            for (int i = 0; i < count; i++)
            {
                float t = (float)random.NextDouble();
                float lane = metallic && i % 4 != 0 ? .28f + .035f * (float)random.NextDouble() : (float)random.NextDouble();
                Vector2 p = metallic ? StreamPoint(lane, t) : FlowPoint(t * MathF.Tau, MathF.Sqrt((float)random.NextDouble()));
                float size = layer == 2 ? .30f + (float)random.NextDouble() * .8f : .06f + (float)random.NextDouble() * .18f;
                path.BeginFigure(p + new Vector2(-size, size * .6f));
                path.AddLine(p + new Vector2(size * .6f, -size));
                path.AddLine(p + new Vector2(size, size * .3f));
                path.EndFigure(CanvasFigureLoop.Closed);
            }
            return CanvasGeometry.CreatePath(path);
        }

        private Vector2 StreamPoint(float lane, float t)
        {
            if (Kind == PaintKind.Wash)
            {
                float angle = (lane - .5f) * 4.9f;
                float bend = _curl * (1 - t) * (1 - t) + .075f * MathF.Sin(t * 23 + lane * 12);
                var edge = FlowPoint(angle + bend, t);
                return edge + Source * MathF.Pow(1 - t, 1.8f);
            }
            // Off-centre folds cross the body rather than all converging on one
            // repeated spiral. Different layers carry different pigment widths.
            float x = -145 + 290 * t;
            float y = (lane - .5f) * 240;
            y += 17 * MathF.Sin(t * 7 + lane * 8 + _detailPhase) +
                9 * MathF.Sin(t * 17 - lane * 11 + _detailPhase) + 3 * MathF.Sin(t * 39 + lane * 18);
            float dx = x - Source.X, dy = y - Source.Y;
            float twist = _curl * 2.1f * MathF.Exp(-(dx * dx + dy * dy) / 7800);
            return Source + Vector2.Transform(new(dx, dy), Matrix3x2.CreateRotation(twist));
        }

        private Vector2 FlowPoint(float angle, float radius)
        {
            float edge = 1 + _roughness * MathF.Sin(angle * _lobes + _detailPhase) +
                .045f * MathF.Sin(angle * (_lobes + 3) - _detailPhase) + _skew * MathF.Cos(angle);
            if (Kind == PaintKind.Splash)
            {
                edge *= .79f;
                foreach (var finger in _fingers)
                {
                    float difference = MathF.Atan2(MathF.Sin(angle - finger.Angle), MathF.Cos(angle - finger.Angle));
                    edge += finger.Length * MathF.Exp(-difference * difference / (2 * finger.Width * finger.Width));
                }
            }
            else if (Kind == PaintKind.Wash)
                edge += .23f * MathF.Cos(angle) + .065f * MathF.Sin(angle * 23 + _detailPhase) +
                    .027f * MathF.Sin(angle * 61 - _detailPhase);
            else if (Kind == PaintKind.Pour)
                edge += .16f * MathF.Sin(angle * 3 + _detailPhase);
            float r = 100 * radius * (1 + (edge - 1) * radius);
            return new(r * MathF.Cos(angle) * _stretch, r * MathF.Sin(angle) / _stretch);
        }

        private static CanvasGeometry Path(CanvasDevice device, int steps, Func<float, Vector2> point, bool closed)
        {
            using var path = new CanvasPathBuilder(device);
            for (int i = 0; i <= steps; i++)
            {
                var p = point(i / (float)steps);
                if (i == 0) path.BeginFigure(p); else path.AddLine(p);
            }
            path.EndFigure(closed ? CanvasFigureLoop.Closed : CanvasFigureLoop.Open);
            return CanvasGeometry.CreatePath(path);
        }

        public void Dispose()
        {
            Outline?.Dispose(); Outline = null;
            Grain?.Dispose(); Grain = null;
            Feathers?.Dispose(); Feathers = null;
            Highlights?.Dispose(); Highlights = null;
            foreach (var geometry in (Bands ?? []).Concat(Ribbons ?? []).Concat(Dust ?? [])) geometry.Dispose();
            foreach (var cell in Cells) { cell.Body.Dispose(); cell.Rim.Dispose(); }
            Cells.Clear(); Beads.Clear();
            Bands = Ribbons = Dust = null;
            BandStarts = BandEnds = null;
            _device = null;
        }
    }
}
