using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using ProjectTabletop.App.Projection.Football;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private readonly Func<DateTimeOffset> _footballClock;
    private FootballRenderer? _footballRenderer;
    private CanvasDevice? _footballDevice;
    private CanvasRenderTarget? _footballThumbnail;
    private Homography? _footballCameraMap, _footballSurfaceMap;
    private long _footballNavigation = -1;
    private long _footballSessionInputRevision = -1, _footballInputEpoch;
    private bool _footballActive, _footballArtworkPublished;
    private DateTimeOffset _footballResetThrough;
    private readonly DateTimeOffset[] _footballSourceTimes = new DateTimeOffset[2];
    private readonly bool[] _footballFingerPlayers = new bool[2];
    private readonly float?[] _footballBarHeadings = new float?[2];

    private bool FootballArtworkReady => _footballRenderer is { PitchReady: true };

    // The pitch is searched for arriving hands only while a human player
    // actually uses fingertip input; black bars need no palm inference.
    private bool FootballFingerSearch => _boardSession.Screen == BoardScreen.Football &&
        (_footballFingerPlayers[0] || _footballFingerPlayers[1] && _boardSession.FootballState.Mode == FootballMode.TwoHumans);

    public FootballSnapshot FootballState { get { lock (_gate) return _boardSession.FootballState; } }
    public long FootballInputRevision
    {
        get { lock (_gate) { SyncFootballSession(); return _footballInputEpoch; } }
    }
    public double FootballPreviewAspect { get { lock (_gate) return PaintBoardAspect(); } }

    public void ShowFootball()
    {
        lock (_gate)
        {
            CancelBoardReveal();
            _blackOutput = false;
            _boardSession.ShowFootball(_footballClock());
            SyncPhotoCopySession();
            SyncFootballSession();
        }
    }

    public void ResetFootball()
    {
        lock (_gate)
        {
            _boardSession.ResetFootball(_footballClock());
            ClearFootballObservations();
        }
    }

    public void SetFootballMode(FootballMode mode)
    {
        lock (_gate)
        {
            if (_boardSession.FootballState.Mode == mode) return;
            _boardSession.SetFootballMode(mode, _footballClock());
            ClearFootballObservations();
        }
    }

    public void SetFootballStyle(int player, FootballKickerStyle style)
    {
        lock (_gate) _boardSession.SetFootballStyle(player, style);
    }

    public void SetFootballFingerInput(int player, bool enabled)
    {
        if (player is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(player));
        lock (_gate) _footballFingerPlayers[player] = enabled;
    }

    internal async Task EnsureFootballResourcesAsync(CanvasDevice device)
    {
        Task pitch;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            pitch = GetFootballRenderer(device).EnsurePitchAsync();
        }
        await pitch.ConfigureAwait(false);
        lock (_gate)
            if (!_disposed && _footballDevice == device) PrepareFootballResources(device);
    }

    private bool PrepareFootballResources(CanvasDevice device)
    {
        var renderer = GetFootballRenderer(device);
        _ = renderer.EnsurePitchAsync();
        if (!renderer.PitchReady) return false;
        if (!_footballArtworkPublished)
        {
            _footballArtworkPublished = true;
            if (_boardSession.Screen == BoardScreen.Football) InvalidateBoardArtworkSurface();
        }
        return true;
    }

    public bool ActivateFootballButton(string id)
    {
        lock (_gate)
        {
            if (_boardSession.Screen != BoardScreen.Football) return false;
            bool accepted = _boardSession.ActivateButton(id, _footballClock());
            if (accepted)
            {
                ClearFootballObservations();
                SyncFootballSession();
            }
            return accepted;
        }
    }

    public bool ActivateFootballAt(double u, double v)
    {
        lock (_gate)
        {
            var button = _boardSession.Buttons.FirstOrDefault(button => button.Enabled && button.Bounds.Contains(u, v));
            return button is not null && ActivateFootballButton(button.Id);
        }
    }

    private void ClearFootballObservations()
    {
        _footballSessionInputRevision = _boardSession.FootballInputRevision;
        _footballInputEpoch++;
        _footballResetThrough = _footballClock();
        Array.Fill(_footballSourceTimes, _footballResetThrough);
        Array.Clear(_footballBarHeadings);
        _boardSession.ClearFootballInput(_footballResetThrough);
        _renderedBoardState = null;
    }

    private void SyncFootballSession()
    {
        bool active = !_disposed && _boardSession.Screen == BoardScreen.Football &&
            !_blackOutput && !_boardSetup && _calibrationTarget < 0 && !IsBoardRevealActive &&
            _boardCameraMap is not null && _boardSurfaceMap is not null;
        if (active == _footballActive && _footballNavigation == _boardSession.NavigationRevision &&
            _footballSessionInputRevision == _boardSession.FootballInputRevision &&
            ReferenceEquals(_footballCameraMap, _boardCameraMap) && ReferenceEquals(_footballSurfaceMap, _boardSurfaceMap)) return;
        _footballActive = active;
        _footballNavigation = _boardSession.NavigationRevision;
        _footballCameraMap = _boardCameraMap;
        _footballSurfaceMap = _boardSurfaceMap;
        ClearFootballObservations();
    }

    // Fit a regulation-shaped field to the physical board. The inverse input
    // mapping uses this exact rectangle, including its letterboxed margins.
    internal Rect FootballFieldBounds()
    {
        double aspect = PaintBoardAspect();
        double width = Math.Min(.84, .69 * FootballGame.Width / aspect);
        double height = width * aspect / FootballGame.Width;
        return new((1 - width) / 2, .15 + (.69 - height) / 2, width, height);
    }

    public bool TryMapFootballCameraPoint(PixelPoint camera, out PixelPoint normalized)
        => TryMapFootballCameraPoint(camera, out normalized, requireInside: true);

    // Physical markers may sit behind a goal or sideline on the visible grass.
    // Preserve their actual position and angle; clamp only the attached kicker.
    public bool TryMapFootballMarkerCameraPoint(PixelPoint camera, out PixelPoint normalized)
        => TryMapFootballCameraPoint(camera, out normalized, requireInside: false) &&
            FootballFieldGeometry.ContainsMarker(normalized.X, normalized.Y);

    private bool TryMapFootballCameraPoint(PixelPoint camera, out PixelPoint normalized, bool requireInside)
    {
        lock (_gate)
        {
            normalized = default;
            SyncFootballSession();
            if (!_footballActive || !double.IsFinite(camera.X) || !double.IsFinite(camera.Y)) return false;
            try
            {
                var uv = _boardSurfaceMap!.InverseTransform(_boardCameraMap!.Transform(new(camera.X, camera.Y)));
                var field = FootballFieldBounds();
                double x = (uv.X - field.X) / field.Width, y = (uv.Y - field.Y) / field.Height;
                if (!double.IsFinite(x) || !double.IsFinite(y) ||
                    requireInside && (x < 0 || x > 1 || y < 0 || y > 1)) return false;
                normalized = new(x, y);
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }
    }

    public void SetFootballPlayerCameraPoint(int player, PixelPoint? cameraPoint, DateTimeOffset frameTime)
    {
        if (player is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(player));
        lock (_gate)
        {
            PixelPoint? field = cameraPoint is { } camera && TryMapFootballCameraPoint(camera, out var uv) ? uv : null;
            SetFootballPlayerFieldPoint(player, field, frameTime);
        }
    }

    /// <summary>Supplies a normalized pitch point already returned by TryMapFootballCameraPoint,
    /// or null. A registration change since that mapping rejects the older frame.</summary>
    public void SetFootballPlayerFieldPoint(int player, PixelPoint? fieldPoint, DateTimeOffset frameTime)
    {
        if (player is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(player));
        lock (_gate)
        {
            SyncFootballSession();
            var now = _footballClock();
            if (frameTime <= _footballResetThrough || frameTime <= _footballSourceTimes[player] ||
                frameTime > now || now - frameTime > FootballGame.InputFreshness) return;
            _footballSourceTimes[player] = frameTime;
            _footballBarHeadings[player] = null;
            Vector2? point = _footballActive && fieldPoint is { } uv && double.IsFinite(uv.X) && double.IsFinite(uv.Y) &&
                uv.X is >= 0 and <= 1 && uv.Y is >= 0 and <= 1
                ? new((float)(uv.X - .5) * FootballGame.Width, (float)(uv.Y - .5)) : null;
            _boardSession.SetFootballInput(player, point, frameTime);
        }
    }

    /// <summary>Maps the whole physical bar through the calibrated plane before deriving
    /// its angle. Camera angles alone are wrong on rotated or oblique projections.</summary>
    public bool SetFootballPlayerBar(int player, ColorTipObservation observation, DateTimeOffset frameTime)
    {
        if (player is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(player));
        lock (_gate)
        {
            SyncFootballSession();
            var now = _footballClock();
            if (!_footballActive || frameTime <= _footballResetThrough || frameTime <= _footballSourceTimes[player] ||
                frameTime > now || now - frameTime > FootballGame.InputFreshness) return false;
            float? previous = frameTime - _footballSourceTimes[player] <= FootballGame.TrackingRecoveryTimeout
                ? _footballBarHeadings[player] : null;
            _footballSourceTimes[player] = frameTime;
            if (observation.Bar is not { } bar || !TryMapFootballMarkerCameraPoint(observation.Center, out var center) ||
                (center.X < .5 ? 0 : 1) != player ||
                !TryMapFootballCameraPoint(bar.End1, out var end1, false) ||
                !TryMapFootballCameraPoint(bar.End2, out var end2, false) ||
                !TryMapFootballCameraPoint(bar.Side1, out var side1, false) ||
                !TryMapFootballCameraPoint(bar.Side2, out var side2, false) ||
                !FootballBarPose.TryResolve(player, World(center), World(end1), World(end2),
                    World(side1), World(side2), previous, out var position, out float heading))
            {
                _footballBarHeadings[player] = null;
                _boardSession.SetFootballInput(player, null, frameTime);
                return false;
            }
            _footballBarHeadings[player] = heading;
            // Keep the complete game object on the pitch when a physical bar
            // reaches a sideline; do not report a visible bar as a lost player.
            position = Vector2.Clamp(position,
                new(-FootballGame.Width / 2 + FootballGame.KickerRadius, -.5f + FootballGame.KickerRadius),
                new(FootballGame.Width / 2 - FootballGame.KickerRadius, .5f - FootballGame.KickerRadius));
            _boardSession.SetFootballInput(player, position, frameTime, heading);
            return true;
        }

        static Vector2 World(PixelPoint point) => new((float)(point.X - .5) * FootballGame.Width, (float)(point.Y - .5));
    }

    private long FootballVisualRevision()
    {
        SyncFootballSession();
        _boardSession.TickFootball(_footballClock());
        return _boardSession.FootballState.Revision;
    }

    private FootballRenderer GetFootballRenderer(CanvasDevice device)
    {
        if (_footballRenderer is null || _footballDevice != device)
        {
            DisposeFootballResources();
            _footballDevice = device;
            _footballRenderer = new(device);
        }
        return _footballRenderer;
    }

    public void DrawFootballPreview(CanvasDrawingSession ds, float width, float height)
    {
        ds.Clear(Color.FromArgb(255, 9, 20, 20));
        if (width <= 0 || height <= 0) return;
        lock (_gate)
        {
            if (_disposed || _boardSession.Screen != BoardScreen.Football) return;
            if (!PrepareBoardArtwork(ds.Device))
            {
                DrawArtworkLoading(ds, new Rect(0, 0, width, height));
                return;
            }
            FootballVisualRevision();
            double aspect = PaintBoardAspect();
            float drawWidth = (float)Math.Min(width, height * aspect), drawHeight = (float)(drawWidth / aspect);
            var previous = ds.Transform;
            ds.Transform = Matrix3x2.CreateScale(drawWidth / BoardSurfaceSize, drawHeight / BoardSurfaceSize) *
                Matrix3x2.CreateTranslation((width - drawWidth) / 2, (height - drawHeight) / 2) * previous;
            try
            {
                DrawFootballSurface(ds);
                DrawFootballControls(ds);
            }
            finally { ds.Transform = previous; }
        }
    }

    private void DrawFootballSurface(CanvasDrawingSession ds)
    {
        ds.FillRectangle(new Rect(0, 0, BoardSurfaceSize, BoardSurfaceSize), Color.FromArgb(255, 13, 30, 25));
        var field = FootballFieldBounds();
        float aspect = (float)PaintBoardAspect();
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(1 / aspect, 1) * previous;
        try
        {
            GetFootballRenderer(ds.Device).Draw(ds,
                new Rect(field.X * BoardSurfaceSize * aspect, field.Y * BoardSurfaceSize,
                    field.Width * BoardSurfaceSize * aspect, field.Height * BoardSurfaceSize), _boardSession.FootballState);
        }
        finally { ds.Transform = previous; }

        var state = _boardSession.FootballState;
        using var score = new CanvasTextFormat
        {
            FontFamily = "Bahnschrift", FontSize = 44, HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap
        };
        using var caption = new CanvasTextFormat
        {
            FontFamily = "Segoe UI", FontSize = 19, HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap
        };
        DrawBoardAspectText(ds, $"{state.Score1}  :  {state.Score2}", new Rect(340, 9, 320, 61),
            Color.FromArgb(255, 248, 247, 226), score);
        DrawBoardAspectText(ds, "PLAYER 1", new Rect(60, 20, 260, 40), Color.FromArgb(255, 255, 186, 121), caption);
        DrawBoardAspectText(ds, state.Mode == FootballMode.HumanVsAi ? "COMPUTER" : "PLAYER 2", new Rect(680, 20, 260, 40),
            Color.FromArgb(255, 116, 206, 239), caption);
        // Keep every phase message off the playing surface. A dark countdown
        // plaque over a real bar merges with its ink and repeatedly loses it.
        if (state.Phase is FootballPhase.Countdown or FootballPhase.Goal or FootballPhase.Finished)
        { caption.FontFamily = "Bahnschrift"; caption.FontSize = 28; }
        DrawBoardAspectText(ds, state.Phase == FootballPhase.Playing ? "FIRST TO FIVE  ·  DEFEND YOUR GOAL" : state.Banner,
            new Rect(40, 76, 920, 39), Color.FromArgb(255, 221, 230, 210), caption);
    }

    private static Rect FootballButtonTextRect(BoardButton button) => new(button.Bounds.X * BoardSurfaceSize + 12,
        button.Bounds.Y * BoardSurfaceSize + 12, button.Bounds.Width * BoardSurfaceSize - 24, button.Bounds.Height * BoardSurfaceSize - 24);

    private static CanvasTextFormat FootballButtonTextFormat() => new()
    {
        FontFamily = "Bahnschrift", FontSize = 28, HorizontalAlignment = CanvasHorizontalAlignment.Center,
        VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap
    };

    private void DrawFootballControls(CanvasDrawingSession ds)
    {
        using var format = FootballButtonTextFormat();
        foreach (var button in _boardSession.Buttons)
        {
            var b = button.Bounds;
            var rect = new Rect(b.X * BoardSurfaceSize, b.Y * BoardSurfaceSize, b.Width * BoardSurfaceSize, b.Height * BoardSurfaceSize);
            ds.FillRoundedRectangle(rect, 14, 14, Color.FromArgb(255, 221, 229, 211));
            ds.DrawRoundedRectangle(rect, 14, 14, Color.FromArgb(255, 130, 163, 133), 2);
            DrawBoardAspectText(ds, button.Label, FootballButtonTextRect(button), Color.FromArgb(255, 21, 46, 36), format);
        }
    }

    private HandTrackingBounds FootballButtonTextRegion(CanvasDevice device, BoardButton button)
    {
        using var format = FootballButtonTextFormat();
        return BoardAspectButtonTextRegion(device, button, format, FootballButtonTextRect(button));
    }

    // Null until the background pitch build publishes; the menu then redraws.
    private CanvasBitmap? FootballMenuThumbnail(CanvasDevice device)
    {
        var renderer = GetFootballRenderer(device);
        if (!renderer.PitchReady) return null;
        if (_footballThumbnail is null)
        {
            _footballThumbnail = new(device, 1200, 600, 96);
            using var drawing = _footballThumbnail.CreateDrawingSession();
            renderer.DrawThumbnail(drawing, new Rect(0, 0, 1200, 600));
        }
        return _footballThumbnail;
    }

    private void DisposeFootballResources()
    {
        _footballThumbnail?.Dispose();
        _footballThumbnail = null;
        _footballRenderer?.Dispose();
        _footballRenderer = null;
        _footballDevice = null;
        _footballArtworkPublished = false;
    }
}
