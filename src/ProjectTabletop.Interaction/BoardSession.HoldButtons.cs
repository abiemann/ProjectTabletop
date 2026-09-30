namespace ProjectTabletop.Interaction;

/// <summary>How far a held long-press button is toward acting, from 0 to 1.</summary>
public readonly record struct BoardHoldProgress(string ButtonId, double Progress);

public sealed partial class BoardSession
{
    // Placed along the viewer's edge, hold buttons answer quick repeated
    // engagement without fingertip recognition or a selection gesture.
    public static readonly TimeSpan HoldActivationInterval = TimeSpan.FromSeconds(1);
    // The camera evidence may miss a frame; a longer gap releases the hold.
    public static readonly TimeSpan HoldEvidenceGap = TimeSpan.FromMilliseconds(350);
    private readonly Dictionary<string, HoldState> _holds = [];
    // Single-action holds stay spent at their place, even when the action puts
    // another button there (Globe's ^ becomes v; Blackjack's Deal becomes Double
    // and Split), until the fingers lift. A spent place blocks every hold
    // button that overlaps it.
    private readonly Dictionary<BoardRect, SpentHold> _spentHolds = [];

    private sealed class SpentHold
    {
        public DateTimeOffset? AbsentSince { get; set; }
    }

    private sealed class HoldState(DateTimeOffset started)
    {
        public DateTimeOffset Started { get; } = started;
        public DateTimeOffset LastSeen { get; set; } = started;
        public int Activations { get; set; }
    }

    /// <summary>
    /// Records one fresh camera frame of hold evidence: the enabled hold buttons whose
    /// caption is covered. A button activates after a second of continuous evidence; a
    /// repeating one again after each further second, a single-action one only after the
    /// camera has seen its place uncovered for <see cref="HoldEvidenceGap"/>. Any frame
    /// covering more than one button's caption activates nothing and restarts every hold.
    /// Returns the buttons activated by this frame.
    /// </summary>
    public IReadOnlyList<string> ObserveHeldButtons(IReadOnlyCollection<string> heldIds,
        DateTimeOffset frameTime, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(heldIds);
        if (frameTime > now || now - frameTime > ObservationLifetime) return [];
        // Like any camera frame, hold evidence advances timed presentations.
        AdvanceBlackjackPresentation(now);
        AdvanceMonopolyPresentation(now);
        AdvanceGlobeDrawer(now);
        AdvanceSlots(now);
        var holdButtons = Buttons.Where(button => button.IsHold && button.Enabled)
            .ToDictionary(button => button.Id);
        // Fingers still resting on a place keep it spent while its button is
        // disabled (a slot spin, dealing cards), so it cannot act again once
        // re-enabled under the same fingers.
        var everyHold = Buttons.Where(button => button.IsHold).ToDictionary(button => button.Id);
        var heldPlaces = heldIds.Where(everyHold.ContainsKey).Select(id => everyHold[id].Bounds).ToArray();
        foreach (var (place, spent) in _spentHolds.ToArray())
        {
            if (heldPlaces.Any(held => Overlaps(held, place))) spent.AbsentSince = null;
            else if (frameTime - (spent.AbsentSince ??= frameTime) >= HoldEvidenceGap) _spentHolds.Remove(place);
        }
        foreach (var (id, hold) in _holds.ToArray())
            if (!holdButtons.ContainsKey(id) ||
                !heldIds.Contains(id) && frameTime - hold.LastSeen > HoldEvidenceGap)
                _holds.Remove(id);
        // Safety: evidence over more than one button is ambiguous. Nothing acts,
        // and a long press needs a full second as the only covered caption.
        var currentIds = Buttons.Select(button => button.Id).ToHashSet();
        if (heldIds.Distinct().Count(currentIds.Contains) > 1)
        {
            _holds.Clear();
            return [];
        }
        var activated = new List<string>();
        foreach (string id in heldIds.Distinct())
        {
            if (!holdButtons.TryGetValue(id, out var button) ||
                _spentHolds.Keys.Any(place => Overlaps(place, button.Bounds))) continue;
            if (!_holds.TryGetValue(id, out var hold)) _holds[id] = hold = new HoldState(frameTime);
            // A delayed or repeated camera frame cannot extend or advance a hold.
            else if (frameTime <= hold.LastSeen) continue;
            hold.LastSeen = frameTime;
            if (frameTime - hold.Started < HoldActivationInterval * (hold.Activations + 1)) continue;
            hold.Activations++;
            if (button.Hold == BoardButtonHold.Once)
            {
                _holds.Remove(id);
                _spentHolds[button.Bounds] = new SpentHold();
            }
            if (SelectButton(button, now)) activated.Add(id);
        }
        return activated;
    }

    /// <summary>
    /// Each held button's progress toward its next activation, 0 to 1, for on-board
    /// feedback. Holds without camera evidence for <see cref="HoldEvidenceGap"/> are omitted.
    /// </summary>
    public IReadOnlyList<BoardHoldProgress> HoldProgress(DateTimeOffset now) => _holds
        .Where(pair => now >= pair.Value.LastSeen && now - pair.Value.LastSeen <= HoldEvidenceGap)
        .Select(pair => new BoardHoldProgress(pair.Key, Math.Clamp(
            (now - pair.Value.Started) / HoldActivationInterval - pair.Value.Activations, 0, 1)))
        .Where(progress => progress.Progress > 0)
        .ToArray();

    private static bool Overlaps(BoardRect first, BoardRect second) =>
        first.X < second.X + second.Width && second.X < first.X + first.Width &&
        first.Y < second.Y + second.Height && second.Y < first.Y + first.Height;

    private void ClearHolds()
    {
        _holds.Clear();
        _spentHolds.Clear();
    }
}
