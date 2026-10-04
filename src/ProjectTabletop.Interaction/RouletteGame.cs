using System.Globalization;

namespace ProjectTabletop.Interaction;

/// <summary>European roulette using virtual credits only. Rendering never chooses or settles an outcome.</summary>
public sealed class RouletteGame
{
    public const decimal StartingBalance = 1000m;
    public const decimal MaximumRoundBet = 10000m;
    public static readonly TimeSpan SpinDuration = TimeSpan.FromSeconds(7);
    public static IReadOnlyList<decimal> ChipOptions { get; } = Array.AsReadOnly<decimal>([1, 5, 25, 100]);
    /// <summary>Clockwise European wheel pockets, beginning at its single zero.</summary>
    public static IReadOnlyList<int> WheelOrder { get; } = Array.AsReadOnly(
        new[] { 0, 32, 15, 19, 4, 21, 2, 25, 17, 34, 6, 27, 13, 36, 11, 30, 8, 23, 10,
            5, 24, 16, 33, 1, 20, 14, 31, 9, 22, 18, 29, 7, 28, 12, 35, 3, 26 });
    public static IReadOnlyList<RouletteBetTarget> BetTargets { get; } = Array.AsReadOnly(
        Enumerable.Range(0, 37).Select(number => new RouletteBetTarget(RouletteBetKind.Straight, number))
            .Concat(new[] { RouletteBetKind.Red, RouletteBetKind.Black, RouletteBetKind.Even,
                RouletteBetKind.Odd, RouletteBetKind.Low, RouletteBetKind.High }.Select(kind => new RouletteBetTarget(kind)))
            .Concat(Enumerable.Range(1, 3).Select(number => new RouletteBetTarget(RouletteBetKind.Dozen, number)))
            .Concat(Enumerable.Range(1, 3).Select(number => new RouletteBetTarget(RouletteBetKind.Column, number))).ToArray());
    private static readonly IReadOnlyDictionary<string, RouletteBetTarget> TargetsById =
        BetTargets.ToDictionary(target => target.Id, StringComparer.Ordinal);
    private readonly Random _random;
    private readonly Dictionary<RouletteBetTarget, decimal> _bets = [];
    private readonly List<RouletteBet[]> _undo = [];
    private RouletteBet[] _lastBets = [];
    private readonly List<RouletteResult> _history = [];
    private decimal _balance, _chip = 25m, _lastWin, _lastProfit;
    private RoulettePhase _phase;
    private DateTimeOffset _roundStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastObservedAt = DateTimeOffset.MinValue;
    private long _roundNumber;
    private int? _outcome, _pocketIndex;
    private RouletteBetTarget? _selectedBet;
    private string _status = "Place your chips. Virtual credits only.";
    private RouletteSnapshot? _snapshot;

    public RouletteGame(int? seed = null, decimal startingBalance = StartingBalance)
    {
        if (startingBalance is < 0 or > 1_000_000_000m) throw new ArgumentOutOfRangeException(nameof(startingBalance));
        _random = seed is { } value ? new Random(value) : new Random();
        _balance = startingBalance;
    }

    public long Revision { get; private set; }
    public RoulettePhase Phase => _phase;
    public decimal Balance => _balance;
    public decimal TotalBet => _bets.Values.Sum();
    public DateTimeOffset RoundStartedAt => _roundStartedAt;
    public DateTimeOffset PhaseEndsAt => _phase == RoulettePhase.Spinning ? _roundStartedAt + SpinDuration : _roundStartedAt;
    public RouletteSnapshot Snapshot => _snapshot ??= new(Revision, _phase, _roundStartedAt, SpinDuration,
        _roundNumber, _outcome, _pocketIndex, _balance, _chip, ChipOptions, TotalBet,
        Array.AsReadOnly(_bets.Select(pair => new RouletteBet(pair.Key, pair.Value)).ToArray()),
        Array.AsReadOnly(_lastBets.ToArray()), _lastBets.Sum(bet => bet.Amount), _lastWin, _lastProfit, _selectedBet,
        Array.AsReadOnly(_history.ToArray()), _status, AvailableActions());

    public IReadOnlyList<string> AvailableActions()
    {
        if (_phase == RoulettePhase.Spinning) return Array.Empty<string>();
        var actions = ChipOptions.Where(chip => chip != _chip).Select(ChipAction).ToList();
        if (_balance >= _chip && TotalBet + _chip <= MaximumRoundBet) actions.AddRange(TargetsById.Keys);
        if (_bets.Count > 0) actions.AddRange(["roulette-undo", "roulette-clear", "roulette-spin"]);
        else
        {
            if (_lastBets.Length > 0 && _balance >= _lastBets.Sum(bet => bet.Amount)) actions.Add("roulette-rebet");
            if (_balance < ChipOptions[0]) actions.Add("roulette-refill");
        }
        return actions.AsReadOnly();
    }

    public bool HandleAction(string id, DateTimeOffset now)
    {
        if (!ObserveTime(now) || !AvailableActions().Contains(id, StringComparer.Ordinal)) return false;
        if (TargetsById.TryGetValue(id, out var target))
        {
            AddBets([new(target, _chip)]);
            _selectedBet = target;
            _status = $"{target.Label} · {Format(TotalBet)} credits on the table";
        }
        else if (id.StartsWith("roulette-chip-", StringComparison.Ordinal))
        {
            _chip = ChipOptions.Single(chip => ChipAction(chip) == id);
            _status = $"{Format(_chip)} credit chip selected";
        }
        else switch (id)
        {
            case "roulette-undo":
                foreach (var bet in _undo[^1])
                {
                    _balance += bet.Amount;
                    decimal remaining = _bets[bet.Target] - bet.Amount;
                    if (remaining == 0) _bets.Remove(bet.Target); else _bets[bet.Target] = remaining;
                }
                _undo.RemoveAt(_undo.Count - 1);
                _selectedBet = _undo.Count > 0 ? _undo[^1][^1].Target : null;
                _status = "Last wager returned";
                break;
            case "roulette-clear":
                _balance += TotalBet;
                _bets.Clear(); _undo.Clear(); _selectedBet = null;
                _status = "All unplayed chips returned";
                break;
            case "roulette-rebet":
                AddBets(_lastBets.ToArray());
                _selectedBet = _lastBets[^1].Target;
                _status = "Previous bets restored";
                break;
            case "roulette-spin":
                _lastBets = _bets.Select(pair => new RouletteBet(pair.Key, pair.Value)).ToArray();
                _undo.Clear();
                _roundStartedAt = now; _roundNumber++;
                _pocketIndex = _random.Next(WheelOrder.Count);
                _outcome = WheelOrder[_pocketIndex.Value];
                _lastWin = _lastProfit = 0;
                _phase = RoulettePhase.Spinning;
                _status = "No more bets";
                break;
            case "roulette-refill":
                _balance = StartingBalance;
                _status = "1,000 virtual credits restored";
                break;
        }
        Changed();
        return true;
    }

    public bool Tick(DateTimeOffset now)
    {
        if (!ObserveTime(now) || _phase != RoulettePhase.Spinning || now < PhaseEndsAt) return false;
        decimal stake = TotalBet;
        _lastWin = _bets.Sum(pair => Payout(pair.Key, pair.Value, _outcome!.Value));
        _lastProfit = _lastWin - stake;
        _balance += _lastWin;
        _history.Insert(0, new(_roundNumber, _outcome!.Value, _pocketIndex!.Value, stake, _lastWin, _lastProfit));
        if (_history.Count > 12) _history.RemoveAt(_history.Count - 1);
        _bets.Clear(); _undo.Clear(); _selectedBet = null;
        _phase = RoulettePhase.Betting;
        string colour = _outcome == 0 ? "GREEN" : IsRed(_outcome.Value) ? "RED" : "BLACK";
        _status = $"{_outcome} {colour} · " + (_lastWin > 0 ? $"{Format(_lastWin)} credits returned" : "No winning bets");
        Changed();
        return true;
    }

    public static bool IsRed(int number) => number is 1 or 3 or 5 or 7 or 9 or 12 or 14 or 16 or 18 or
        19 or 21 or 23 or 25 or 27 or 30 or 32 or 34 or 36;

    /// <summary>Gross return including the winning stake: 36× straight, 2× even-money, 3× dozen/column.</summary>
    public static decimal Payout(RouletteBetTarget target, decimal stake, int outcome)
    {
        if (!BetTargets.Contains(target)) throw new ArgumentOutOfRangeException(nameof(target));
        if (stake < 0) throw new ArgumentOutOfRangeException(nameof(stake));
        if (outcome is < 0 or > 36) throw new ArgumentOutOfRangeException(nameof(outcome));
        bool wins = target.Kind switch
        {
            RouletteBetKind.Straight => target.Number == outcome,
            RouletteBetKind.Red => outcome != 0 && IsRed(outcome),
            RouletteBetKind.Black => outcome != 0 && !IsRed(outcome),
            RouletteBetKind.Even => outcome != 0 && outcome % 2 == 0,
            RouletteBetKind.Odd => outcome % 2 == 1,
            RouletteBetKind.Low => outcome is >= 1 and <= 18,
            RouletteBetKind.High => outcome is >= 19 and <= 36,
            RouletteBetKind.Dozen => outcome != 0 && (outcome - 1) / 12 + 1 == target.Number,
            RouletteBetKind.Column => outcome != 0 && (outcome - 1) % 3 + 1 == target.Number,
            _ => false
        };
        return wins ? stake * (target.Kind == RouletteBetKind.Straight ? 36 :
            target.Kind is RouletteBetKind.Dozen or RouletteBetKind.Column ? 3 : 2) : 0;
    }

    public static string ChipAction(decimal chip) => "roulette-chip-" + chip.ToString("0", CultureInfo.InvariantCulture);
    public static string Format(decimal credits) => credits.ToString("#,0.##", CultureInfo.InvariantCulture);

    private void AddBets(RouletteBet[] bets)
    {
        foreach (var bet in bets)
        {
            _balance -= bet.Amount;
            _bets[bet.Target] = _bets.GetValueOrDefault(bet.Target) + bet.Amount;
        }
        _undo.Add(bets);
    }

    private bool ObserveTime(DateTimeOffset now)
    {
        if (now < _lastObservedAt) return false;
        _lastObservedAt = now;
        return true;
    }
    private void Changed() { Revision++; _snapshot = null; }
}
