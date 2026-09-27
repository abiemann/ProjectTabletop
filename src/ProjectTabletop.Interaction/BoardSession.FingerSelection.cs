namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    private static readonly TimeSpan FingersTogetherDuration = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan IndexSeparationDuration = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan FingerMissGrace = TimeSpan.FromMilliseconds(200);
    private readonly Dictionary<long, FingerTrack> _fingerTracks = new();
    private DateTimeOffset _ignoreFingerFramesThrough = DateTimeOffset.MinValue;

    public IReadOnlyList<BoardFingerSelectionFeedback> FingerSelectionFeedback { get; private set; } =
        Array.Empty<BoardFingerSelectionFeedback>();

    private FingerSelectionCandidate? UpdateFingerSelection(IReadOnlyList<BoardHandSample> hands,
        IReadOnlyList<BoardButton> buttons, DateTimeOffset frameTime)
    {
        if (frameTime <= _ignoreFingerFramesThrough)
        {
            PauseFingerSelection();
            return null;
        }
        var seen = new HashSet<long>();
        var feedback = new List<BoardFingerSelectionFeedback>();
        FingerSelectionCandidate? ready = null;
        foreach (var hand in hands)
        {
            if (hand.TrackingId <= 0 || !seen.Add(hand.TrackingId)) continue;
            if (!_fingerTracks.TryGetValue(hand.TrackingId, out var track))
            {
                if (_fingerTracks.Count >= 128)
                    _fingerTracks.Remove(_fingerTracks.MinBy(pair => pair.Value.LastObserved).Key);
                track = new FingerTrack();
                _fingerTracks.Add(hand.TrackingId, track);
            }
            if (frameTime - track.LastObserved > ObservationLifetime ||
                frameTime - track.LastPoseTime > ObservationLifetime) track.Clear();
            track.LastObserved = frameTime;
            if (hand.FingerAim is not { } aim || !Finite(aim))
            {
                track.WasMissing = true;
                continue;
            }
            bool together = hand.FourFingersExtended && hand.FingersTogether && !hand.IndexFingerSeparated;
            bool separated = hand.FourFingersExtended && hand.IndexFingerSeparated && !hand.FingersTogether;
            var target = buttons.FirstOrDefault(button => button.Enabled && button.Bounds.Contains(aim.U, aim.V));
            if (together)
            {
                if (target is null)
                {
                    track.Clear();
                    continue;
                }
                if (track.TargetId != target.Id || track.Stage is not (BoardFingerSelectionStage.Arming or BoardFingerSelectionStage.Armed))
                {
                    track.Clear();
                    track.TargetId = target.Id;
                    track.Stage = BoardFingerSelectionStage.Arming;
                }
                if (track.Stage == BoardFingerSelectionStage.Arming)
                {
                    track.ObserveEvidence(frameTime);
                    if (track.Samples >= 2 && track.Evidence >= FingersTogetherDuration)
                        track.Stage = BoardFingerSelectionStage.Armed;
                }
                // While the fingers are together the middle tip is free to aim.
                // Freeze this target and point only when separation starts.
                track.Anchor = aim;
                track.TargetBounds = target.Bounds;
                track.LastPoseTime = frameTime;
                track.WasMissing = false;
            }
            else
            {
                var armedTarget = buttons.FirstOrDefault(button => button.Enabled && button.Id == track.TargetId);
                if (armedTarget is null || !track.WithinAnchor(aim))
                {
                    track.Clear();
                    continue;
                }
                if (separated)
                {
                    if (track.Stage == BoardFingerSelectionStage.Armed)
                    {
                        track.Stage = BoardFingerSelectionStage.Separating;
                        track.ResetEvidence();
                    }
                    if (track.Stage == BoardFingerSelectionStage.Separating)
                    {
                        track.ObserveEvidence(frameTime);
                        if (track.Samples >= 2 && track.Evidence >= IndexSeparationDuration)
                            ready ??= new FingerSelectionCandidate(armedTarget, track, track.Anchor);
                    }
                    else if (track.Stage != BoardFingerSelectionStage.Selected)
                    {
                        // A hand first seen apart, or opened before it was
                        // armed, cannot select anything by remaining apart.
                        track.Clear();
                        continue;
                    }
                    track.LastPoseTime = frameTime;
                    track.WasMissing = false;
                }
                else
                {
                    // The gap between closed/open geometry thresholds pauses
                    // confirmation, without losing a brief natural transition.
                    track.WasMissing = true;
                }
            }
            if (track.TargetId is { } id && track.Stage is { } stage)
            {
                double progress = stage switch
                {
                    BoardFingerSelectionStage.Arming => track.Evidence.TotalMilliseconds / FingersTogetherDuration.TotalMilliseconds,
                    BoardFingerSelectionStage.Separating => track.Evidence.TotalMilliseconds / IndexSeparationDuration.TotalMilliseconds,
                    _ => 1
                };
                feedback.Add(new(id, stage, Math.Clamp(progress, 0, 1)));
            }
        }
        foreach (var pair in _fingerTracks)
        {
            if (seen.Contains(pair.Key)) continue;
            pair.Value.WasMissing = true;
            if (frameTime - pair.Value.LastObserved > ObservationLifetime) pair.Value.Clear();
        }
        FingerSelectionFeedback = feedback.GroupBy(item => item.ButtonId)
            .Select(group => group.OrderBy(item => item.Stage == BoardFingerSelectionStage.Selected ? -1 : (int)item.Stage)
                .ThenBy(item => item.Progress).Last()).ToArray();
        return ready;
    }

    private BoardButton? FingerHoverTarget(BoardHandSample hand, IReadOnlyList<BoardButton> buttons)
    {
        if (hand.FingerAim is not { } aim || !Finite(aim)) return null;
        if (_fingerTracks.TryGetValue(hand.TrackingId, out var track) &&
            track.Stage is BoardFingerSelectionStage.Armed or BoardFingerSelectionStage.Separating or BoardFingerSelectionStage.Selected &&
            track.WithinAnchor(aim))
            return buttons.FirstOrDefault(button => button.Enabled && button.Id == track.TargetId);
        return buttons.FirstOrDefault(button => button.Enabled && button.Bounds.Contains(aim.U, aim.V));
    }

    private static void MarkFingerSelection(FingerSelectionCandidate selected, DateTimeOffset frameTime)
    {
        selected.Track.TargetId = selected.Button.Id;
        selected.Track.TargetBounds = selected.Button.Bounds;
        selected.Track.Anchor = selected.Anchor;
        selected.Track.Stage = BoardFingerSelectionStage.Selected;
        selected.Track.LastPoseTime = selected.Track.LastObserved = frameTime;
    }

    private void PauseFingerSelection()
    {
        foreach (var track in _fingerTracks.Values) track.WasMissing = true;
    }

    private void InvalidateFingerSelection(DateTimeOffset now)
    {
        _ignoreFingerFramesThrough = Later(_ignoreFingerFramesThrough, now);
        FingerSelectionFeedback = Array.Empty<BoardFingerSelectionFeedback>();
        foreach (var track in _fingerTracks.Values) track.Clear();
    }

    private static bool Finite(BoardAim aim) => double.IsFinite(aim.U) && double.IsFinite(aim.V);
    private sealed record FingerSelectionCandidate(BoardButton Button, FingerTrack Track, BoardAim Anchor);

    private sealed class FingerTrack
    {
        public string? TargetId;
        public BoardRect TargetBounds;
        public BoardAim Anchor;
        public BoardFingerSelectionStage? Stage;
        public DateTimeOffset LastObserved;
        public DateTimeOffset LastPoseTime;
        public DateTimeOffset LastEvidenceTime;
        public TimeSpan Evidence;
        public int Samples;
        public bool WasMissing;

        public bool WithinAnchor(BoardAim aim)
        {
            var bounds = TargetBounds;
            return Math.Pow(aim.U - Anchor.U, 2) + Math.Pow(aim.V - Anchor.V, 2) <= .04 * .04 &&
                new BoardRect(bounds.X - .012, bounds.Y - .012, bounds.Width + .024, bounds.Height + .024).Contains(aim.U, aim.V);
        }

        public void ObserveEvidence(DateTimeOffset frameTime)
        {
            var gap = frameTime - LastEvidenceTime;
            if (Samples == 0 || gap > ObservationLifetime || WasMissing && gap > FingerMissGrace)
            {
                Evidence = TimeSpan.Zero;
                Samples = 1;
            }
            else
            {
                if (!WasMissing) Evidence += gap;
                Samples = Math.Min(2, Samples + 1);
            }
            LastEvidenceTime = frameTime;
        }

        public void ResetEvidence()
        {
            Evidence = TimeSpan.Zero;
            Samples = 0;
            WasMissing = false;
        }

        public void Clear()
        {
            TargetId = null;
            Stage = null;
            ResetEvidence();
        }
    }
}
