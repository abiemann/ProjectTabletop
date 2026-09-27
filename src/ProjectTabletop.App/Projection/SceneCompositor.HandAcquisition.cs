using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.UI;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // This is acquisition assistance, never a hand observation or gesture.
    // Learn the camera appearance of a known, stationary rendered table: its
    // projected colors differ from RGB because of the projector and exposure.
    private static readonly TimeSpan AcquisitionSettle = TimeSpan.FromMilliseconds(450);
    // Webcam timestamps are CPU-arrival times, not exposure times. Give the
    // visibly delayed camera image time to lose our own white disk after off.
    private static readonly TimeSpan AcquisitionLightOffSettle = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan AcquisitionLightDuration = TimeSpan.FromMilliseconds(900);
    private AcquisitionSceneState? _acquisitionScene;
    private long _acquisitionRevision;
    private DateTimeOffset _acquisitionQuietUntil;
    private HandAcquisitionHint? _acquisitionHint;
    private HandSpotlight? _acquisitionLight;
    private DateTimeOffset _acquisitionLightUntil;
    private DateTimeOffset _acquisitionLightStarted;
    private HandAcquisitionSceneImage? _acquisitionExpectedScene;
    private DateTimeOffset _acquisitionReferenceRetryAt;
    private string? _acquisitionReferenceError;
    private string _acquisitionReason = "inactive";
    private long _acquisitionLightCount;

    private readonly record struct AcquisitionSceneState(long Navigation, long Game, long Flights, long LightReset);
    public sealed record HandAcquisitionContext(long Revision, bool ObserveMotion,
        PixelPoint[] SearchPolygon, HandAcquisitionHint? IlluminatedHint,
        HandAcquisitionSceneImage? ExpectedScene = null, DateTimeOffset IlluminationStartedAt = default,
        PixelPoint[]? StationarySearchCenters = null);

    public HandAcquisitionContext? GetHandAcquisitionContext(DateTimeOffset frameTime)
    {
        lock (_gate)
        {
            var now = _blackjackClock();
            if (!AcquisitionBoardReady)
            {
                ClearAcquisitionLight();
                _acquisitionScene = null;
                _acquisitionExpectedScene = null;
                _acquisitionReferenceError = null;
                _acquisitionReferenceRetryAt = default;
                _acquisitionReason = "inactive";
                return null;
            }
            var flights = GetBlackjackFlights(now);
            var state = new AcquisitionSceneState(_boardSession.Revision, _boardSession.BlackjackState.Revision,
                _blackjackFlightRevision, _spotlightResetCount);
            if (_acquisitionScene != state)
            {
                _acquisitionScene = state;
                _acquisitionRevision++;
                _acquisitionExpectedScene = null;
                _acquisitionReferenceRetryAt = default;
                ClearAcquisitionLight();
                _acquisitionQuietUntil = now + AcquisitionSettle;
            }
            // Include the lower felt around the controls so entering fingers can
            // suggest a crop even when the palm is just outside the board.
            var polygon = new[] { new Point2(.025, .64), new Point2(.975, .64),
                new Point2(.975, .995), new Point2(.025, .995) }
                .Select(point => _boardCameraMap!.InverseTransform(_boardSurfaceMap!.Transform(point)))
                .Select(point => new PixelPoint(point.X, point.Y)).ToArray();
            HandAcquisitionContext Context(bool observe, HandAcquisitionHint? illuminated)
            {
                if ((observe || illuminated is not null) && _acquisitionExpectedScene is null && now >= _acquisitionReferenceRetryAt)
                    _acquisitionExpectedScene = CaptureExpectedAcquisitionScene();
                var centers = new[] { .13, .37, .63, .87 }.Select(u =>
                    _boardCameraMap!.InverseTransform(_boardSurfaceMap!.Transform(new(u, .90))))
                    .Select(point => new PixelPoint(point.X, point.Y)).ToArray();
                return new(_acquisitionRevision, observe, polygon, illuminated,
                    _acquisitionExpectedScene, _acquisitionLightStarted, centers);
            }
            if (flights.Length > 0 || HasAcquiredHandOrSuppression(now))
            {
                ClearAcquisitionLight();
                _acquisitionQuietUntil = now + AcquisitionSettle;
                _acquisitionReason = flights.Length > 0 ? "table-animation" : "hand-or-execute-suppression";
                return Context(false, null);
            }
            if (_acquisitionHint is not null && now < _acquisitionLightUntil)
            {
                _acquisitionReason = "illuminated-search";
                return Context(false, _acquisitionHint);
            }
            if (_acquisitionHint is not null)
            {
                _acquisitionQuietUntil = _acquisitionLightUntil + AcquisitionLightOffSettle;
                ClearAcquisitionLight();
            }
            bool ready = now >= _acquisitionQuietUntil && frameTime >= _acquisitionQuietUntil &&
                frameTime <= now && now - frameTime <= TimeSpan.FromMilliseconds(350);
            _acquisitionReason = ready ? "watching-button-area" : "settling-rendered-scene";
            return Context(ready, null);
        }
    }

    private bool AcquisitionBoardReady => !_disposed && _boardSession.Screen == BoardScreen.Blackjack &&
        !_blackOutput && !_boardSetup && !IsBoardRevealActive && _calibrationTarget < 0 &&
        _boardMediaClip is not null && _boardCameraMap is not null && _boardSurfaceMap is not null;

    private bool HasAcquiredHandOrSuppression(DateTimeOffset now) => _suppressedHandLights.Count > 0 ||
        _handSpotlights.Any(hand => SpotlightOpacity(now, hand.SourceFrameTime) > 0) ||
        _spotlightCursors.Length > 0 && now >= _spotlightCursorFrameTime &&
        now - _spotlightCursorFrameTime <= TimeSpan.FromMilliseconds(350);

    public void CompleteHandAcquisition(HandAcquisitionContext? requested,
        IReadOnlyList<HandAcquisitionHint> hints, IReadOnlyList<HandDetection> hands, DateTimeOffset frameTime,
        bool? illuminatedPresence = null)
    {
        lock (_gate)
        {
            var now = _blackjackClock();
            var current = GetHandAcquisitionContext(frameTime);
            if (current is null || requested is null || requested.Revision != current.Revision) return;
            if (hands.Count > 0)
            {
                ClearAcquisitionLight();
                _acquisitionQuietUntil = now + AcquisitionSettle;
                return;
            }
            if (current.IlluminatedHint is not null && requested.IlluminatedHint is not null &&
                current.IlluminationStartedAt == requested.IlluminationStartedAt && frameTime <= now &&
                now - frameTime <= TimeSpan.FromMilliseconds(350))
            {
                // Keep searching a stationary foreground object using current
                // camera evidence. The projected white disk alone cannot renew it.
                if (illuminatedPresence == true)
                    _acquisitionLightUntil = frameTime + TimeSpan.FromMilliseconds(800);
                else if (illuminatedPresence == false)
                {
                    ClearAcquisitionLight();
                    _acquisitionQuietUntil = now + AcquisitionLightOffSettle;
                    _acquisitionReason = "foreground-left";
                }
                return;
            }
            if (!current.ObserveMotion || _acquisitionHint is not null || hints.Count == 0) return;
            var hint = hints[0];
            if (hint.ObservedAt > frameTime || frameTime - hint.ObservedAt > TimeSpan.FromMilliseconds(450) ||
                !double.IsFinite(hint.RadiusPixels) || hint.RadiusPixels <= 0) return;
            var center = _boardCameraMap!.Transform(new(hint.Center.X, hint.Center.Y));
            var board = _boardSurfaceMap!.InverseTransform(center);
            if (board.X is < .025 or > .975 || board.Y is < .64 or > .995) return;
            double radius = 0;
            for (int i = 0; i < 8; i++)
            {
                double angle = i * Math.PI / 4;
                var point = _boardCameraMap.Transform(new(hint.Center.X + Math.Cos(angle) * hint.RadiusPixels,
                    hint.Center.Y + Math.Sin(angle) * hint.RadiusPixels));
                radius = Math.Max(radius, Math.Sqrt(Math.Pow((point.X - center.X) * _displayAspect, 2) +
                    Math.Pow(point.Y - center.Y, 2)));
            }
            if (!double.IsFinite(radius) || radius <= 0 || !double.IsFinite(center.X) || !double.IsFinite(center.Y)) return;
            _acquisitionHint = hint;
            _acquisitionLight = new(new(center.X, center.Y), Math.Min(radius, .24));
            _acquisitionLightUntil = now + AcquisitionLightDuration;
            _acquisitionLightStarted = now;
            _acquisitionLightCount++;
            _acquisitionReason = "illuminated-search";
        }
    }

    public object GetHandAcquisitionDiagnostics()
    {
        lock (_gate)
        {
            var now = _blackjackClock();
            var context = GetHandAcquisitionContext(now);
            return new { revision = _acquisitionRevision, reason = _acquisitionReason,
                observingMotion = context?.ObserveMotion ?? false, searchPolygon = context?.SearchPolygon,
                searchLightActive = context?.IlluminatedHint is not null, light = _acquisitionLight,
                lightUntil = _acquisitionLightUntil, lightCount = _acquisitionLightCount,
                expectedSceneReady = _acquisitionExpectedScene is not null,
                expectedSceneError = _acquisitionReferenceError,
                hint = context?.IlluminatedHint };
        }
    }

    private void ClearAcquisitionLight()
    {
        _acquisitionHint = null;
        _acquisitionLight = null;
        _acquisitionLightUntil = DateTimeOffset.MinValue;
        _acquisitionLightStarted = DateTimeOffset.MinValue;
    }

    private HandAcquisitionSceneImage? CaptureExpectedAcquisitionScene()
    {
        if (_boardApplicationTarget is null || _renderedBoardState is not { Screen: BoardScreen.Blackjack } rendered ||
            rendered.BlackjackRevision != _boardSession.BlackjackState.Revision ||
            rendered.BlackjackFlightRevision != _blackjackFlightRevision ||
            rendered.HoverMask != 0 || rendered.FingerSelectionStep != 0) return null;
        var cameraToProjector = _boardCameraMap!.ToMatrix();
        var projectorToBoard = _boardSurfaceMap!.Inverse().ToMatrix();
        var cameraToBoard = new double[9];
        for (int row = 0; row < 3; row++)
        for (int column = 0; column < 3; column++)
        for (int middle = 0; middle < 3; middle++)
            cameraToBoard[row * 3 + column] += projectorToBoard[row * 3 + middle] * cameraToProjector[middle * 3 + column];
        // Capture the unlit generated table once per known scene, before the
        // separate hand/search-light overlay. This is not a webcam background.
        try
        {
            var pixels = _boardApplicationTarget.GetPixelBytes();
            _acquisitionReferenceError = null;
            return new((int)BoardSurfaceSize, (int)BoardSurfaceSize, pixels, cameraToBoard);
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or
            InvalidOperationException or ObjectDisposedException)
        {
            // Device recreation must not escape the camera callback or latch
            // hand inference busy. Native square searches can continue without
            // a rendered reference while the graphics device recovers.
            _acquisitionReferenceError = error.Message;
            _acquisitionReferenceRetryAt = _blackjackClock() + TimeSpan.FromSeconds(1);
            return null;
        }
    }

    // Already inside the physical board clip; never paint beyond it or feed
    // this exploratory illumination back as a confirmed hand or button input.
    private void DrawHandAcquisitionLight(CanvasDrawingSession ds, Rect output)
    {
        var now = _blackjackClock();
        if (!AcquisitionBoardReady || HasAcquiredHandOrSuppression(now) ||
            now >= _acquisitionLightUntil || _acquisitionLight is not { } light ||
            _acquisitionScene is not { } state || state.Navigation != _boardSession.Revision ||
            state.Game != _boardSession.BlackjackState.Revision || state.LightReset != _spotlightResetCount ||
            GetBlackjackFlights(now).Length > 0) return;
        var center = new Vector2((float)(output.X + light.Center.X * output.Width),
            (float)(output.Y + light.Center.Y * output.Height));
        float radius = (float)(light.Radius * output.Height);
        using var brush = new CanvasRadialGradientBrush(ds.Device,
        [
            new() { Position = 0, Color = Colors.White },
            new() { Position = .82f, Color = Colors.White },
            new() { Position = 1, Color = Color.FromArgb(0, 255, 255, 255) }
        ]) { Center = center, RadiusX = radius, RadiusY = radius };
        ds.FillCircle(center, radius, brush);
    }
}
