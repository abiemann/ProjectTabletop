namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    // Placed along the viewer's edge, hold buttons answer quick repeated
    // engagement without fingertip recognition or a selection gesture.
    public static readonly TimeSpan HoldActivationInterval = TimeSpan.FromSeconds(1);
    // The camera evidence may miss a frame; a longer gap releases the hold.
    public static readonly TimeSpan HoldEvidenceGap = TimeSpan.FromMilliseconds(350);
    private readonly Dictionary<string, HoldState> _holds = [];
    // Single-action holds stay spent at their place, even when the action puts
    // another button there (Globe's ^ becomes v), until the fingers lift.
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
    /// camera has seen its place uncovered for <see cref="HoldEvidenceGap"/>. Returns the
    /// buttons activated by this frame.
    /// </summary>
    public IReadOnlyList<string> ObserveHeldButtons(IReadOnlyCollection<string> heldIds,
        DateTimeOffset frameTime, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(heldIds);
        if (frameTime > now || now - frameTime > ObservationLifetime) return [];
        var holdButtons = Buttons.Where(button => button.IsHold && button.Enabled)
            .ToDictionary(button => button.Id);
        var heldPlaces = heldIds.Where(holdButtons.ContainsKey).Select(id => holdButtons[id].Bounds).ToHashSet();
        foreach (var (place, spent) in _spentHolds.ToArray())
        {
            if (heldPlaces.Contains(place)) spent.AbsentSince = null;
            else if (frameTime - (spent.AbsentSince ??= frameTime) >= HoldEvidenceGap) _spentHolds.Remove(place);
        }
        foreach (var (id, hold) in _holds.ToArray())
            if (!holdButtons.ContainsKey(id) ||
                !heldIds.Contains(id) && frameTime - hold.LastSeen > HoldEvidenceGap)
                _holds.Remove(id);
        var activated = new List<string>();
        foreach (string id in heldIds.Distinct())
        {
            if (!holdButtons.TryGetValue(id, out var button) || _spentHolds.ContainsKey(button.Bounds)) continue;
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

    private void ClearHolds()
    {
        _holds.Clear();
        _spentHolds.Clear();
    }
}
