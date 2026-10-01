namespace ProjectTabletop.Interaction;

/// <summary>An accepted player press for presentation, independent of slot rules and snapshots.</summary>
public sealed record SlotButtonPress(string ButtonId, BoardRect Bounds, DateTimeOffset StartedAt, long Sequence);

public sealed partial class BoardSession
{
    private readonly SlotGame _slots;
    private long _slotsObservedRevision;
    private long _slotsButtonPressSequence;

    // Every slot control sits on the viewer's edge row, where the wrist leaves
    // the camera view, so each is a long-press hold button acting once per press.
    // Gaps keep one resting hand from covering two captions (the safety rule).
    public static readonly BoardRect SlotExitBounds = new(.03, .855, .15, .115);
    public static readonly BoardRect SlotBetDownBounds = new(.20, .855, .15, .115);
    public static readonly BoardRect SlotBetUpBounds = new(.37, .855, .15, .115);
    public static readonly BoardRect SlotBuyBounds = new(.54, .855, .17, .115);
    public static readonly BoardRect SlotSpinBounds = new(.74, .855, .23, .115);

    public SlotSnapshot SlotsState => _slots.Snapshot;
    /// <summary>The last accepted Bet, Buy or Spin press, retained across navigation.</summary>
    public SlotButtonPress? SlotsLastButtonPress { get; private set; }

    public void ShowSlots(DateTimeOffset? now = null) => Show(BoardScreen.Slots, now ?? DateTimeOffset.UtcNow);

    /// <summary>Makes the next slot spin land a feature (demonstrations and verification).</summary>
    public void DemonstrateSlots(SlotDemo demo) => _slots.Demonstrate(demo);

    /// <summary>Advances the machine's timed presentation while its board is showing.</summary>
    public bool TickSlots(DateTimeOffset now) => AdvanceSlots(now);

    private bool AdvanceSlots(DateTimeOffset now)
    {
        if (Screen != BoardScreen.Slots || !_slots.Tick(now)) return false;
        // Controls re-enable only when a spin fully settles. Any pointing or pulse
        // begun while they were disabled must not land on them afterwards.
        if (_slots.Phase == SlotPhase.Idle && _slotsObservedRevision != _slots.Revision)
        {
            _ignoreExecutionsThrough = Later(_ignoreExecutionsThrough, now);
            _ignoreSelectionsThrough = Later(_ignoreSelectionsThrough, now);
            HoveredButtonIds = Array.Empty<string>();
            InvalidateFingerSelection(now);
        }
        _slotsObservedRevision = _slots.Revision;
        return true;
    }

    private IReadOnlyList<BoardButton> SlotsButtons()
    {
        var actions = _slots.AvailableActions();
        bool refill = actions.Contains("slot-refill");
        return Array.AsReadOnly(new[]
        {
            new BoardButton("slot-exit", "Exit", SlotExitBounds, BoardScreen.Menu, Hold: BoardButtonHold.Once),
            new BoardButton("slot-bet-down", "Bet -", SlotBetDownBounds, BoardScreen.Slots,
                actions.Contains("slot-bet-down"), BoardButtonHold.Once),
            new BoardButton("slot-bet-up", "Bet +", SlotBetUpBounds, BoardScreen.Slots,
                actions.Contains("slot-bet-up"), BoardButtonHold.Once),
            new BoardButton("slot-buy", "Buy", SlotBuyBounds, BoardScreen.Slots,
                actions.Contains("slot-buy"), BoardButtonHold.Once),
            refill
                ? new BoardButton("slot-refill", "Refill", SlotSpinBounds, BoardScreen.Slots, true, BoardButtonHold.Once)
                : new BoardButton("slot-spin", "Spin", SlotSpinBounds, BoardScreen.Slots,
                    actions.Contains("slot-spin"), BoardButtonHold.Once)
        });
    }

    private bool SelectSlotsButton(BoardButton button, DateTimeOffset now)
    {
        if (button.Id == "slot-exit")
        {
            Show(BoardScreen.Menu, now);
            return true;
        }
        if (!_slots.HandleAction(button.Id, now)) return false;
        if (button.Id is "slot-bet-down" or "slot-bet-up" or "slot-buy" or "slot-spin")
            SlotsLastButtonPress = new(button.Id, button.Bounds, now, ++_slotsButtonPressSequence);
        _slotsObservedRevision = _slots.Revision;
        _ignoreExecutionsThrough = Later(_ignoreExecutionsThrough, now);
        _ignoreSelectionsThrough = Later(_ignoreSelectionsThrough, now);
        HoveredButtonIds = Array.Empty<string>();
        InvalidateFingerSelection(now);
        return true;
    }
}
