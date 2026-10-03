namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    private readonly RouletteGame _roulette;
    public RouletteSnapshot RouletteState => _roulette.Snapshot;
    public static readonly BoardRect RouletteTableBounds = new(.07, .48, .86, .27);
    public static readonly BoardRect RouletteExitBounds = new(.035, .855, .13, .115);
    public static readonly BoardRect RouletteUndoBounds = new(.185, .855, .14, .115);
    public static readonly BoardRect RouletteClearBounds = new(.345, .855, .14, .115);
    public static readonly BoardRect RouletteRebetBounds = new(.505, .855, .18, .115);
    public static readonly BoardRect RouletteSpinBounds = new(.705, .855, .26, .115);

    public void ShowRoulette(DateTimeOffset? now = null) => Show(BoardScreen.Roulette, now ?? DateTimeOffset.UtcNow);
    public bool TickRoulette(DateTimeOffset now) => AdvanceRoulette(now);

    /// <summary>One shared table layout for rendering and hit testing: zero, 3×12 numbers, columns, dozens, outside bets.</summary>
    public static BoardRect RouletteBetBounds(RouletteBetTarget target)
    {
        if (!RouletteGame.BetTargets.Contains(target)) throw new ArgumentOutOfRangeException(nameof(target));
        const double left = .128, top = .48, width = .742, row = .054, gap = .002;
        if (target.Kind == RouletteBetKind.Straight)
        {
            if (target.Number == 0) return new(.07, top, .055, row * 3 - gap);
            int column = (target.Number - 1) / 3, line = 2 - (target.Number - 1) % 3;
            return new(left + column * width / 12, top + line * row, width / 12 - gap, row - gap);
        }
        if (target.Kind == RouletteBetKind.Column)
            return new(.874, top + (3 - target.Number) * row, .056, row - gap);
        if (target.Kind == RouletteBetKind.Dozen)
            return new(left + (target.Number - 1) * width / 3, .648, width / 3 - gap, .047);
        int index = target.Kind switch
        {
            RouletteBetKind.Low => 0, RouletteBetKind.Even => 1, RouletteBetKind.Red => 2,
            RouletteBetKind.Black => 3, RouletteBetKind.Odd => 4, RouletteBetKind.High => 5,
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
        return new(left + index * width / 6, .701, width / 6 - gap, .049);
    }

    public static BoardRect RouletteChipBounds(int index)
    {
        if (index < 0 || index >= RouletteGame.ChipOptions.Count) throw new ArgumentOutOfRangeException(nameof(index));
        return new(.18 + index * .165, .785, .14, .05);
    }

    private IReadOnlyList<BoardButton> RouletteButtons()
    {
        var actions = _roulette.AvailableActions();
        var result = RouletteGame.BetTargets.Select(target => new BoardButton(target.Id, target.Label,
            RouletteBetBounds(target), BoardScreen.Roulette, actions.Contains(target.Id))).ToList();
        for (int index = 0; index < RouletteGame.ChipOptions.Count; index++)
        {
            decimal chip = RouletteGame.ChipOptions[index];
            string id = RouletteGame.ChipAction(chip);
            result.Add(new(id, RouletteGame.Format(chip), RouletteChipBounds(index), BoardScreen.Roulette, actions.Contains(id)));
        }
        result.Add(new("roulette-exit", "Exit", RouletteExitBounds, BoardScreen.Menu, Hold: BoardButtonHold.Once));
        Add("roulette-undo", "Undo", RouletteUndoBounds);
        Add("roulette-clear", "Clear", RouletteClearBounds);
        Add("roulette-rebet", "Rebet", RouletteRebetBounds);
        bool refill = actions.Contains("roulette-refill");
        Add(refill ? "roulette-refill" : "roulette-spin", refill ? "Refill" : "Spin", RouletteSpinBounds);
        return result.AsReadOnly();

        void Add(string id, string label, BoardRect bounds) =>
            result.Add(new(id, label, bounds, BoardScreen.Roulette, actions.Contains(id), BoardButtonHold.Once));
    }

    private bool AdvanceRoulette(DateTimeOffset now)
    {
        // The game survives navigation. A returning board catches up to its
        // original deadline without rerolling or charging the committed slip.
        if (Screen != BoardScreen.Roulette || !_roulette.Tick(now)) return false;
        _ignoreExecutionsThrough = Later(_ignoreExecutionsThrough, now);
        _ignoreSelectionsThrough = Later(_ignoreSelectionsThrough, now);
        HoveredButtonIds = Array.Empty<string>();
        InvalidateFingerSelection(now);
        ResetHoldCaptionEvidence();
        return true;
    }

    private bool SelectRouletteButton(BoardButton button, DateTimeOffset now)
    {
        if (button.Id == "roulette-exit")
        {
            Show(BoardScreen.Menu, now);
            return true;
        }
        if (!_roulette.HandleAction(button.Id, now)) return false;
        _ignoreExecutionsThrough = Later(_ignoreExecutionsThrough, now);
        _ignoreSelectionsThrough = Later(_ignoreSelectionsThrough, now);
        HoveredButtonIds = Array.Empty<string>();
        InvalidateFingerSelection(now);
        return true;
    }
}
