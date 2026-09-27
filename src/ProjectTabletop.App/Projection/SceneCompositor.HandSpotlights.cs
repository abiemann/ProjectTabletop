using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.UI;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private static readonly TimeSpan SpotlightHold = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SpotlightLifetime = TimeSpan.FromMilliseconds(700);
    private const float SpotlightCoreFraction = .96f;
    private LitHand[] _handSpotlights = [];
    private sealed record LitHand(HandSpotlight Light, long TrackingId);
    private DateTimeOffset _spotlightFrameTime;
    private DateTimeOffset _spotlightObservationFrameTime;
    private DateTimeOffset _spotlightResetTime;
    private long _spotlightUpdateCount;
    private long _spotlightResetCount;

    public sealed record HandLightingDiagnostics(string Board, bool BoardClipReady, bool BlackOutput,
        bool BoardSetup, int CalibrationTarget, DateTimeOffset SourceFrameTime, double? SourceAgeMilliseconds,
        float Opacity, long GeometryUpdates, long Resets, double ProjectorAspect,
        double[]? CameraToProjector, HandSpotlight[] Lights, long[] SuppressedHandIds);

    public HandLightingDiagnostics GetHandLightingDiagnostics()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            return new(_boardSession.Screen.ToString(), _boardMediaClip is not null, _blackOutput,
                _boardSetup, _calibrationTarget, _spotlightFrameTime,
                _spotlightFrameTime == DateTimeOffset.MinValue ? null : (now - _spotlightFrameTime).TotalMilliseconds,
                SpotlightOpacity(now), _spotlightUpdateCount, _spotlightResetCount, _displayAspect,
                _boardCameraMap?.ToMatrix(), _handSpotlights.Select(hand => hand.Light).ToArray(),
                _suppressedHandLights.Select(hand => hand.TrackingId).ToArray());
        }
    }

    public int ActiveHandSpotlightCount
    {
        get { lock (_gate) return SpotlightOpacity(DateTimeOffset.UtcNow) > 0 ? _handSpotlights.Length : 0; }
    }

    // Illumination has its own short dropout hold. It never supplies landmarks,
    // hover positions, or gestures back to the input path.
    public void SetHandSpotlights(IReadOnlyList<HandDetection> hands, DateTimeOffset frameTime)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (frameTime < _spotlightResetTime || frameTime <= _spotlightObservationFrameTime ||
                frameTime > now || now - frameTime > TimeSpan.FromMilliseconds(350)) return;
            if (_boardCameraMap is null || _boardMediaClip is null || _blackOutput || _boardSetup ||
                _calibrationTarget >= 0)
            {
                ClearHandSpotlights();
                return;
            }
            _spotlightObservationFrameTime = frameTime;
            var map = _boardCameraMap.ToMatrix();
            var observations = hands.Where(hand => HandSpotlight.TryCreate(hand, map, _displayAspect, out _))
                .Select(hand => new SpotlightObservation(hand, _spotlightCursorFrameTime == frameTime
                    ? _spotlightCursors.FirstOrDefault(cursor => cursor.Position == hand.IndexTip)?.TrackingId ?? 0 : 0))
                .ToArray();
            var suppressed = MatchSuppressedHandLights(observations, frameTime);
            var lights = new List<LitHand>();
            for (int index = 0; index < observations.Length; index++)
                if (!suppressed.Contains(index) && HandSpotlight.TryCreate(observations[index].Hand, map, _displayAspect, out var light))
                    lights.Add(new(light, observations[index].TrackingId));
            _spotlightObservations = observations;
            if (observations.Length == 0) return; // Preserve the ordinary short dropout hold.
            _handSpotlights = lights.ToArray();
            _spotlightFrameTime = frameTime;
            _spotlightUpdateCount++;
        }
    }

    private void ClearHandSpotlights()
    {
        _handSpotlights = [];
        _spotlightFrameTime = DateTimeOffset.MinValue;
        _spotlightObservationFrameTime = DateTimeOffset.MinValue;
        _spotlightResetTime = DateTimeOffset.UtcNow;
        _spotlightResetCount++;
        _suppressedHandLights.Clear();
        _spotlightObservations = [];
        _knownSpotlightHands.Clear();
        _spotlightCursors = [];
        _spotlightCursorFrameTime = DateTimeOffset.MinValue;
    }

    private float SpotlightOpacity(DateTimeOffset now)
    {
        var age = now - _spotlightFrameTime;
        if (_blackOutput || _boardSetup || _calibrationTarget >= 0 || _boardMediaClip is null ||
            age < TimeSpan.Zero || age >= SpotlightLifetime)
            return 0;
        return age <= SpotlightHold ? 1 :
            (float)((SpotlightLifetime - age).TotalMilliseconds /
                (SpotlightLifetime - SpotlightHold).TotalMilliseconds);
    }

    // Called inside the existing board clip, over the complete scene: drawing
    // labels back over the light would put those dark markings back on the hand.
    private void DrawHandSpotlights(CanvasDrawingSession ds, Rect output)
    {
        var opacity = SpotlightOpacity(DateTimeOffset.UtcNow);
        if (_handSpotlights.Length == 0 || opacity <= 0) return;
        using var photoCopyClip = ClipPhotoCopyHandLighting(ds, output);
        using var brush = new CanvasRadialGradientBrush(ds.Device,
        [
            new CanvasGradientStop { Position = 0, Color = Colors.White },
            new CanvasGradientStop { Position = SpotlightCoreFraction, Color = Colors.White },
            new CanvasGradientStop { Position = 1, Color = Color.FromArgb(0, 255, 255, 255) }
        ]);
        brush.Opacity = opacity;
        foreach (var hand in _handSpotlights)
        {
            var light = hand.Light;
            var center = new Vector2((float)(output.X + light.Center.X * output.Width),
                (float)(output.Y + light.Center.Y * output.Height));
            // The solid white core covers the hand; only the outer margin fades.
            var radius = (float)(light.Radius * output.Height / SpotlightCoreFraction);
            brush.Center = center;
            brush.RadiusX = radius;
            brush.RadiusY = radius;
            ds.FillCircle(center, radius, brush);
        }
    }
}
