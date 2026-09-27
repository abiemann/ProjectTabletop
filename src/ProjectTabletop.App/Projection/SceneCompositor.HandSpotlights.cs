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
    private sealed record LitHand(HandSpotlight Light, long TrackingId, HandDetection Hand, DateTimeOffset SourceFrameTime);
    private DateTimeOffset _spotlightFrameTime;
    private DateTimeOffset _spotlightObservationFrameTime;
    private DateTimeOffset _spotlightResetTime;
    private long _spotlightUpdateCount;
    private long _spotlightResetCount;

    public sealed record HandLightingDiagnostics(string Board, bool BoardClipReady, bool BlackOutput,
        bool BoardSetup, int CalibrationTarget, DateTimeOffset SourceFrameTime, double? SourceAgeMilliseconds,
        float Opacity, long GeometryUpdates, long Resets, double ProjectorAspect,
        double[]? CameraToProjector, HandSpotlight[] Lights, long[] SuppressedHandIds,
        HandLightLifetimeDiagnostics[] LightLifetimes);

    public sealed record HandLightLifetimeDiagnostics(long TrackingId, DateTimeOffset SourceFrameTime,
        double SourceAgeMilliseconds, float Opacity);

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
                _suppressedHandLights.Select(hand => hand.TrackingId).ToArray(),
                _handSpotlights.Select(hand => new HandLightLifetimeDiagnostics(hand.TrackingId, hand.SourceFrameTime,
                    (now - hand.SourceFrameTime).TotalMilliseconds, SpotlightOpacity(now, hand.SourceFrameTime))).ToArray());
        }
    }

    public int ActiveHandSpotlightCount
    {
        get
        {
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                return _handSpotlights.Count(hand => SpotlightOpacity(now, hand.SourceFrameTime) > 0);
            }
        }
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
            if (_boardCameraMap is null || _boardMediaClip is null || _blackOutput || _boardSetup || IsBoardRevealActive ||
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
            var knownIds = _knownSpotlightHands.Keys.ToHashSet();
            var suppressed = MatchSuppressedHandLights(observations, frameTime);
            var previous = _handSpotlights.Where(hand => SpotlightOpacity(now, hand.SourceFrameTime) > 0).ToArray();
            var matched = new HashSet<int>();
            var matchedObservations = new HashSet<int>();
            // Match all stable identities first so a nearby second hand cannot
            // claim the first hand's retained light through proximity alone.
            for (int index = 0; index < observations.Length; index++)
            {
                var observation = observations[index];
                int old = observation.TrackingId > 0
                    ? Array.FindIndex(previous, hand => hand.TrackingId == observation.TrackingId) : -1;
                if (old >= 0 && matched.Add(old)) matchedObservations.Add(index);
            }
            for (int index = 0; index < observations.Length; index++)
            {
                if (matchedObservations.Contains(index)) continue;
                // A known hand without a light may be deliberately suppressed.
                // It must not consume a different missing hand's cached light.
                if (observations[index].TrackingId > 0 && knownIds.Contains(observations[index].TrackingId)) continue;
                int nearest = -1;
                double distance = .9;
                for (int old = 0; old < previous.Length; old++)
                {
                    if (matched.Contains(old)) continue;
                    double candidate = SpotlightHandDistance(previous[old].Hand, observations[index].Hand);
                    if (candidate < distance) { nearest = old; distance = candidate; }
                }
                // Reacquisition can assign a new tracker ID. Coalesce the old
                // light, including when this observation is deliberately dark.
                if (nearest >= 0) matched.Add(nearest);
            }
            var lights = new List<LitHand>();
            for (int index = 0; index < observations.Length; index++)
                if (!suppressed.Contains(index) && HandSpotlight.TryCreate(observations[index].Hand, map, _displayAspect, out var light))
                    lights.Add(new(light, observations[index].TrackingId, observations[index].Hand, frameTime));
            // One missing hand gets its own grace period even if a second hand
            // (or an off-board false positive) continues producing fresh results.
            // Never refresh a held light's timestamp or use it as gesture input.
            lights.AddRange(previous.Where((_, index) => !matched.Contains(index))
                .OrderByDescending(hand => hand.SourceFrameTime).Take(Math.Max(0, 2 - observations.Length)));
            _spotlightObservations = observations;
            _handSpotlights = lights.ToArray();
            if (lights.Count > 0) _spotlightFrameTime = lights.Max(hand => hand.SourceFrameTime);
            if (observations.Length > 0) _spotlightUpdateCount++;
        }
    }

    private static double SpotlightHandDistance(HandDetection first, HandDetection second)
    {
        static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        var a = first.Landmarks; var b = second.Landmarks;
        double scale = Math.Max(Math.Max(Distance(a[0], a[9]), Distance(a[5], a[17])),
            Math.Max(Distance(b[0], b[9]), Distance(b[5], b[17])));
        return scale > 0 ? (Distance(a[0], b[0]) + Distance(a[9], b[9])) / (2 * scale) : double.PositiveInfinity;
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

    private float SpotlightOpacity(DateTimeOffset now) => _handSpotlights.Length == 0 ? 0 :
        _handSpotlights.Max(hand => SpotlightOpacity(now, hand.SourceFrameTime));

    private float SpotlightOpacity(DateTimeOffset now, DateTimeOffset sourceFrameTime)
    {
        var age = now - sourceFrameTime;
        if (_blackOutput || _boardSetup || IsBoardRevealActive || _calibrationTarget >= 0 || _boardMediaClip is null ||
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
        var now = DateTimeOffset.UtcNow;
        if (_handSpotlights.Length == 0 || SpotlightOpacity(now) <= 0) return;
        using var brush = new CanvasRadialGradientBrush(ds.Device,
        [
            new CanvasGradientStop { Position = 0, Color = Colors.White },
            new CanvasGradientStop { Position = SpotlightCoreFraction, Color = Colors.White },
            new CanvasGradientStop { Position = 1, Color = Color.FromArgb(0, 255, 255, 255) }
        ]);
        foreach (var hand in _handSpotlights)
        {
            brush.Opacity = SpotlightOpacity(now, hand.SourceFrameTime);
            if (brush.Opacity <= 0) continue;
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
