using System.Numerics;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection.PaintFluid;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private readonly Func<DateTimeOffset> _paintClock;
    private readonly Queue<PaintDrop> _paintPendingDrops = new();
    private PaintFluidSimulation? _paintFluid;
    private CanvasDevice? _paintFluidDevice;
    private DateTimeOffset _paintAdvancedAt;
    private long _paintSessionRevision = -1, _paintRevision, _paintDropCount;
    private BoardScreen _paintPreviousScreen;
    private DateTimeOffset _paintLastDropAt;
    private readonly List<(Point2 Center, DateTimeOffset Time)> _paintRecentDrops = [];
    private int _paintDropsAtLastTimestamp;

    private sealed record PaintDrop(Point2 Center, float Radius, Vector3 Pigment, int Seed);

    // Deposits build the shared height field while their visible surface coats
    // keep separate colors. Thin rims reveal the paint underneath.
    private static readonly Vector3[] PaintPigments =
    [
        new(.03f, .65f, .85f), new(.85f, .035f, .24f), new(.95f, .60f, .035f),
        new(.17f, .07f, .75f), new(.035f, .60f, .23f), new(.70f, .12f, .55f),
        new(.96f, .27f, .035f), new(.08f, .28f, .84f)
    ];

    internal sealed record PaintDiagnostics(long SessionRevision, long Revision, long DropCount,
        int ActiveDrops, long SettledDrops, int NativeWidth, int NativeHeight, DateTimeOffset LastDropAt,
        PaintFluidDiagnostics? Fluid, int PendingIntroductionDrops, DateTimeOffset? IntroductionStartedAt,
        int PendingIdleStreakDrops, DateTimeOffset? IdleStreakStartedAt, DateTimeOffset? NextIdleStreakAt,
        DateTimeOffset? LastUserActivityAt, long IdleStreakCount);

    internal PaintDiagnostics GetPaintDiagnostics()
    {
        lock (_gate)
        {
            SyncPaintSession();
            bool active = _paintIntroductionDrops.Count > 0 || _paintIdleStreakDrops.Count > 0 ||
                _paintPendingDrops.Count > 0 || _paintFluid is { IsActive: true };
            return new(_paintSessionRevision, _paintRevision, _paintDropCount,
                active ? (int)Math.Min(int.MaxValue, _paintDropCount) : 0, active ? 0 : _paintDropCount,
                _paintFluid is null ? 0 : (int)_boardRasterPixels.Width,
                _paintFluid is null ? 0 : (int)_boardRasterPixels.Height,
                _paintLastDropAt, _paintFluid?.GetDiagnostics(), _paintIntroductionDrops.Count, _paintIntroductionStartedAt,
                _paintIdleStreakDrops.Count, _paintIdleStreakStartedAt, _paintNextIdleStreakAt,
                _paintLastUserActivityAt, _paintIdleStreakCount);
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
                _paintPendingDrops.Count >= 48 ||
                !double.IsFinite(boardUv.X) || !double.IsFinite(boardUv.Y) || !double.IsFinite(radiusUv) || radiusUv <= 0 ||
                boardUv.X < .01 || boardUv.X > .99 || boardUv.Y < .01 || boardUv.Y > .99 ||
                sourceTime > now + TimeSpan.FromMilliseconds(50) || now - sourceTime > TimeSpan.FromMilliseconds(500) ||
                sourceTime < _paintLastDropAt || sourceTime == _paintLastDropAt && _paintDropsAtLastTimestamp >= 2)
                return false;
            NotePaintUserActivity();
            _paintRecentDrops.RemoveAll(item => sourceTime - item.Time >= TimeSpan.FromMilliseconds(900));
            double aspect = PaintBoardAspect();
            if (_paintRecentDrops.Any(item =>
                Math.Pow((boardUv.X - item.Center.X) * aspect, 2) + Math.Pow(boardUv.Y - item.Center.Y, 2) < .075 * .075))
                return false;
            if (_paintRecentDrops.Count >= 64) return false;
            _paintDropsAtLastTimestamp = sourceTime == _paintLastDropAt ? _paintDropsAtLastTimestamp + 1 : 1;
            _paintLastDropAt = sourceTime;
            _paintRecentDrops.Add((boardUv, sourceTime));
            long nextDrop = _paintDropCount + 1;
            QueuePaintDrop(new(boardUv, (float)Math.Clamp(radiusUv, .035, .11),
                PaintPigments[(int)((nextDrop - 1) % PaintPigments.Length)], (int)(nextDrop % int.MaxValue)));
            return true;
        }
    }

    public void ResetPaint()
    {
        lock (_gate)
        {
            ResetPaintSave();
            CancelPaintIntroduction();
            ResetPaintAutomatic();
            DisposePaintResources();
            DisposePaintReference();
            _paintDropCount = 0;
            _paintLastDropAt = default;
            _paintRecentDrops.Clear();
            _paintButtonLightIgnoreRegion = null;
            _paintButtonLightIgnoreUntil = default;
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
        if (_boardSession.Screen == BoardScreen.Paint) SchedulePaintIntroduction();
    }

    private void DisposePaintResources()
    {
        _paintFluid?.Dispose();
        _paintFluid = null;
        _paintFluidDevice = null;
        _paintAdvancedAt = default;
        _paintPendingDrops.Clear();
    }

    private long PaintVisualRevision(DateTimeOffset now)
    {
        SyncPaintSession();
        if (_boardSession.Screen != BoardScreen.Paint) return 0;
        AdvancePaintFluid(now);
        long tick = _paintIntroductionDrops.Count > 0 || _paintIdleStreakDrops.Count > 0 ||
            _paintPendingDrops.Count > 0 || _paintFluid is { IsActive: true }
            ? now.UtcTicks / (TimeSpan.TicksPerSecond / 30) : 0;
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
        if (_paintFluid is null || _paintFluidDevice != ds.Device)
        {
            _paintFluid?.Dispose();
            double aspect = PaintBoardAspect();
            // Transport is independent of the laptop preview's size. Surface
            // normals and light are evaluated again at native board resolution.
            int width = aspect >= 1 ? 1024 : (int)Math.Round(1024 * aspect);
            int height = aspect <= 1 ? 1024 : (int)Math.Round(1024 / aspect);
            _paintFluid = new(ds.Device, width, height, aspect);
            _paintFluidDevice = ds.Device;
            _paintAdvancedAt = now;
        }

        AdvancePaintFluid(now);
        AdvancePaintAutomatic(now);
        while (_paintPendingDrops.TryDequeue(out var drop))
            _paintFluid.AddDrop(new((float)drop.Center.X, (float)drop.Center.Y),
                drop.Radius, drop.Pigment, 1, drop.Seed);
        _paintFluid.Draw(ds, new Rect(0, 0, BoardSurfaceSize, BoardSurfaceSize));
    }

    private void AdvancePaintFluid(DateTimeOffset now)
    {
        // Output, preview and memory Save all share one simulation clock. A
        // second draw at the same timestamp cannot advance the fluid twice.
        if (_paintFluid is null || now <= _paintAdvancedAt) return;
        if (_paintFluid.IsActive) _paintFluid.Advance((now - _paintAdvancedAt).TotalSeconds);
        _paintAdvancedAt = now;
    }

#if DEBUG
    internal PaintMemoryImage CapturePaintArtworkForVerification()
    {
        lock (_gate) return RenderPaintMemoryImage(_paintClock());
    }

    internal PaintFluidFieldStatistics CapturePaintFieldStatisticsForVerification()
    {
        lock (_gate) return _paintFluid!.GetFieldStatistics();
    }

    internal PaintFluidFieldProbe CapturePaintFieldProbeForVerification(Point2 boardUv)
    {
        lock (_gate) return _paintFluid!.GetFieldProbe(new((float)boardUv.X, (float)boardUv.Y));
    }
#endif

    private static Color PaintColor(byte r, byte g, byte b, byte a = 255) => Color.FromArgb(a, r, g, b);
}
