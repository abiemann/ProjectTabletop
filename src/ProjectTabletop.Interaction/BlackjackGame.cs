namespace ProjectTabletop.Interaction;

public enum BlackjackSuit { Clubs, Diamonds, Hearts, Spades }
public enum BlackjackPhase { Betting, PlayerTurn, DealerTurn, RoundOver }

public sealed record BlackjackCard(int Rank, BlackjackSuit Suit)
{
    public string DisplayRank => Rank switch { 1 => "A", 11 => "J", 12 => "Q", 13 => "K", _ => Rank.ToString() };
    public bool IsRed => Suit is BlackjackSuit.Diamonds or BlackjackSuit.Hearts;
}

public sealed record BlackjackHandSnapshot(IReadOnlyList<BlackjackCard> Cards, int Total, bool IsSoft,
    decimal Bet, bool IsActive, bool IsBust, bool IsNatural, string Result);

/// <summary>The dealer's concealed card is null; totals also exclude it until the reveal.</summary>
public sealed record BlackjackSnapshot(BlackjackPhase Phase, IReadOnlyList<BlackjackCard?> DealerCards,
    int DealerTotal, bool DealerIsSoft, bool DealerHoleCardHidden, IReadOnlyList<BlackjackHandSnapshot> Hands,
    int ActiveHandIndex, decimal Bankroll, decimal SelectedBet, string Status,
    IReadOnlyList<string> AvailableActions, int RoundNumber, long Revision);

/// <summary>
/// Single-player, six-deck blackjack with virtual credits. Stands on soft 17, pays naturals 3:2,
/// allows one equal-rank split and doubling after a split; split aces receive one card each.
/// Call from one thread. The optional initial shoe is drawn in enumeration order for deterministic tests.
/// </summary>
public sealed class BlackjackGame
{
    private static readonly TimeSpan DealerPause = TimeSpan.FromMilliseconds(650);
    private static readonly int[] Bets = [10, 25, 50, 100];
    private readonly Random _random;
    private readonly List<Hand> _hands = [];
    private readonly List<BlackjackCard> _dealer = [];
    private Queue<BlackjackCard> _shoe;
    private bool _usingInitialShoe;
    private bool _holeHidden;
    private int _activeHand = -1;
    private DateTimeOffset _nextDealerStep;
    private DateTimeOffset? _lastNow;
    private BlackjackPhase _phase = BlackjackPhase.Betting;
    private decimal _bankroll = 1000m;
    private decimal _selectedBet = 25m;
    private string _status = "Choose your bet, then deal.";
    private int _round;
    private BlackjackSnapshot? _snapshot;

    public BlackjackGame(int? seed = null, IEnumerable<BlackjackCard>? initialShoe = null)
    {
        _random = seed is { } value ? new Random(value) : new Random();
        if (initialShoe is null)
        {
            _shoe = NewShoe();
        }
        else
        {
            var cards = initialShoe.ToArray();
            if (cards.Any(card => card is null || card.Rank is < 1 or > 13 || !Enum.IsDefined(card.Suit)))
                throw new ArgumentException("Every shoe card must have a rank from 1 to 13 and a valid suit.", nameof(initialShoe));
            _shoe = new Queue<BlackjackCard>(cards);
            _usingInitialShoe = true;
        }
    }

    public long Revision { get; private set; }

    public BlackjackSnapshot Snapshot => _snapshot ??= CreateSnapshot();

    public bool HandleAction(string id, DateTimeOffset now)
    {
        if (!ObserveTime(now) || !AvailableActions().Contains(id, StringComparer.Ordinal)) return false;
        switch (id)
        {
            case "bj-deal": Deal(now); break;
            case "bj-hit":
                var hitting = _hands[_activeHand];
                hitting.Cards.Add(Draw());
                var hitValue = Value(hitting.Cards).Total;
                if (hitValue >= 21)
                {
                    hitting.Done = true;
                    if (hitValue > 21) hitting.Result = "BUST";
                    AdvanceHand(now);
                }
                else UpdateTurnStatus();
                break;
            case "bj-stand":
                _hands[_activeHand].Done = true;
                AdvanceHand(now);
                break;
            case "bj-double":
                var doubling = _hands[_activeHand];
                _bankroll -= doubling.Bet;
                doubling.Bet *= 2;
                doubling.Cards.Add(Draw());
                doubling.Done = true;
                if (Value(doubling.Cards).Total > 21) doubling.Result = "BUST";
                AdvanceHand(now);
                break;
            case "bj-split": Split(now); break;
            case "bj-reset":
                _bankroll = 1000m;
                _selectedBet = 25m;
                _hands.Clear();
                _dealer.Clear();
                _activeHand = -1;
                _holeHidden = false;
                _phase = BlackjackPhase.Betting;
                _status = "1,000 credits restored. Choose your bet.";
                break;
            default:
                _selectedBet = int.Parse(id.AsSpan("bj-bet-".Length), System.Globalization.CultureInfo.InvariantCulture);
                _status = $"Bet {Format(_selectedBet)} credits. Ready to deal.";
                break;
        }
        Changed();
        return true;
    }

    /// <summary>Advances at most one dealer reveal/draw/settlement, preserving visible pauses even after a late frame.</summary>
    public bool Tick(DateTimeOffset now)
    {
        if (!ObserveTime(now) || _phase != BlackjackPhase.DealerTurn || now < _nextDealerStep) return false;
        if (_holeHidden)
        {
            _holeHidden = false;
            _status = "Dealer reveals the hole card.";
        }
        else if (_hands.Any(hand => Value(hand.Cards).Total <= 21) && Value(_dealer).Total < 17)
        {
            _dealer.Add(Draw());
            _status = Value(_dealer).Total > 21 ? "Dealer busts!" : "Dealer draws.";
        }
        else
        {
            Settle();
        }
        _nextDealerStep = now + DealerPause;
        Changed();
        return true;
    }

    private void Deal(DateTimeOffset now)
    {
        // Start a fresh shoe before a round gets close to running out. An explicitly supplied
        // test shoe is retained; Draw also handles its exhaustion without dropping the round.
        if (!_usingInitialShoe && _shoe.Count < 52) _shoe = NewShoe();
        _hands.Clear();
        _dealer.Clear();
        _bankroll -= _selectedBet;
        var hand = new Hand(_selectedBet);
        _hands.Add(hand);
        hand.Cards.Add(Draw());
        _dealer.Add(Draw());
        hand.Cards.Add(Draw());
        _dealer.Add(Draw());
        _round++;
        _holeHidden = true;
        _activeHand = 0;
        _phase = BlackjackPhase.PlayerTurn;
        UpdateTurnStatus();

        // The dealer peeks immediately. A two-card player natural also finishes immediately.
        if (IsNatural(hand) || Value(_dealer).Total == 21) Settle();
    }

    private void Split(DateTimeOffset now)
    {
        var first = _hands[0];
        _bankroll -= first.Bet;
        var second = new Hand(first.Bet) { FromSplit = true };
        first.FromSplit = true;
        second.Cards.Add(first.Cards[1]);
        first.Cards.RemoveAt(1);
        first.Cards.Add(Draw());
        second.Cards.Add(Draw());
        _hands.Add(second);
        bool aces = first.Cards[0].Rank == 1;
        first.Done = aces || Value(first.Cards).Total == 21;
        second.Done = aces || Value(second.Cards).Total == 21;
        _activeHand = 0;
        if (first.Done) AdvanceHand(now);
        else UpdateTurnStatus();
    }

    private void AdvanceHand(DateTimeOffset now)
    {
        int next = _hands.FindIndex(hand => !hand.Done);
        if (next >= 0)
        {
            _activeHand = next;
            UpdateTurnStatus();
            return;
        }
        _activeHand = -1;
        _phase = BlackjackPhase.DealerTurn;
        _status = "Dealer's turn.";
        _nextDealerStep = now + DealerPause;
    }

    private void UpdateTurnStatus()
    {
        var hand = _hands[_activeHand];
        string lead = _hands.Count > 1 ? $"Hand {_activeHand + 1} of {_hands.Count}. " : "Your turn. ";
        _status = lead + (hand.Cards.Count == 2 && _bankroll >= hand.Bet ? "Hit, stand, or double." : "Hit or stand.");
    }

    private void Settle()
    {
        // This is entered only from an active round. Once RoundOver, actions and ticks cannot pay again.
        _holeHidden = false;
        _activeHand = -1;
        int dealerTotal = Value(_dealer).Total;
        bool dealerNatural = _dealer.Count == 2 && dealerTotal == 21;
        foreach (var hand in _hands)
        {
            hand.Done = true;
            int total = Value(hand.Cards).Total;
            if (total > 21) hand.Result = "BUST";
            else if (dealerNatural && !IsNatural(hand)) hand.Result = "DEALER BLACKJACK";
            else if (IsNatural(hand) && !dealerNatural)
            {
                _bankroll += hand.Bet * 2.5m;
                hand.Result = $"BLACKJACK +{Format(hand.Bet * 1.5m)}";
            }
            else if (dealerNatural || total == dealerTotal)
            {
                _bankroll += hand.Bet;
                hand.Result = "PUSH";
            }
            else if (dealerTotal > 21 || total > dealerTotal)
            {
                _bankroll += hand.Bet * 2;
                hand.Result = $"WIN +{Format(hand.Bet)}";
            }
            else hand.Result = "DEALER WINS";
        }
        _phase = BlackjackPhase.RoundOver;
        _status = _hands.Count == 1 ? _hands[0].Result + ". Place your next bet."
            : $"Hand 1: {_hands[0].Result} · Hand 2: {_hands[1].Result}";
        if (_bankroll < Bets[0]) _status = "Out of credits. Reset chips to play again.";
        if (_bankroll < _selectedBet)
            _selectedBet = Bets.Where(bet => bet <= _bankroll).DefaultIfEmpty(Bets[0]).Max();
    }

    private IReadOnlyList<string> AvailableActions()
    {
        var actions = new List<string>();
        if (_phase is BlackjackPhase.Betting or BlackjackPhase.RoundOver)
        {
            actions.AddRange(Bets.Where(bet => bet <= _bankroll).Select(bet => $"bj-bet-{bet}"));
            if (_selectedBet <= _bankroll) actions.Add("bj-deal");
            actions.Add("bj-reset");
        }
        else if (_phase == BlackjackPhase.PlayerTurn)
        {
            var hand = _hands[_activeHand];
            actions.AddRange(["bj-hit", "bj-stand"]);
            if (hand.Cards.Count == 2 && _bankroll >= hand.Bet)
            {
                actions.Add("bj-double");
                if (_hands.Count == 1 && hand.Cards[0].Rank == hand.Cards[1].Rank) actions.Add("bj-split");
            }
        }
        return actions.AsReadOnly();
    }

    private BlackjackSnapshot CreateSnapshot()
    {
        var dealer = _dealer.Select((card, index) => _holeHidden && index == 1 ? null : card).ToArray();
        var visibleValue = Value(dealer.OfType<BlackjackCard>());
        var hands = _hands.Select((hand, index) =>
        {
            var value = Value(hand.Cards);
            return new BlackjackHandSnapshot(Array.AsReadOnly(hand.Cards.ToArray()), value.Total, value.IsSoft,
                hand.Bet, _phase == BlackjackPhase.PlayerTurn && index == _activeHand,
                value.Total > 21, IsNatural(hand), hand.Result);
        }).ToArray();
        return new BlackjackSnapshot(_phase, Array.AsReadOnly(dealer), visibleValue.Total, visibleValue.IsSoft,
            _holeHidden, Array.AsReadOnly(hands), _activeHand, _bankroll, _selectedBet, _status,
            AvailableActions(), _round, Revision);
    }

    private BlackjackCard Draw()
    {
        if (_shoe.Count == 0)
        {
            _shoe = NewShoe();
            _usingInitialShoe = false;
        }
        return _shoe.Dequeue();
    }

    private Queue<BlackjackCard> NewShoe()
    {
        var cards = Enumerable.Range(0, 6).SelectMany(_ => Enum.GetValues<BlackjackSuit>()
            .SelectMany(suit => Enumerable.Range(1, 13).Select(rank => new BlackjackCard(rank, suit)))).ToArray();
        _random.Shuffle(cards);
        return new Queue<BlackjackCard>(cards);
    }

    private bool ObserveTime(DateTimeOffset now)
    {
        if (_lastNow is { } last && now < last) return false;
        _lastNow = now;
        return true;
    }

    private void Changed() { Revision++; _snapshot = null; }
    private static bool IsNatural(Hand hand) => !hand.FromSplit && hand.Cards.Count == 2 && Value(hand.Cards).Total == 21;
    private static string Format(decimal credits) => credits.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static (int Total, bool IsSoft) Value(IEnumerable<BlackjackCard> cards)
    {
        int total = 0, aces = 0;
        foreach (var card in cards)
        {
            total += card.Rank == 1 ? 11 : Math.Min(card.Rank, 10);
            if (card.Rank == 1) aces++;
        }
        while (total > 21 && aces > 0) { total -= 10; aces--; }
        return (total, aces > 0);
    }

    private sealed class Hand(decimal bet)
    {
        public List<BlackjackCard> Cards { get; } = [];
        public decimal Bet { get; set; } = bet;
        public bool Done { get; set; }
        public bool FromSplit { get; set; }
        public string Result { get; set; } = string.Empty;
    }
}
