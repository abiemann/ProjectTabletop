namespace ProjectTabletop.Interaction;

/// <summary>
/// Dragon's Hoard: a five-reel, 40-line slot machine played with virtual credits.
/// Coins with a power egg start Dragonfire Respins, where held symbols reset three
/// respins and the eggs expand the grid (green), boost coins (blue) or collect every
/// value (red). Three elixirs start free spins; keys under all five reels open the
/// Treasure Vault, whose chests are opened until three gems match a jackpot.
/// Every spin resolves in timed phases so the board can present it; call
/// <see cref="Tick"/> with a monotonic clock. Call from one thread.
/// </summary>
public sealed class SlotGame
{
    public const int Reels = 5, Rows = 5, BaseFirstRow = 1, BaseRowCount = 3, PaylineCount = 40;
    public const int FreeSpinsAwarded = 5, VaultChestCount = 12, MaximumElixirLevel = 5, RespinCount = 3;
    public const decimal StartingBalance = 2000m;
    /// <summary>Price of a Feature Buy, in bets.</summary>
    public const int BuyMultiplier = 56;
    // Every pay is a multiple of 0.05 bets, so bets in 20s pay whole credits.
    public static readonly IReadOnlyList<decimal> BetOptions = Array.AsReadOnly(new[] { 20m, 40m, 100m, 200m });
    public static readonly IReadOnlyDictionary<SlotJackpot, int> JackpotMultipliers = new Dictionary<SlotJackpot, int>
    {
        [SlotJackpot.Mini] = 10, [SlotJackpot.Minor] = 25, [SlotJackpot.Major] = 100, [SlotJackpot.Grand] = 1000
    };

    public static readonly TimeSpan SpinDuration = TimeSpan.FromMilliseconds(2000);
    public static readonly TimeSpan FreeSpinDuration = TimeSpan.FromMilliseconds(1700);
    public static readonly TimeSpan LineWinDuration = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan RespinIntroDuration = TimeSpan.FromMilliseconds(2000);
    public static readonly TimeSpan RespinDuration = TimeSpan.FromMilliseconds(1100);
    public static readonly TimeSpan RespinEffectDuration = TimeSpan.FromMilliseconds(1400);
    public static readonly TimeSpan RespinOutroDuration = TimeSpan.FromMilliseconds(2600);
    public static readonly TimeSpan FreeSpinsIntroDuration = TimeSpan.FromMilliseconds(2400);
    public static readonly TimeSpan FreeSpinsOutroDuration = TimeSpan.FromMilliseconds(2600);
    public static readonly TimeSpan VaultIntroDuration = TimeSpan.FromMilliseconds(2000);
    public static readonly TimeSpan VaultPickDuration = TimeSpan.FromMilliseconds(900);
    public static readonly TimeSpan VaultOutroDuration = TimeSpan.FromMilliseconds(3000);

    /// <summary>When each reel lands, measured from the start of a spin.</summary>
    public static TimeSpan ReelStop(int reel, bool freeSpin) =>
        TimeSpan.FromMilliseconds((freeSpin ? 750 : 900) + Math.Clamp(reel, 0, Reels - 1) * (freeSpin ? 190 : 220));

    /// <summary>When the new symbols of a respin land, measured from the respin's start.</summary>
    public static readonly TimeSpan RespinLanding = TimeSpan.FromMilliseconds(750);

    /// <summary>Forty paylines over the three base rows (0 top, 2 bottom).</summary>
    public static readonly IReadOnlyList<IReadOnlyList<int>> Paylines = BuildPaylines();

    private static readonly SlotSymbol[] PaySymbols =
    [
        SlotSymbol.Ten, SlotSymbol.Jack, SlotSymbol.Queen, SlotSymbol.King, SlotSymbol.Ace,
        SlotSymbol.Dagger, SlotSymbol.Goblet, SlotSymbol.Chest, SlotSymbol.Crown
    ];

    /// <summary>Pays for three, four and five of a kind, in bets.</summary>
    public static readonly IReadOnlyDictionary<SlotSymbol, IReadOnlyList<decimal>> Paytable =
        new Dictionary<SlotSymbol, IReadOnlyList<decimal>>
        {
            [SlotSymbol.Ten] = [.15m, .3m, 1.15m],
            [SlotSymbol.Jack] = [.15m, .45m, 1.4m],
            [SlotSymbol.Queen] = [.3m, .6m, 1.75m],
            [SlotSymbol.King] = [.3m, .7m, 2.3m],
            [SlotSymbol.Ace] = [.35m, .9m, 2.9m],
            [SlotSymbol.Dagger] = [.6m, 1.75m, 4.65m],
            [SlotSymbol.Goblet] = [.7m, 2.3m, 7m],
            [SlotSymbol.Chest] = [.95m, 3.5m, 9.3m],
            [SlotSymbol.Crown] = [1.15m, 4.65m, 14m]
        };

    private readonly Random _random;
    private readonly decimal _startingBalance;
    private SlotCell[] _grid = BlankGrid();
    private SlotCell[] _previous = BlankGrid();
    private SlotCell[] _feature = BlankGrid();
    private SlotPhase _phase = SlotPhase.Idle;
    private DateTimeOffset _phaseStarted;
    private DateTimeOffset _phaseEnds;
    private TimeSpan _phaseDuration;
    private DateTimeOffset? _lastNow;
    private decimal _balance;
    private decimal _bet = BetOptions[0];
    private long _spinNumber;
    private DateTimeOffset _spinStartedAt;
    private bool _freeSpin;
    private List<SlotLineWin> _lineWins = [];
    private decimal _roundWin;
    private bool _pendingRespins;
    private bool _pendingFreeSpins;
    // Respins
    private bool _inRespins;
    private int _firstRow = BaseFirstRow, _rowCount = BaseRowCount;
    private int _respinsLeft;
    private readonly List<SlotPosition> _fresh = [];
    private readonly List<(SlotEffect Effect, SlotPosition Cell)> _effects = [];
    // First successful hatches belong to this game, not to an individual spin.
    // Keeping them is presentation history; powers still require qualifying eggs.
    private readonly List<SlotDragonHatch> _dragonHatches = [];
    private SlotEffect _effect;
    private SlotPosition? _effectCell;
    private int _boost;
    private decimal _effectAmount;
    private decimal _respinTotal;
    private bool _grandFill;
    // Free spins
    private bool _inFreeSpins;
    private int _freeRemaining, _freePlayed, _elixirLevel;
    private decimal _freeWin;
    // Treasure Vault
    private readonly bool[] _keys = new bool[Reels];
    private readonly SlotVaultChest?[] _chests = new SlotVaultChest?[VaultChestCount];
    private List<SlotVaultChest> _vaultPlan = [];
    private int _vaultStep;
    private int _vaultNewest = -1;
    private SlotJackpot _vaultAward;
    private decimal _vaultBet;
    private SlotDemo _demo;
    private string _status = "Hold SPIN for a second to play.";
    private string _banner = string.Empty;
    private SlotSnapshot? _snapshot;

    public SlotGame(int? seed = null, decimal startingBalance = StartingBalance)
    {
        _random = seed is { } value ? new Random(value) : new Random();
        _startingBalance = startingBalance;
        _balance = startingBalance;
        // An attractive, win-free opening screen.
        for (int reel = 0; reel < Reels; reel++)
            for (int row = BaseFirstRow; row < BaseFirstRow + BaseRowCount; row++)
                _grid[Index(reel, row)] = new(PaySymbols[(reel * 2 + row * 3) % PaySymbols.Length]);
    }

    public long Revision { get; private set; }
    public SlotPhase Phase => _phase;
    public DateTimeOffset PhaseEndsAt => _phaseEnds;
    public decimal Balance => _balance;
    public decimal Bet => _bet;
    public decimal BuyCost => _bet * BuyMultiplier;
    public SlotSnapshot Snapshot => _snapshot ??= CreateSnapshot();

    /// <summary>Makes the next spin land the requested feature.</summary>
    public void Demonstrate(SlotDemo demo) => _demo = demo;

    public IReadOnlyList<string> AvailableActions()
    {
        if (_phase != SlotPhase.Idle) return [];
        var actions = new List<string>();
        if (_balance < BetOptions[0]) actions.Add("slot-refill");
        else if (_balance >= _bet) actions.Add("slot-spin");
        int betIndex = BetIndex();
        if (betIndex > 0) actions.Add("slot-bet-down");
        if (betIndex < BetOptions.Count - 1 && _balance >= BetOptions[betIndex + 1]) actions.Add("slot-bet-up");
        if (_balance >= BuyCost) actions.Add("slot-buy");
        return actions;
    }

    public bool HandleAction(string id, DateTimeOffset now)
    {
        if (!ObserveTime(now) || !AvailableActions().Contains(id, StringComparer.Ordinal)) return false;
        switch (id)
        {
            case "slot-spin": StartSpin(now, free: false); break;
            case "slot-buy": Buy(now); break;
            case "slot-bet-down":
                _bet = BetOptions[BetIndex() - 1];
                _status = $"Bet {Format(_bet)} credits.";
                break;
            case "slot-bet-up":
                _bet = BetOptions[BetIndex() + 1];
                _status = $"Bet {Format(_bet)} credits.";
                break;
            case "slot-refill":
                _balance = _startingBalance;
                _bet = BetOptions[0];
                _status = $"{Format(_startingBalance)} credits restored. Hold SPIN to play.";
                break;
        }
        Changed();
        return true;
    }

    /// <summary>Advances at most one presentation phase once its time has elapsed.</summary>
    public bool Tick(DateTimeOffset now)
    {
        if (!ObserveTime(now) || _phase == SlotPhase.Idle || now < _phaseEnds) return false;
        switch (_phase)
        {
            case SlotPhase.Spinning:
                if (_lineWins.Count > 0)
                {
                    decimal won = _lineWins.Sum(win => win.Amount);
                    Credit(won);
                    Enter(SlotPhase.LineWins, LineWinDuration, now, _lineWins.Count == 1
                        ? $"Line win · {Format(won)} credits" : $"{_lineWins.Count} line wins · {Format(won)} credits");
                }
                else Continue(now);
                break;
            case SlotPhase.LineWins: Continue(now); break;
            case SlotPhase.RespinIntro:
            case SlotPhase.RespinEffect:
            case SlotPhase.Respinning: NextRespinStep(now); break;
            case SlotPhase.RespinOutro:
                _inRespins = false;
                _effect = SlotEffect.None;
                _effectCell = null;
                _fresh.Clear();
                _firstRow = BaseFirstRow;
                _rowCount = BaseRowCount;
                Continue(now);
                break;
            case SlotPhase.FreeSpinsIntro: Continue(now); break;
            case SlotPhase.FreeSpinsOutro:
                _inFreeSpins = false;
                Continue(now);
                break;
            case SlotPhase.VaultIntro:
            case SlotPhase.VaultPicking:
                if (_vaultStep < _vaultPlan.Count)
                {
                    var chest = _vaultPlan[_vaultStep++];
                    _chests[chest.Index] = chest;
                    _vaultNewest = chest.Index;
                    int found = _chests.Count(item => item?.Gem == chest.Gem);
                    Enter(SlotPhase.VaultPicking, VaultPickDuration, now,
                        $"{JackpotName(chest.Gem)} gem · {found} of 3");
                }
                else
                {
                    _vaultAward = _vaultPlan[^1].Gem;
                    decimal award = JackpotMultipliers[_vaultAward] * _vaultBet;
                    Credit(award);
                    Enter(SlotPhase.VaultOutro, VaultOutroDuration, now,
                        $"{JackpotName(_vaultAward)} jackpot · {Format(award)} credits",
                        $"{JackpotName(_vaultAward).ToUpperInvariant()} JACKPOT");
                }
                break;
            case SlotPhase.VaultOutro:
                Array.Clear(_chests);
                _vaultPlan = [];
                _vaultNewest = -1;
                _vaultAward = SlotJackpot.None;
                Continue(now);
                break;
        }
        Changed();
        return true;
    }

    private void StartSpin(DateTimeOffset now, bool free)
    {
        _spinStartedAt = now;
        if (free)
        {
            _freeRemaining--;
            _freePlayed++;
        }
        else
        {
            _balance -= _bet;
            _roundWin = 0;
        }
        _previous = _grid;
        _grid = Generate(free);
        _spinNumber++;
        _freeSpin = free;
        _lineWins = Evaluate(_grid, _bet);
        int eggs = CountBase(_grid, cell => cell.IsEgg);
        int coins = CountBase(_grid, cell => cell.Symbol == SlotSymbol.Coin);
        _pendingRespins = eggs >= 1 && coins >= 3;
        int elixirs = CountBase(_grid, cell => cell.Symbol == SlotSymbol.Elixir);
        if (free)
        {
            _freeRemaining += elixirs;
            _elixirLevel = Math.Min(MaximumElixirLevel, _elixirLevel + elixirs);
        }
        else if (elixirs >= 3) _pendingFreeSpins = true;
        if (!free)
            for (int reel = 0; reel < Reels; reel++)
                for (int row = BaseFirstRow; row < BaseFirstRow + BaseRowCount; row++)
                    if (_grid[Index(reel, row)].Symbol == SlotSymbol.Key) _keys[reel] = true;
        Enter(SlotPhase.Spinning, free ? FreeSpinDuration : SpinDuration, now,
            free ? $"Free spin {_freePlayed}" : "Spinning…");
    }

    private void Buy(DateTimeOffset now)
    {
        _spinStartedAt = now;
        _balance -= BuyCost;
        _roundWin = 0;
        _previous = _grid;
        var grid = BlankGrid();
        for (int reel = 0; reel < Reels; reel++)
            for (int row = BaseFirstRow; row < BaseFirstRow + BaseRowCount; row++)
                grid[Index(reel, row)] = new(PaySymbols[_random.Next(PaySymbols.Length)]);
        var places = Enumerable.Range(0, Reels * BaseRowCount).OrderBy(_ => _random.Next()).Take(6).ToArray();
        grid[BasePlace(places[0])] = EggCell(Pick([(SlotSymbol.EggGreen, 30), (SlotSymbol.EggRed, 30),
            (SlotSymbol.EggBlue, 30), (SlotSymbol.EggRainbow, 10)]));
        foreach (int place in places.Skip(1)) grid[BasePlace(place)] = CoinCell();
        _grid = grid;
        _spinNumber++;
        _freeSpin = false;
        _lineWins = [];
        _pendingRespins = true;
        Enter(SlotPhase.Spinning, SpinDuration, now, "Feature bought · Dragonfire Respins");
    }

    private void Continue(DateTimeOffset now)
    {
        if (_pendingRespins)
        {
            _pendingRespins = false;
            StartRespins(now);
            return;
        }
        if (_pendingFreeSpins && !_inFreeSpins)
        {
            _pendingFreeSpins = false;
            _inFreeSpins = true;
            _freeRemaining = FreeSpinsAwarded;
            _freePlayed = 0;
            _elixirLevel = 0;
            _freeWin = 0;
            Enter(SlotPhase.FreeSpinsIntro, FreeSpinsIntroDuration, now,
                $"{FreeSpinsAwarded} free spins · each elixir adds one", $"{FreeSpinsAwarded} FREE SPINS");
            return;
        }
        if (_inFreeSpins)
        {
            if (_freeRemaining > 0) StartSpin(now, free: true);
            else Enter(SlotPhase.FreeSpinsOutro, FreeSpinsOutroDuration, now,
                $"Free spins won {Format(_freeWin)} credits", "FREE SPINS COMPLETE");
            return;
        }
        if (_keys.All(lit => lit))
        {
            StartVault(now);
            return;
        }
        if (_balance < _bet)
            _bet = BetOptions.LastOrDefault(bet => bet <= _balance, BetOptions[0]);
        Enter(SlotPhase.Idle, TimeSpan.Zero, now, _balance < BetOptions[0]
            ? "Out of credits. Hold REFILL for a fresh bankroll."
            : _roundWin > 0 ? $"You won {Format(_roundWin)} credits! Hold SPIN to play again."
            : "Hold SPIN for a second to play.");
    }

    private void StartRespins(DateTimeOffset now)
    {
        _inRespins = true;
        _feature = BlankGrid();
        var eggs = new List<(SlotCell Cell, SlotPosition Position)>();
        for (int reel = 0; reel < Reels; reel++)
            for (int row = BaseFirstRow; row < BaseFirstRow + BaseRowCount; row++)
            {
                var cell = _grid[Index(reel, row)];
                if (!cell.IsBonus) continue;
                _feature[Index(reel, row)] = cell;
                if (cell.IsEgg) eggs.Add((cell, new(reel, row)));
            }
        _firstRow = BaseFirstRow;
        _rowCount = BaseRowCount;
        _respinsLeft = RespinCount;
        _fresh.Clear();
        _effects.Clear();
        QueueEffects(eggs);
        _effect = SlotEffect.None;
        _effectCell = null;
        _grandFill = false;
        _respinTotal = FeatureTotal();
        Enter(SlotPhase.RespinIntro, RespinIntroDuration, now,
            "Dragonfire Respins · new symbols reset three respins", "DRAGONFIRE RESPINS");
    }

    private void QueueEffects(IEnumerable<(SlotCell Cell, SlotPosition Position)> eggs)
    {
        foreach (var (cell, position) in eggs)
        {
            if (cell.Symbol is SlotSymbol.EggGreen or SlotSymbol.EggRainbow) _effects.Add((SlotEffect.Expand, position));
            if (cell.Symbol is SlotSymbol.EggBlue or SlotSymbol.EggRainbow) _effects.Add((SlotEffect.Boost, position));
            if (cell.Symbol is SlotSymbol.EggRed or SlotSymbol.EggRainbow) _effects.Add((SlotEffect.Collect, position));
        }
        // Expand first so boosts and collections see the whole grid; collect last.
        var ordered = _effects.OrderBy(item => item.Effect).ToList();
        _effects.Clear();
        _effects.AddRange(ordered);
    }

    private void NextRespinStep(DateTimeOffset now)
    {
        while (_effects.Count > 0)
        {
            var (effect, cell) = _effects[0];
            _effects.RemoveAt(0);
            if (ApplyEffect(effect, cell, now)) return;
        }
        _effect = SlotEffect.None;
        _effectCell = null;
        if (_respinsLeft == 0 || FeatureFull())
        {
            _grandFill = FeatureFull();
            decimal total = FeatureTotal() + (_grandFill ? JackpotMultipliers[SlotJackpot.Grand] * _bet : 0);
            _respinTotal = total;
            Credit(total);
            Enter(SlotPhase.RespinOutro, RespinOutroDuration, now,
                _grandFill ? $"Grid filled · GRAND jackpot · {Format(total)} credits" : $"Respins won {Format(total)} credits",
                _grandFill ? "GRAND JACKPOT" : "DRAGONFIRE WIN");
            return;
        }
        _respinsLeft--;
        _fresh.Clear();
        var landedEggs = new List<(SlotCell Cell, SlotPosition Position)>();
        double level = _inFreeSpins ? _elixirLevel : 0;
        double coin = .032 + .004 * level, egg = .002 + .0006 * level, rainbow = .0004 + .0001 * level;
        for (int reel = 0; reel < Reels; reel++)
            for (int row = _firstRow; row < _firstRow + _rowCount; row++)
            {
                if (_feature[Index(reel, row)].Symbol != SlotSymbol.Empty) continue;
                double roll = _random.NextDouble();
                SlotCell? landed = null;
                if (roll < coin) landed = CoinCell();
                else if ((roll -= coin) < egg) landed = _rowCount == BaseRowCount ? EggCell(SlotSymbol.EggGreen) : CoinCell();
                else if ((roll -= egg) < egg) landed = EggCell(SlotSymbol.EggRed);
                else if ((roll -= egg) < egg) landed = EggCell(SlotSymbol.EggBlue);
                else if (roll - egg < rainbow) landed = EggCell(SlotSymbol.EggRainbow);
                if (landed is null) continue;
                _feature[Index(reel, row)] = landed;
                _fresh.Add(new(reel, row));
                if (landed.IsEgg) landedEggs.Add((landed, new(reel, row)));
            }
        if (_fresh.Count > 0) _respinsLeft = RespinCount;
        QueueEffects(landedEggs);
        _respinTotal = FeatureTotal();
        Enter(SlotPhase.Respinning, RespinDuration, now, _fresh.Count > 0
            ? $"{_fresh.Count} new · respins reset to {RespinCount}"
            : _respinsLeft == 0 ? "Last respin" : $"{_respinsLeft} respins left");
    }

    private bool ApplyEffect(SlotEffect effect, SlotPosition cell, DateTimeOffset now)
    {
        _effectAmount = 0;
        _boost = 0;
        switch (effect)
        {
            case SlotEffect.Expand:
                if (_rowCount != BaseRowCount) return false;
                _firstRow = 0;
                _rowCount = Rows;
                Enter(SlotPhase.RespinEffect, RespinEffectDuration, now, "Expand · the grid grows to five rows", "EXPAND");
                break;
            case SlotEffect.Boost:
                _boost = Pick([(2, 75), (3, 20), (5, 5)]);
                for (int index = 0; index < _feature.Length; index++)
                    if (_feature[index] is { Symbol: SlotSymbol.Coin, Jackpot: SlotJackpot.None } coin)
                        _feature[index] = coin with { Value = coin.Value * _boost };
                Enter(SlotPhase.RespinEffect, RespinEffectDuration, now, $"Boost · every coin ×{_boost}", $"BOOST ×{_boost}");
                break;
            case SlotEffect.Collect:
                int collector = Index(cell.Reel, cell.Row);
                decimal sum = 0;
                for (int index = 0; index < _feature.Length; index++)
                    if (index != collector) sum += _feature[index].Value;
                _effectAmount = sum;
                _feature[collector] = _feature[collector] with { Value = _feature[collector].Value + sum };
                Enter(SlotPhase.RespinEffect, RespinEffectDuration, now,
                    $"Collect · {Format(sum)} credits gathered", "COLLECT");
                break;
        }
        _effect = effect;
        _effectCell = cell;
        _respinTotal = FeatureTotal();
        if (!_dragonHatches.Any(hatch => hatch.Power == effect))
            _dragonHatches.Add(new(effect, now));
        return true;
    }

    private void StartVault(DateTimeOffset now)
    {
        Array.Clear(_keys);
        Array.Clear(_chests);
        _vaultBet = _bet;
        var tier = Pick([(SlotJackpot.Minor, 88), (SlotJackpot.Major, 11), (SlotJackpot.Grand, 1)]);
        var gems = new List<SlotJackpot> { tier, tier };
        foreach (var other in new[] { SlotJackpot.Minor, SlotJackpot.Major, SlotJackpot.Grand }.Where(item => item != tier))
            for (int count = _random.Next(3); count > 0; count--) gems.Add(other);
        gems = gems.OrderBy(_ => _random.Next()).ToList();
        gems.Add(tier);
        var chests = Enumerable.Range(0, VaultChestCount).OrderBy(_ => _random.Next()).ToArray();
        _vaultPlan = gems.Select((gem, index) => new SlotVaultChest(chests[index], gem)).ToList();
        _vaultStep = 0;
        _vaultAward = SlotJackpot.None;
        Enter(SlotPhase.VaultIntro, VaultIntroDuration, now,
            "Treasure Vault · chests open until three gems match", "TREASURE VAULT");
    }

    private SlotCell[] Generate(bool free)
    {
        var grid = BlankGrid();
        double level = free ? _elixirLevel : 0;
        for (int reel = 0; reel < Reels; reel++)
        {
            double stacked = free ? .05 + .01 * level : .03;
            if (reel is >= 1 and <= 3 && _random.NextDouble() < stacked)
            {
                for (int row = BaseFirstRow; row < BaseFirstRow + BaseRowCount; row++)
                    grid[Index(reel, row)] = new(SlotSymbol.Wild);
                continue;
            }
            bool egg = false, elixir = false, key = false;
            for (int row = BaseFirstRow; row < BaseFirstRow + BaseRowCount; row++)
            {
                var symbol = DrawSymbol(reel, free, level);
                var cell = new SlotCell(symbol);
                if (cell.IsEgg ? egg : symbol == SlotSymbol.Elixir ? elixir : symbol == SlotSymbol.Key && key)
                    symbol = PaySymbols[_random.Next(PaySymbols.Length)];
                egg |= cell.IsEgg && symbol == cell.Symbol;
                elixir |= symbol == SlotSymbol.Elixir;
                key |= symbol == SlotSymbol.Key;
                grid[Index(reel, row)] = symbol == SlotSymbol.Coin ? CoinCell()
                    : symbol is SlotSymbol.EggGreen or SlotSymbol.EggRed or SlotSymbol.EggBlue or SlotSymbol.EggRainbow
                        ? EggCell(symbol) : new(symbol);
            }
        }
        ApplyDemonstration(grid, free);
        return grid;
    }

    private void ApplyDemonstration(SlotCell[] grid, bool free)
    {
        var demo = _demo;
        _demo = SlotDemo.None;
        switch (demo)
        {
            case SlotDemo.Respins:
                var egg = Pick([(SlotSymbol.EggGreen, 1), (SlotSymbol.EggRed, 1), (SlotSymbol.EggBlue, 1), (SlotSymbol.EggRainbow, 1)]);
                int eggReel = egg switch { SlotSymbol.EggGreen => 1, SlotSymbol.EggRed => 2, SlotSymbol.EggBlue => 3, _ => 2 };
                grid[Index(eggReel, 2)] = EggCell(egg);
                foreach (var (reel, row) in new[] { (0, 1), (0, 3), (1, 1), (3, 3), (4, 2) })
                    grid[Index(reel, row)] = CoinCell();
                break;
            case SlotDemo.FreeSpins:
                foreach (int reel in new[] { 0, 2, 4 })
                {
                    for (int row = BaseFirstRow; row < BaseFirstRow + BaseRowCount; row++)
                        if (grid[Index(reel, row)].Symbol is SlotSymbol.Elixir or SlotSymbol.Wild)
                            grid[Index(reel, row)] = new(PaySymbols[row]);
                    grid[Index(reel, 2)] = new(SlotSymbol.Elixir);
                }
                break;
            case SlotDemo.Vault when !free:
                for (int reel = 0; reel < Reels - 1; reel++) _keys[reel] = true;
                grid[Index(Reels - 1, 2)] = new(SlotSymbol.Key);
                break;
        }
    }

    private SlotSymbol DrawSymbol(int reel, bool free, double level)
    {
        Span<double> weights = stackalloc double[(int)SlotSymbol.Empty];
        ReadOnlySpan<double> pays = [150, 145, 135, 115, 105, 70, 58, 48, 38];
        for (int index = 0; index < pays.Length; index++) weights[index] = pays[index];
        weights[(int)SlotSymbol.Wild] = reel == 0 ? 0 : 42;
        // Free spins raise the coin and egg odds, more with every elixir.
        weights[(int)SlotSymbol.Coin] = 70 * (free ? 1.6 + .2 * level : 1);
        weights[(int)SlotSymbol.Key] = free ? 0 : 1.6;
        weights[(int)SlotSymbol.Elixir] = reel % 2 == 0 ? free ? 22 : 50 : 0;
        double eggBoost = free ? 3 + level : 1;
        weights[(int)SlotSymbol.EggGreen] = reel == 1 ? 10.5 * eggBoost : 0;
        weights[(int)SlotSymbol.EggRed] = reel == 2 ? 10.5 * eggBoost : 0;
        weights[(int)SlotSymbol.EggBlue] = reel == 3 ? 10.5 * eggBoost : 0;
        weights[(int)SlotSymbol.EggRainbow] = reel is >= 1 and <= 3 ? 1.05 * eggBoost : 0;
        double total = 0;
        foreach (double weight in weights) total += weight;
        double roll = _random.NextDouble() * total;
        for (int index = 0; index < weights.Length; index++)
            if ((roll -= weights[index]) < 0) return (SlotSymbol)index;
        return SlotSymbol.Ten;
    }

    private SlotCell CoinCell()
    {
        var jackpot = Pick([(SlotJackpot.None, 99.26), (SlotJackpot.Mini, .6), (SlotJackpot.Minor, .12), (SlotJackpot.Major, .02)]);
        return jackpot != SlotJackpot.None
            ? new(SlotSymbol.Coin, JackpotMultipliers[jackpot] * _bet, jackpot)
            : new(SlotSymbol.Coin, CoinMultiplier() * _bet);
    }

    private SlotCell EggCell(SlotSymbol egg) => new(egg, CoinMultiplier() * _bet);

    private int CoinMultiplier() => Pick([(1, 450), (2, 300), (3, 130), (5, 70), (10, 25), (15, 5)]);

    private T Pick<T>(ReadOnlySpan<(T Value, double Weight)> table)
    {
        double total = 0;
        foreach (var item in table) total += item.Weight;
        double roll = _random.NextDouble() * total;
        foreach (var item in table)
            if ((roll -= item.Weight) < 0) return item.Value;
        return table[^1].Value;
    }

    /// <summary>Line wins pay left to right from reel one; wilds substitute for every paying symbol.</summary>
    public static List<SlotLineWin> Evaluate(IReadOnlyList<SlotCell> grid, decimal bet)
    {
        var wins = new List<SlotLineWin>();
        for (int line = 0; line < Paylines.Count; line++)
        {
            var rows = Paylines[line];
            SlotSymbol? target = null;
            int count = 0, wilds = 0;
            bool leadingWilds = true;
            for (int reel = 0; reel < Reels; reel++)
            {
                var symbol = grid[Index(reel, BaseFirstRow + rows[reel])].Symbol;
                if (symbol == SlotSymbol.Wild)
                {
                    count++;
                    if (leadingWilds) wilds++;
                    continue;
                }
                leadingWilds = false;
                if (!Paytable.ContainsKey(symbol) || target is { } chosen && chosen != symbol) break;
                target = symbol;
                count++;
            }
            decimal symbolPay = target is { } paying && count >= 3 ? Paytable[paying][count - 3] : 0;
            decimal wildPay = wilds >= 3 ? Paytable[SlotSymbol.Crown][wilds - 3] : 0;
            if (symbolPay == 0 && wildPay == 0) continue;
            bool wildBetter = wildPay > symbolPay;
            int length = wildBetter ? wilds : count;
            wins.Add(new(line, wildBetter ? SlotSymbol.Wild : target!.Value, length,
                Math.Max(symbolPay, wildPay) * bet,
                Enumerable.Range(0, length).Select(reel => new SlotPosition(reel, BaseFirstRow + rows[reel])).ToArray()));
        }
        return wins;
    }

    private static IReadOnlyList<IReadOnlyList<int>> BuildPaylines()
    {
        var lines = new List<int[]> { new[] { 1, 1, 1, 1, 1 }, new[] { 0, 0, 0, 0, 0 }, new[] { 2, 2, 2, 2, 2 },
            new[] { 0, 1, 2, 1, 0 }, new[] { 2, 1, 0, 1, 2 } };
        var smooth = new List<int[]>();
        Walk(new int[Reels], 0);
        foreach (var line in smooth.OrderBy(Variation).ThenBy(Turns).ThenBy(item => string.Concat(item)))
        {
            if (lines.Count == PaylineCount) break;
            if (!lines.Any(existing => existing.SequenceEqual(line))) lines.Add(line);
        }
        return lines.Select(line => (IReadOnlyList<int>)Array.AsReadOnly(line)).ToArray();

        void Walk(int[] rows, int reel)
        {
            if (reel == Reels)
            {
                smooth.Add((int[])rows.Clone());
                return;
            }
            for (int row = 0; row < BaseRowCount; row++)
            {
                if (reel > 0 && Math.Abs(row - rows[reel - 1]) > 1) continue;
                rows[reel] = row;
                Walk(rows, reel + 1);
            }
        }
        static int Variation(int[] line) => Enumerable.Range(1, line.Length - 1).Sum(index => Math.Abs(line[index] - line[index - 1]));
        static int Turns(int[] line)
        {
            int turns = 0, direction = 0;
            for (int index = 1; index < line.Length; index++)
            {
                int step = Math.Sign(line[index] - line[index - 1]);
                if (step == 0) continue;
                if (direction != 0 && step != direction) turns++;
                direction = step;
            }
            return turns;
        }
    }

    private void Credit(decimal amount)
    {
        _balance += amount;
        _roundWin += amount;
        if (_inFreeSpins) _freeWin += amount;
    }

    private void Enter(SlotPhase phase, TimeSpan duration, DateTimeOffset now, string status, string banner = "")
    {
        _phase = phase;
        _phaseStarted = now;
        _phaseDuration = duration;
        _phaseEnds = now + duration;
        _status = status;
        _banner = banner;
    }

    private bool FeatureFull()
    {
        for (int reel = 0; reel < Reels; reel++)
            for (int row = _firstRow; row < _firstRow + _rowCount; row++)
                if (_feature[Index(reel, row)].Symbol == SlotSymbol.Empty) return false;
        return true;
    }

    private decimal FeatureTotal() => _feature.Sum(cell => cell.Value);

    private static int CountBase(SlotCell[] grid, Func<SlotCell, bool> match)
    {
        int count = 0;
        for (int reel = 0; reel < Reels; reel++)
            for (int row = BaseFirstRow; row < BaseFirstRow + BaseRowCount; row++)
                if (match(grid[Index(reel, row)])) count++;
        return count;
    }

    private int BetIndex() => Math.Max(0, BetOptions.ToList().IndexOf(_bet));

    private static int BasePlace(int place) => Index(place / BaseRowCount, BaseFirstRow + place % BaseRowCount);

    public static int Index(int reel, int row) => reel * Rows + row;

    private static SlotCell[] BlankGrid() => Enumerable.Repeat(SlotCell.Empty, Reels * Rows).ToArray();

    public static string JackpotName(SlotJackpot jackpot) => jackpot switch
    {
        SlotJackpot.Mini => "Mini", SlotJackpot.Minor => "Minor", SlotJackpot.Major => "Major",
        SlotJackpot.Grand => "Grand", _ => string.Empty
    };

    public static string Format(decimal amount) => amount.ToString("#,0.##", System.Globalization.CultureInfo.InvariantCulture);

    private bool ObserveTime(DateTimeOffset now)
    {
        if (_lastNow is { } last && now < last) return false;
        _lastNow = now;
        return true;
    }

    private void Changed()
    {
        Revision++;
        _snapshot = null;
    }

    private SlotSnapshot CreateSnapshot()
    {
        decimal bet = _phase is SlotPhase.VaultIntro or SlotPhase.VaultPicking or SlotPhase.VaultOutro ? _vaultBet : _bet;
        return new(Revision, _phase, _phaseStarted, _phaseDuration, _balance, _bet, BetOptions,
            Array.AsReadOnly((_inRespins ? _feature : _grid).ToArray()), Array.AsReadOnly(_previous.ToArray()),
            _inRespins ? _firstRow : BaseFirstRow, _inRespins ? _rowCount : BaseRowCount,
            _spinNumber, _freeSpin, _lineWins.AsReadOnly(), _roundWin,
            _inRespins, _respinsLeft, _fresh.ToArray(), _effect, _effectCell, _boost, _effectAmount,
            _respinTotal, _grandFill, Array.AsReadOnly(_keys.ToArray()), _inFreeSpins, _freeRemaining, _freePlayed,
            _elixirLevel, _freeWin, Array.AsReadOnly(_chests.ToArray()), _vaultNewest, _vaultAward,
            JackpotMultipliers.ToDictionary(pair => pair.Key, pair => pair.Value * bet), BuyCost,
            _status, _banner, AvailableActions().ToArray())
        {
            SpinStartedAt = _spinStartedAt,
            DragonHatches = Array.AsReadOnly(_dragonHatches.ToArray())
        };
    }
}
