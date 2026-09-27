using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // Fresh missing observations, not elapsed wall time or a tracker ID change,
    // establish removal. Brief losses in the darker image cannot relight a hand.
    private static readonly TimeSpan HandLightRemovalDelay = TimeSpan.FromMilliseconds(900);
    private readonly List<SuppressedHandLight> _suppressedHandLights = [];
    private HandCursor[] _spotlightCursors = [];
    private DateTimeOffset _spotlightCursorFrameTime;
    private SpotlightObservation[] _spotlightObservations = [];
    private readonly Dictionary<long, DateTimeOffset> _knownSpotlightHands = [];
    private long _spotlightExecuteEventId;

    private sealed record SpotlightObservation(HandDetection Hand, long TrackingId);
    private sealed class SuppressedHandLight(long trackingId, PixelPoint tip, HandDetection? hand)
    {
        public long TrackingId = trackingId;
        public PixelPoint Tip = tip;
        public HandDetection? Hand = hand;
        public DateTimeOffset? MissingSince;
        public DateTimeOffset LastMissingFrame;
        public int MissingSamples;
    }

    private void ObserveHandLightingCommands(IReadOnlyList<HandCursor> cursors,
        DateTimeOffset frameTime, DateTimeOffset now, BoardNavigation? selection)
    {
        _spotlightCursors = cursors.ToArray();
        _spotlightCursorFrameTime = frameTime;
        long previousEvent = _spotlightExecuteEventId;
        foreach (var cursor in cursors)
        {
            _spotlightExecuteEventId = Math.Max(_spotlightExecuteEventId, cursor.ExecuteEventId);
            // Includes execute feedback in the tester and Photo Copy pinches
            // outside buttons. A held one-second pulse never creates new state.
            if (cursor.ExecuteEventId > previousEvent && cursor.ExecuteUntil > now &&
                cursor.ExecuteUntil <= now.AddSeconds(1) &&
                cursor.ExecuteUntil.AddSeconds(-1) > _spotlightResetTime)
                SuppressHandLight(cursor);
        }
        if (selection is { Gesture: BoardSelectionGesture.IndexSeparation, TrackingId: > 0 } &&
            cursors.FirstOrDefault(cursor => cursor.TrackingId == selection.TrackingId) is { } selectingHand)
            SuppressHandLight(selectingHand);
    }

    private void SuppressHandLight(HandCursor cursor)
    {
        if (cursor.TrackingId <= 0 || _suppressedHandLights.Any(hand => hand.TrackingId == cursor.TrackingId)) return;
        var previous = _spotlightObservations.FirstOrDefault(hand => hand.TrackingId == cursor.TrackingId);
        _suppressedHandLights.Add(new(cursor.TrackingId, cursor.Position, previous?.Hand));
        // Remove the cached light immediately, including its dropout hold.
        _handSpotlights = _handSpotlights.Where(hand => hand.TrackingId != cursor.TrackingId).ToArray();
    }

    private HashSet<int> MatchSuppressedHandLights(SpotlightObservation[] observations, DateTimeOffset frameTime)
    {
        foreach (long id in _knownSpotlightHands.Where(pair => frameTime - pair.Value > TimeSpan.FromSeconds(2))
            .Select(pair => pair.Key).ToArray()) _knownSpotlightHands.Remove(id);
        var used = new HashSet<int>();
        var matched = new HashSet<SuppressedHandLight>();
        // Stable identity wins over position, even when two hands approach.
        foreach (var state in _suppressedHandLights)
        {
            int index = Array.FindIndex(observations, item => item.TrackingId > 0 && item.TrackingId == state.TrackingId);
            if (index >= 0 && used.Add(index)) Remember(state, observations[index]);
        }
        foreach (var state in _suppressedHandLights.Where(state => !matched.Contains(state)))
        {
            int best = -1;
            double bestDistance = 1.5;
            for (int index = 0; index < observations.Length; index++)
            {
                var candidate = observations[index];
                if (used.Contains(index) || candidate.TrackingId > 0 &&
                    _knownSpotlightHands.ContainsKey(candidate.TrackingId)) continue;
                double distance = HandDistance(state, candidate.Hand);
                if (distance < bestDistance) { best = index; bestDistance = distance; }
            }
            if (best >= 0) { used.Add(best); Remember(state, observations[best]); }
        }
        foreach (var state in _suppressedHandLights.Where(state => !matched.Contains(state)))
        {
            if (frameTime <= state.LastMissingFrame) continue;
            // A stalled camera is not evidence that the physical hand left.
            if (frameTime - state.LastMissingFrame > TimeSpan.FromMilliseconds(350))
            { state.MissingSince = null; state.MissingSamples = 0; }
            state.MissingSince ??= frameTime;
            state.LastMissingFrame = frameTime;
            state.MissingSamples++;
        }
        _suppressedHandLights.RemoveAll(state => state.MissingSamples >= 3 &&
            state.MissingSince is { } since && frameTime - since >= HandLightRemovalDelay);
        foreach (var observation in observations.Where(item => item.TrackingId > 0))
            _knownSpotlightHands[observation.TrackingId] = frameTime;
        return used;

        void Remember(SuppressedHandLight state, SpotlightObservation observation)
        {
            matched.Add(state);
            if (observation.TrackingId > 0) state.TrackingId = observation.TrackingId;
            state.Hand = observation.Hand;
            state.Tip = observation.Hand.IndexTip;
            state.MissingSince = null;
            state.MissingSamples = 0;
            state.LastMissingFrame = frameTime;
        }
    }

    private static double HandDistance(SuppressedHandLight state, HandDetection hand)
    {
        var points = hand.Landmarks;
        double scale = Math.Max(Distance(points[0], points[9]), Distance(points[5], points[17]));
        if (scale <= 0) return double.PositiveInfinity;
        if (state.Hand is not { Landmarks.Count: 21 } previous)
            return Distance(state.Tip, hand.IndexTip) / scale;
        var old = previous.Landmarks;
        scale = Math.Max(scale, Math.Max(Distance(old[0], old[9]), Distance(old[5], old[17])));
        return (Distance(old[0], points[0]) + Distance(old[9], points[9])) / (2 * scale);

        static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    }
}
