using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjectTabletop.Interaction;

/// <summary>An accepted human or AI roll, after its complete gameplay result is committed.</summary>
public sealed record CrownDeedRoll(long Sequence, CrownDeedSnapshot Previous, CrownDeedSnapshot Current,
    DateTimeOffset StartedAt);

/// <summary>An accepted development, emitted only after its cash and building change are committed.</summary>
public sealed record CrownDeedDevelopment(long Sequence, int SpaceIndex, int PlayerId,
    CrownDeedSnapshot Previous, CrownDeedSnapshot Current, DateTimeOffset StartedAt);

/// <summary>
/// Crown &amp; Deed, an original merchant-city game for two to six local players. State changes are synchronous;
/// AI makes at most one visible decision per Tick. Saves preserve decks and decisions.
/// </summary>
public sealed partial class CrownDeedGame
{
    private static readonly TimeSpan AiPause = TimeSpan.FromMilliseconds(1000);
    private static readonly JsonSerializerOptions SaveOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly Random _random;
    private readonly Queue<CrownDeedDice> _initialRolls;
    private CrownDeedSaveData _state = new() { Phase = CrownDeedPhase.Landing, ActivePlayerIndex = -1,
        Status = "Build a merchant city. Leave your mark on the boulevard." };
    private CrownDeedSnapshot? _snapshot;
    private DateTimeOffset _nextAiStep;
    private DateTimeOffset? _lastNow;
    private CrownDeedPhase _exitReturnPhase;
    private string _exitReturnStatus = "";
    private string? _resumeSave;
    private long _rollSequence;
    private long _developmentSequence;
    private (CrownDeedSnapshot Previous, int SpaceIndex, int PlayerId)? _pendingDevelopment;

    public CrownDeedGame(int? seed = null, IEnumerable<CrownDeedDice>? initialRolls = null)
    {
        _random = seed.HasValue ? new Random(seed.Value) : new Random();
        _initialRolls = new(initialRolls ?? []);
        if (_initialRolls.Any(d => d.First is < 1 or > 6 || d.Second is < 1 or > 6))
            throw new ArgumentException("Dice must be between one and six.", nameof(initialRolls));
    }

    public long Revision { get; private set; }
    public CrownDeedSnapshot Snapshot => _snapshot ??= CreateSnapshot();

    /// <summary>Raised once per accepted roll; sequence numbers survive new games and save loads.</summary>
    public event Action<CrownDeedRoll>? RollOccurred;

    /// <summary>Raised once for a human or AI build; not replayed by saves, loads, selling or drawing.</summary>
    public event Action<CrownDeedDevelopment>? DevelopmentOccurred;

    public bool HandleAction(string id, DateTimeOffset now)
    {
        if (!ObserveTime(now) || !AvailableActions().Contains(id, StringComparer.Ordinal)) return false;
        var beforeRoll = id == "mp-roll" ? Snapshot : null;
        if (id == "mp-exit")
        {
            if (_state.Players.Count == 0 || _state.Phase == CrownDeedPhase.GameOver)
            {
                ReturnToLanding();
                Changed();
                return true;
            }
            _exitReturnPhase = _state.Phase;
            _exitReturnStatus = _state.Status;
            _state.Phase = CrownDeedPhase.ExitConfirmation;
            _state.Status = "Save this game and return to it later.";
        }
        else if (id == "mp-exit-cancel")
        {
            _state.Phase = _exitReturnPhase;
            _state.Status = _exitReturnStatus;
        }
        else if (id == "mp-save-exit")
        {
            _state.Phase = CrownDeedPhase.Saving;
            _state.Status = "Saving your game...";
        }
        else if (id == "mp-exit-without-saving") ReturnToLanding();
        else if (id == "mp-start-game")
        {
            _state.Phase = CrownDeedPhase.Setup;
            _state.Status = "Gather your companies. Two to six merchants can join.";
        }
        else if (id == "mp-setup-cancel")
        {
            _state.Phase = CrownDeedPhase.Landing;
            _state.Status = "Build a merchant city. Leave your mark on the boulevard.";
        }
        else if (id == "mp-human-plus") _state.Humans++;
        else if (id == "mp-human-minus") _state.Humans--;
        else if (id == "mp-ai-plus") _state.Ais++;
        else if (id == "mp-ai-minus") _state.Ais--;
        else if (id.StartsWith("mp-piece-next-", StringComparison.Ordinal))
            CycleSetupPiece(int.Parse(id.AsSpan("mp-piece-next-".Length)) - 1);
        else if (id == "mp-start") Start(now);
        else if (id == "mp-resume")
        {
            LoadSave(_resumeSave!, now);
            return true;
        }
        else if (id == "mp-new-game")
        {
            _state = new() { Phase = CrownDeedPhase.Setup, ActivePlayerIndex = -1,
                Status = "Gather your companies. Two to six merchants can join." };
        }
        else PerformGameAction(id, now);
        if (_state.Phase == CrownDeedPhase.Setup) ReconcileSetupPieces();
        _nextAiStep = now + AiPause;
        Changed();
        PublishRoll(beforeRoll, now);
        PublishDevelopment(now);
        return true;
    }

    public bool Tick(DateTimeOffset now)
    {
        if (!ObserveTime(now) || now < _nextAiStep || _state.Phase is CrownDeedPhase.Landing or CrownDeedPhase.Setup
            or CrownDeedPhase.GameOver or CrownDeedPhase.ExitConfirmation or CrownDeedPhase.Saving or CrownDeedPhase.ManageProperties)
            return false;
        var actor = ActingPlayer();
        if (_state.Phase == CrownDeedPhase.AwaitingEndTurn && Active.Bankrupt)
        {
            EndTurn();
            _nextAiStep = now + AiPause;
            Changed();
            return true;
        }
        if (actor is null || !actor.IsAi || actor.Bankrupt) return false;
        string? action = _state.Phase switch
        {
            CrownDeedPhase.AwaitingRoll => actor.InJail && actor.GetOutOfJailCards > 0 ? "mp-jail-card" : "mp-roll",
            CrownDeedPhase.AwaitingPurchase => actor.Money >= Spaces[_state.PendingPropertyIndex!.Value].Price + 120 ? "mp-buy" : "mp-auction",
            CrownDeedPhase.Auction => AiAuctionAction(actor),
            CrownDeedPhase.AwaitingEndTurn => AiManageOrEnd(actor),
            CrownDeedPhase.Debt => AiLiquidate(actor, now),
            _ => null
        };
        var beforeRoll = action == "mp-roll" ? Snapshot : null;
        if (action is not null) PerformGameAction(action, now);
        _nextAiStep = now + AiPause;
        Changed();
        PublishRoll(beforeRoll, now);
        PublishDevelopment(now);
        return true;
    }

    public string ExportSave()
    {
        if (_state.Players.Count == 0) throw new InvalidOperationException("There is no game to save.");
        var phase = _state.Phase;
        var status = _state.Status;
        try
        {
            if (phase is CrownDeedPhase.ExitConfirmation or CrownDeedPhase.Saving)
            {
                _state.Phase = _exitReturnPhase;
                _state.Status = _exitReturnStatus;
            }
            return JsonSerializer.Serialize(_state, SaveOptions);
        }
        finally { _state.Phase = phase; _state.Status = status; }
    }

    /// <summary>Validates first, then replaces gameplay atomically. Invalid files leave the game intact.</summary>
    public void LoadSave(string json, DateTimeOffset now)
    {
        var restored = ParseSave(json);
        _state = restored;
        for (int slot = 0; slot < restored.Players.Count; slot++)
            _setupPieces[slot] = restored.Players[slot].PieceIndex!.Value;
        _lastNow = now;
        _nextAiStep = now + AiPause;
        Changed();
    }

    public void StageSave(string json)
    {
        _ = ParseSave(json);
        _resumeSave = json;
        Changed();
    }

    public void CompleteSave(bool success, DateTimeOffset now, string? error = null)
    {
        if (_state.Phase != CrownDeedPhase.Saving) return;
        if (success)
        {
            _resumeSave = ExportSave();
            ReturnToLanding();
        }
        else
        {
            _state.Phase = CrownDeedPhase.ExitConfirmation;
            _state.Status = string.IsNullOrWhiteSpace(error) ? "Save failed. Please try again."
                : error.StartsWith("Save failed", StringComparison.OrdinalIgnoreCase) ? error : "Save failed. " + error;
        }
        _nextAiStep = now + AiPause;
        Changed();
    }

    private void Start(DateTimeOffset now)
    {
        ReconcileSetupPieces();
        int humans = _state.Humans, ais = _state.Ais;
        _state = new() { Phase = CrownDeedPhase.AwaitingRoll, Humans = humans, Ais = ais,
            ActivePlayerIndex = 0, TurnNumber = 1, ChanceDeck = ShuffleDeck(), ChestDeck = ShuffleDeck() };
        for (int i = 0; i < humans + ais; i++)
            _state.Players.Add(new() { Id = i + 1, Name = i < humans ? $"Player {i + 1}" : $"AI {i - humans + 1}",
                IsAi = i >= humans, Money = 1500, ColorIndex = i, PieceIndex = _setupPieces[i] });
        _state.Properties = Spaces.Where(IsPurchasable).Select(s => new CrownDeedSavedProperty { SpaceIndex = s.Index }).ToList();
        _state.Status = $"{Active.Name}, roll the dice to begin.";
        _nextAiStep = now + AiPause;
    }

    private void ReturnToLanding() => _state = new() { Phase = CrownDeedPhase.Landing, ActivePlayerIndex = -1,
        Humans = _state.Humans, Ais = _state.Ais, Status = "Build a merchant city. Leave your mark on the boulevard." };

    private void PerformGameAction(string id, DateTimeOffset now)
    {
        switch (id)
        {
            case "mp-roll": Roll(now); break;
            case "mp-jail-pay":
                Active.Money -= 50;
                Active.InJail = false;
                Active.JailTurns = 0;
                _state.Status = $"{Active.Name} paid 50 crowns. Roll to leave Civic Watch.";
                break;
            case "mp-jail-card":
                if (_state.ChanceFreeCardHolderId == Active.Id) ReturnJailCard(chance: true);
                else if (_state.ChestFreeCardHolderId == Active.Id) ReturnJailCard(chance: false);
                Active.GetOutOfJailCards--;
                Active.InJail = false;
                Active.JailTurns = 0;
                _state.Status = $"{Active.Name} used a Safe-Conduct Pass.";
                break;
            case "mp-buy":
                var space = Spaces[_state.PendingPropertyIndex!.Value];
                Active.Money -= space.Price;
                PropertyAt(space.Index).OwnerId = Active.Id;
                _state.Status = $"{Active.Name} bought {space.Name} for {space.Price} crowns.";
                _state.PendingPropertyIndex = null;
                FinishLanding();
                break;
            case "mp-auction": BeginAuction(_state.PendingPropertyIndex!.Value); break;
            case "mp-bid-10": Bid(10); break;
            case "mp-bid-50": Bid(50); break;
            case "mp-bid-100": Bid(100); break;
            case "mp-pass": PassAuction(); break;
            case "mp-end-turn": EndTurn(); break;
            case "mp-manage":
                _state.ManageReturnPhase = _state.Phase;
                _state.Phase = CrownDeedPhase.ManageProperties;
                _state.SelectedPropertyIndex = Owned(ActingOwnerId()).FirstOrDefault()?.SpaceIndex;
                break;
            case "mp-property-next": SelectProperty(1); break;
            case "mp-property-previous": SelectProperty(-1); break;
            case "mp-manage-back": _state.Phase = _state.ManageReturnPhase; break;
            case "mp-build": Build(PropertyAt(_state.SelectedPropertyIndex!.Value)); break;
            case "mp-sell": Sell(PropertyAt(_state.SelectedPropertyIndex!.Value)); ResolveDebtIfPaid(); break;
            case "mp-sell-group": SellGroup(PropertyAt(_state.SelectedPropertyIndex!.Value)); ResolveDebtIfPaid(); break;
            case "mp-mortgage": Mortgage(PropertyAt(_state.SelectedPropertyIndex!.Value)); ResolveDebtIfPaid(); break;
            case "mp-unmortgage":
                var mortgaged = PropertyAt(_state.SelectedPropertyIndex!.Value);
                Player(mortgaged.OwnerId!.Value).Money -= UnmortgageCost(mortgaged);
                mortgaged.Mortgaged = false;
                _state.Status = $"Mortgage lifted on {Spaces[mortgaged.SpaceIndex].Name}.";
                break;
            case "mp-bankrupt": DeclareBankrupt(Player(_state.DebtPlayerId!.Value)); break;
        }
    }

    private void Roll(DateTimeOffset now)
    {
        _state.LastCard = "";
        _state.Dice = _initialRolls.Count > 0 ? _initialRolls.Dequeue() : new(_random.Next(1, 7), _random.Next(1, 7));
        if (Active.InJail)
        {
            Active.JailTurns++;
            _state.ExtraRoll = false;
            if (_state.Dice.IsDouble)
            {
                Active.InJail = false;
                Active.JailTurns = 0;
                MoveBy(_state.Dice.Total);
                ResolveLanding();
            }
            else if (Active.JailTurns == 3)
            {
                Active.InJail = false;
                Active.JailTurns = 0;
                Charge(Active.Id, 50, null, "jail-move");
            }
            else
            {
                _state.Status = $"{Active.Name} rolled {_state.Dice.First} + {_state.Dice.Second} and stays in Civic Watch.";
                _state.Phase = CrownDeedPhase.AwaitingEndTurn;
            }
            return;
        }
        _state.DoublesCount = _state.Dice.IsDouble ? _state.DoublesCount + 1 : 0;
        _state.ExtraRoll = _state.Dice.IsDouble;
        if (_state.DoublesCount == 3)
        {
            SendToJail("Three hurried circuits draw a civic review. Report to Civic Watch.");
            return;
        }
        MoveBy(_state.Dice.Total);
        ResolveLanding();
    }

    private void MoveBy(int distance, bool collectGo = true)
    {
        int position = Active.Position + distance;
        if (collectGo && position >= 40) Active.Money += 200;
        Active.Position = (position % 40 + 40) % 40;
    }

    private void MoveTo(int destination, bool collectGo = true)
    {
        if (collectGo && destination < Active.Position) Active.Money += 200;
        Active.Position = destination;
    }

    private void ResolveLanding(int railroadMultiplier = 1, bool specialUtility = false)
    {
        var space = Spaces[Active.Position];
        _state.Status = $"{Active.Name} landed on {space.Name}.";
        if (IsPurchasable(space))
        {
            var property = PropertyAt(space.Index);
            if (property.OwnerId is null)
            {
                _state.PendingPropertyIndex = space.Index;
                _state.Phase = CrownDeedPhase.AwaitingPurchase;
                _state.Status += $" Buy for {space.Price} crowns, or send it to auction.";
            }
            else if (property.OwnerId != Active.Id && !property.Mortgaged)
            {
                int rent = Rent(property) * railroadMultiplier;
                if (specialUtility)
                {
                    var utilityDice = _initialRolls.Count > 0 ? _initialRolls.Dequeue() : new(_random.Next(1, 7), _random.Next(1, 7));
                    rent = utilityDice.Total * 10;
                }
                _state.Status = $"{Active.Name} owes {rent} crowns rent to {Player(property.OwnerId.Value).Name}.";
                Charge(Active.Id, rent, property.OwnerId, "finish");
            }
            else FinishLanding();
        }
        else switch (space.Kind)
        {
            case CrownDeedSpaceKind.GoToJail: SendToJail("Report to Civic Watch. No circuit grant is paid."); break;
            case CrownDeedSpaceKind.Tax: Charge(Active.Id, space.Price, null, "finish"); break;
            case CrownDeedSpaceKind.Chance: DrawCard(true); break;
            case CrownDeedSpaceKind.CommunityChest: DrawCard(false); break;
            default: FinishLanding(); break;
        }
    }

    private void FinishLanding()
    {
        if (CheckWinner()) return;
        if (_state.PendingBankAuctions.Count > 0)
        {
            int property = _state.PendingBankAuctions[0];
            _state.PendingBankAuctions.RemoveAt(0);
            BeginAuction(property);
            return;
        }
        _state.Phase = _state.ExtraRoll && !Active.Bankrupt && !Active.InJail
            ? CrownDeedPhase.AwaitingRoll : CrownDeedPhase.AwaitingEndTurn;
        if (_state.Phase == CrownDeedPhase.AwaitingRoll) _state.Status += " Doubles: roll again.";
    }

    private void SendToJail(string message)
    {
        Active.Position = CivicWatchIndex;
        Active.InJail = true;
        Active.JailTurns = 0;
        _state.ExtraRoll = false;
        _state.DoublesCount = 0;
        _state.Status = $"{Active.Name}: {message}";
        _state.Phase = CrownDeedPhase.AwaitingEndTurn;
    }

    private void EndTurn()
    {
        if (CheckWinner()) return;
        do { _state.ActivePlayerIndex = (_state.ActivePlayerIndex + 1) % _state.Players.Count; }
        while (Active.Bankrupt);
        _state.TurnNumber++;
        _state.DoublesCount = 0;
        _state.ExtraRoll = false;
        _state.PendingPropertyIndex = null;
        _state.SelectedPropertyIndex = null;
        _state.Dice = default;
        _state.LastCard = "";
        _state.Phase = CrownDeedPhase.AwaitingRoll;
        _state.Status = Active.InJail ? $"{Active.Name} is in Civic Watch. Roll doubles, pay 50 crowns, or use a card."
            : $"{Active.Name}, your turn. Roll the dice.";
    }

    private int Rent(CrownDeedSavedProperty property)
    {
        var space = Spaces[property.SpaceIndex];
        var owner = property.OwnerId!.Value;
        return space.Kind switch
        {
            CrownDeedSpaceKind.Railroad => 25 * (1 << (Owned(owner).Count(p => Spaces[p.SpaceIndex].Kind == CrownDeedSpaceKind.Railroad) - 1)),
            CrownDeedSpaceKind.Utility => _state.Dice.Total * (Owned(owner).Count(p => Spaces[p.SpaceIndex].Kind == CrownDeedSpaceKind.Utility) == 2 ? 10 : 4),
            _ => space.Rents[property.Houses] * (property.Houses == 0 && OwnsGroup(owner, space.Group) ? 2 : 1)
        };
    }

    private void BeginAuction(int spaceIndex)
    {
        _state.PendingPropertyIndex = spaceIndex;
        _state.Auction = new() { SpaceIndex = spaceIndex, CurrentBidderId = _state.Players.First(p => !p.Bankrupt).Id };
        _state.Phase = CrownDeedPhase.Auction;
        _state.Status = $"Auction: {Spaces[spaceIndex].Name}. Everyone may bid, including the player who declined.";
    }

    private void Bid(int increment)
    {
        var auction = _state.Auction!;
        auction.Bid += increment;
        auction.BidderId = auction.CurrentBidderId;
        _state.Status = $"{Player(auction.BidderId.Value).Name} bids {auction.Bid} crowns.";
        AdvanceAuction();
    }

    private void PassAuction()
    {
        var auction = _state.Auction!;
        auction.PassedPlayerIds.Add(auction.CurrentBidderId);
        _state.Status = $"{Player(auction.CurrentBidderId).Name} passes.";
        AdvanceAuction();
    }

    private void AdvanceAuction()
    {
        var auction = _state.Auction!;
        var eligible = _state.Players.Where(p => !p.Bankrupt && !auction.PassedPlayerIds.Contains(p.Id)).ToList();
        if (eligible.Count == 0 || (eligible.Count == 1 && auction.BidderId == eligible[0].Id))
        {
            if (auction.BidderId is { } winner)
            {
                Player(winner).Money -= auction.Bid;
                PropertyAt(auction.SpaceIndex).OwnerId = winner;
                _state.Status = $"{Player(winner).Name} wins {Spaces[auction.SpaceIndex].Name} for {auction.Bid} crowns.";
            }
            else _state.Status = $"No bids. {Spaces[auction.SpaceIndex].Name} remains with the city treasury.";
            _state.Auction = null;
            _state.PendingPropertyIndex = null;
            FinishLanding();
            return;
        }
        int index = _state.Players.FindIndex(p => p.Id == auction.CurrentBidderId);
        do { index = (index + 1) % _state.Players.Count; }
        while (_state.Players[index].Bankrupt || auction.PassedPlayerIds.Contains(_state.Players[index].Id)
            || auction.BidderId == _state.Players[index].Id);
        auction.CurrentBidderId = _state.Players[index].Id;
    }

    private void Charge(int playerId, int amount, int? creditorId, string continuation)
    {
        _state.Payments.Add(new() { PlayerId = playerId, Amount = amount, CreditorId = creditorId });
        _state.DebtContinuation = continuation;
        ProcessPayments();
    }

    private void ProcessPayments()
    {
        while (_state.Payments.Count > 0)
        {
            var payment = _state.Payments[0];
            var debtor = Player(payment.PlayerId);
            if (debtor.Bankrupt) { _state.Payments.RemoveAt(0); continue; }
            if (debtor.Money < payment.Amount)
            {
                _state.DebtAmount = payment.Amount;
                _state.DebtPlayerId = debtor.Id;
                _state.DebtCreditorId = payment.CreditorId;
                _state.Phase = CrownDeedPhase.Debt;
                _state.Status = $"{debtor.Name} owes {payment.Amount} crowns. Sell buildings or mortgage property to raise funds.";
                return;
            }
            debtor.Money -= payment.Amount;
            if (payment.CreditorId is { } creditor && !Player(creditor).Bankrupt) Player(creditor).Money += payment.Amount;
            _state.Payments.RemoveAt(0);
        }
        _state.DebtAmount = 0;
        _state.DebtPlayerId = null;
        _state.DebtCreditorId = null;
        if (CheckWinner()) return;
        if (_state.DebtContinuation == "jail-move" && !Active.Bankrupt)
        {
            _state.DebtContinuation = "finish";
            MoveBy(_state.Dice.Total);
            ResolveLanding();
        }
        else FinishLanding();
    }

    private void ResolveDebtIfPaid()
    {
        if (_state.DebtPlayerId is { } id && Player(id).Money >= _state.DebtAmount) ProcessPayments();
    }

    private void DeclareBankrupt(CrownDeedSavedPlayer debtor)
    {
        int? creditor = _state.DebtCreditorId;
        var properties = Owned(debtor.Id).ToList();
        foreach (var property in properties)
        {
            // Buildings are returned to the city treasury at half their purchase price.
            debtor.Money += property.Houses * Spaces[property.SpaceIndex].HouseCost / 2;
            property.Houses = 0;
            property.OwnerId = creditor;
            if (creditor is null)
            {
                property.Mortgaged = false;
                _state.PendingBankAuctions.Add(property.SpaceIndex);
            }
        }
        if (creditor is { } recipient)
        {
            Player(recipient).Money += debtor.Money;
            Player(recipient).GetOutOfJailCards += debtor.GetOutOfJailCards;
            // The mortgage transfer interest is a separate Bank obligation.
            int interest = properties.Where(p => p.Mortgaged).Sum(MortgageInterest);
            if (interest > 0) _state.Payments.Add(new() { PlayerId = recipient, Amount = interest });
        }
        debtor.Money = 0;
        debtor.GetOutOfJailCards = 0;
        if (_state.ChanceFreeCardHolderId == debtor.Id)
        {
            if (creditor is null) ReturnJailCard(chance: true); else _state.ChanceFreeCardHolderId = creditor;
        }
        if (_state.ChestFreeCardHolderId == debtor.Id)
        {
            if (creditor is null) ReturnJailCard(chance: false); else _state.ChestFreeCardHolderId = creditor;
        }
        debtor.Bankrupt = true;
        _state.Status = $"{debtor.Name} is bankrupt.";
        _state.Payments.RemoveAll(p => p.PlayerId == debtor.Id);
        _state.DebtAmount = 0;
        _state.DebtPlayerId = null;
        _state.DebtCreditorId = null;
        _state.DebtContinuation = "finish";
        // Eliminating the final opponent ends the game before the recipient's
        // new mortgage-transfer obligations can require further liquidation.
        if (!CheckWinner()) ProcessPayments();
    }

    private bool CheckWinner()
    {
        if (_state.Players.Count == 0) return false;
        var alive = _state.Players.Where(p => !p.Bankrupt).ToList();
        if (alive.Count != 1) return false;
        _state.WinnerId = alive[0].Id;
        _state.Phase = CrownDeedPhase.GameOver;
        _state.Status = $"{alive[0].Name} wins the game!";
        _state.Auction = null;
        _state.PendingPropertyIndex = null;
        _state.SelectedPropertyIndex = null;
        _state.PendingBankAuctions.Clear();
        _state.Payments.Clear();
        _state.DebtAmount = 0;
        _state.DebtPlayerId = null;
        _state.DebtCreditorId = null;
        return true;
    }

    private IEnumerable<CrownDeedSavedProperty> Owned(int owner) => _state.Properties.Where(p => p.OwnerId == owner);
    private IEnumerable<CrownDeedSavedProperty> Group(CrownDeedGroup group) => _state.Properties.Where(p => Spaces[p.SpaceIndex].Group == group);
    private bool OwnsGroup(int owner, CrownDeedGroup group) => group != CrownDeedGroup.None && Group(group).All(p => p.OwnerId == owner);
    private int HousesAvailable => 32 - _state.Properties.Where(p => p.Houses < 5).Sum(p => p.Houses);
    private int HotelsAvailable => 12 - _state.Properties.Count(p => p.Houses == 5);

    private bool CanBuild(CrownDeedSavedProperty p) => p.OwnerId == ActingOwnerId() && p.Houses < 5
        && Spaces[p.SpaceIndex].Group != CrownDeedGroup.None && OwnsGroup(p.OwnerId.Value, Spaces[p.SpaceIndex].Group)
        && Group(Spaces[p.SpaceIndex].Group).All(g => !g.Mortgaged && g.Houses >= p.Houses)
        && Player(p.OwnerId.Value).Money >= Spaces[p.SpaceIndex].HouseCost
        && (p.Houses == 4 ? HotelsAvailable > 0 : HousesAvailable > 0);
    private bool CanSell(CrownDeedSavedProperty p) => p.OwnerId == ActingOwnerId() && p.Houses > 0
        && Group(Spaces[p.SpaceIndex].Group).All(g => g.Houses <= p.Houses)
        && (p.Houses != 5 || HousesAvailable >= 4);
    private bool CanMortgage(CrownDeedSavedProperty p) => p.OwnerId == ActingOwnerId() && !p.Mortgaged && p.Houses == 0
        && (Spaces[p.SpaceIndex].Group == CrownDeedGroup.None || Group(Spaces[p.SpaceIndex].Group).All(g => g.Houses == 0));
    private static int MortgageInterest(CrownDeedSavedProperty p) => (Spaces[p.SpaceIndex].Price + 19) / 20;
    private int UnmortgageCost(CrownDeedSavedProperty p) => Spaces[p.SpaceIndex].Price / 2 + MortgageInterest(p);

    private void Build(CrownDeedSavedProperty p)
    {
        _pendingDevelopment = (Snapshot, p.SpaceIndex, p.OwnerId!.Value);
        Player(p.OwnerId!.Value).Money -= Spaces[p.SpaceIndex].HouseCost;
        p.Houses++;
        _state.Status = p.Houses == 5 ? $"A grand hall now crowns {Spaces[p.SpaceIndex].Name}." : $"A shop opened on {Spaces[p.SpaceIndex].Name}.";
    }
    private void Sell(CrownDeedSavedProperty p)
    {
        p.Houses--;
        Player(p.OwnerId!.Value).Money += Spaces[p.SpaceIndex].HouseCost / 2;
        _state.Status = $"A building on {Spaces[p.SpaceIndex].Name} was sold back to the city treasury.";
    }
    private void Mortgage(CrownDeedSavedProperty p)
    {
        p.Mortgaged = true;
        Player(p.OwnerId!.Value).Money += Spaces[p.SpaceIndex].Price / 2;
        _state.Status = $"{Spaces[p.SpaceIndex].Name} mortgaged for {Spaces[p.SpaceIndex].Price / 2} crowns.";
    }
    private void SellGroup(CrownDeedSavedProperty selected)
    {
        var group = Group(Spaces[selected.SpaceIndex].Group).ToList();
        int proceeds = group.Sum(p => p.Houses * Spaces[p.SpaceIndex].HouseCost / 2);
        foreach (var property in group) property.Houses = 0;
        Player(selected.OwnerId!.Value).Money += proceeds;
        _state.Status = $"All buildings in {GroupName(Spaces[selected.SpaceIndex].Group)} sold for {proceeds} crowns.";
    }
    private void SelectProperty(int direction)
    {
        var owned = Owned(ActingOwnerId()).Select(p => p.SpaceIndex).ToList();
        int index = owned.IndexOf(_state.SelectedPropertyIndex ?? -1);
        _state.SelectedPropertyIndex = owned[(index + direction + owned.Count) % owned.Count];
    }

    private string AiAuctionAction(CrownDeedSavedPlayer actor)
    {
        var auction = _state.Auction!;
        var space = Spaces[auction.SpaceIndex];
        double premium = space.Group != CrownDeedGroup.None && Group(space.Group).Any(p => p.OwnerId == actor.Id) ? 1.5 : 1.05;
        int ceiling = Math.Min(actor.Money - 120, (int)(space.Price * premium));
        return auction.Bid + 10 <= ceiling ? "mp-bid-10" : "mp-pass";
    }
    private string AiManageOrEnd(CrownDeedSavedPlayer actor)
    {
        var build = Owned(actor.Id).FirstOrDefault(p => CanBuild(p) && actor.Money - Spaces[p.SpaceIndex].HouseCost >= 250);
        if (build is not null) { Build(build); return "mp-ai-internal"; }
        var lift = Owned(actor.Id).FirstOrDefault(p => p.Mortgaged && actor.Money - UnmortgageCost(p) >= 400);
        if (lift is not null)
        {
            actor.Money -= UnmortgageCost(lift);
            lift.Mortgaged = false;
            _state.Status = $"{actor.Name} lifted the mortgage on {Spaces[lift.SpaceIndex].Name}.";
            return "mp-ai-internal";
        }
        return "mp-end-turn";
    }
    private string AiLiquidate(CrownDeedSavedPlayer actor, DateTimeOffset now)
    {
        var building = Owned(actor.Id).FirstOrDefault(CanSell);
        if (building is not null) { Sell(building); ResolveDebtIfPaid(); return "mp-ai-internal"; }
        var hotel = Owned(actor.Id).FirstOrDefault(p => p.Houses == 5);
        if (hotel is not null) { SellGroup(hotel); ResolveDebtIfPaid(); return "mp-ai-internal"; }
        var mortgage = Owned(actor.Id).FirstOrDefault(CanMortgage);
        if (mortgage is not null) { Mortgage(mortgage); ResolveDebtIfPaid(); return "mp-ai-internal"; }
        return "mp-bankrupt";
    }

    private IReadOnlyList<string> AvailableActions()
    {
        var actions = new List<string>();
        if (_state.Phase is not CrownDeedPhase.ExitConfirmation and not CrownDeedPhase.Saving) actions.Add("mp-exit");
        switch (_state.Phase)
        {
            case CrownDeedPhase.Landing:
                actions.Add("mp-start-game");
                if (_resumeSave is not null) actions.Add("mp-resume");
                break;
            case CrownDeedPhase.Setup:
                if (_state.Humans > 0) actions.Add("mp-human-minus");
                if (_state.Ais > 0) actions.Add("mp-ai-minus");
                if (_state.Humans + _state.Ais < 6) { actions.Add("mp-human-plus"); actions.Add("mp-ai-plus"); }
                if (_state.Humans + _state.Ais >= 2) actions.Add("mp-start");
                for (int slot = 0; slot < _state.Humans + _state.Ais; slot++) actions.Add($"mp-piece-next-{slot + 1}");
                actions.Add("mp-setup-cancel");
                break;
            case CrownDeedPhase.ExitConfirmation:
                actions.AddRange(["mp-save-exit", "mp-exit-without-saving", "mp-exit-cancel"]);
                break;
            case CrownDeedPhase.GameOver: actions.Add("mp-new-game"); break;
            case CrownDeedPhase.Saving: break;
            default:
                var actor = ActingPlayer();
                if (actor is null || actor.IsAi) break;
                switch (_state.Phase)
                {
                    case CrownDeedPhase.AwaitingRoll:
                        actions.Add("mp-roll");
                        if (actor.InJail && actor.Money >= 50) actions.Add("mp-jail-pay");
                        if (actor.InJail && actor.GetOutOfJailCards > 0) actions.Add("mp-jail-card");
                        if (Owned(actor.Id).Any()) actions.Add("mp-manage");
                        break;
                    case CrownDeedPhase.AwaitingPurchase:
                        if (actor.Money >= Spaces[_state.PendingPropertyIndex!.Value].Price) actions.Add("mp-buy");
                        actions.Add("mp-auction");
                        break;
                    case CrownDeedPhase.Auction:
                        foreach (int bid in new[] { 10, 50, 100 })
                            if (actor.Money >= _state.Auction!.Bid + bid) actions.Add($"mp-bid-{bid}");
                        actions.Add("mp-pass");
                        break;
                    case CrownDeedPhase.AwaitingEndTurn:
                        actions.Add("mp-end-turn");
                        if (Owned(actor.Id).Any()) actions.Add("mp-manage");
                        break;
                    case CrownDeedPhase.Debt:
                        if (Owned(actor.Id).Any()) actions.Add("mp-manage");
                        if (LiquidationValue(actor) < _state.DebtAmount || !Owned(actor.Id).Any(p => p.Houses > 0 || CanMortgage(p)))
                            actions.Add("mp-bankrupt");
                        break;
                    case CrownDeedPhase.ManageProperties:
                        actions.Add("mp-manage-back");
                        var owned = Owned(actor.Id).ToList();
                        if (owned.Count > 1) actions.AddRange(["mp-property-previous", "mp-property-next"]);
                        var selected = owned.FirstOrDefault(p => p.SpaceIndex == _state.SelectedPropertyIndex);
                        if (selected is not null)
                        {
                            if (_state.ManageReturnPhase != CrownDeedPhase.Debt && CanBuild(selected)) actions.Add("mp-build");
                            if (CanSell(selected)) actions.Add("mp-sell");
                            if (selected.Houses == 5 && HousesAvailable < 4) actions.Add("mp-sell-group");
                            if (CanMortgage(selected)) actions.Add("mp-mortgage");
                            if (_state.ManageReturnPhase != CrownDeedPhase.Debt && selected.Mortgaged && actor.Money >= UnmortgageCost(selected))
                                actions.Add("mp-unmortgage");
                        }
                        break;
                }
                break;
        }
        return actions.AsReadOnly();
    }

    private int LiquidationValue(CrownDeedSavedPlayer player) => player.Money + Owned(player.Id)
        .Sum(p => p.Houses * Spaces[p.SpaceIndex].HouseCost / 2 + (p.Mortgaged ? 0 : Spaces[p.SpaceIndex].Price / 2));
    private int ActingOwnerId() => _state.DebtPlayerId ?? Active.Id;
    private CrownDeedSavedPlayer? ActingPlayer() => _state.Players.Count == 0 ? null
        : _state.Phase == CrownDeedPhase.Auction ? Player(_state.Auction!.CurrentBidderId)
        : Player(ActingOwnerId());
    private CrownDeedSavedPlayer Active => _state.Players[_state.ActivePlayerIndex];
    private CrownDeedSavedPlayer Player(int id) => _state.Players.First(p => p.Id == id);
    private CrownDeedSavedProperty PropertyAt(int index) => _state.Properties.First(p => p.SpaceIndex == index);
    private static bool IsPurchasable(CrownDeedSpace space) => space.Kind is CrownDeedSpaceKind.Property or CrownDeedSpaceKind.Railroad or CrownDeedSpaceKind.Utility;
    private bool ObserveTime(DateTimeOffset now)
    {
        if (_lastNow is { } last && now < last) return false;
        _lastNow = now;
        return true;
    }
    private void Changed() { Revision++; _snapshot = null; }
    private void PublishRoll(CrownDeedSnapshot? previous, DateTimeOffset now)
    {
        if (previous is null) return;
        long sequence = ++_rollSequence;
        RollOccurred?.Invoke(new(sequence, previous, Snapshot, now));
    }
    private void PublishDevelopment(DateTimeOffset now)
    {
        if (_pendingDevelopment is not { } development) return;
        _pendingDevelopment = null;
        long sequence = ++_developmentSequence;
        DevelopmentOccurred?.Invoke(new(sequence, development.SpaceIndex, development.PlayerId,
            development.Previous, Snapshot, now));
    }
    private CrownDeedSnapshot CreateSnapshot() => new(_state.Phase, _state.Humans, _state.Ais,
        Array.AsReadOnly(_state.Players.Select(p => new CrownDeedPlayerSnapshot(p.Id, p.Name, p.IsAi, p.Money,
            p.Position, p.InJail, p.JailTurns, p.GetOutOfJailCards, p.Bankrupt, p.ColorIndex)
            { PieceIndex = p.PieceIndex ?? p.ColorIndex }).ToArray()),
        Array.AsReadOnly(_state.Properties.Select(p => new CrownDeedPropertySnapshot(p.SpaceIndex, p.OwnerId, p.Houses, p.Mortgaged)).ToArray()),
        _state.ActivePlayerIndex, _state.Dice, _state.Status, _state.LastCard, _state.TurnNumber, Array.AsReadOnly(AvailableActions().ToArray()),
        _state.SelectedPropertyIndex, _state.PendingPropertyIndex,
        _state.Auction is { } a ? new(a.SpaceIndex, a.Bid, a.BidderId, a.CurrentBidderId, Array.AsReadOnly(a.PassedPlayerIds.ToArray())) : null,
        _state.WinnerId, Revision, _resumeSave is not null, _state.DebtAmount, _state.DebtPlayerId, _state.DebtCreditorId)
        { SetupPieces = Array.AsReadOnly(_setupPieces.Take(_state.Humans + _state.Ais).ToArray()) };

    private List<int> ShuffleDeck()
    {
        var cards = Enumerable.Range(0, 16).ToList();
        for (int i = cards.Count - 1; i > 0; i--) { int j = _random.Next(i + 1); (cards[i], cards[j]) = (cards[j], cards[i]); }
        return cards;
    }

    private static CrownDeedSaveData ParseSave(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 100_000) throw new FormatException("The Crown & Deed save is empty or too large.");
        CrownDeedSaveData s;
        try { s = JsonSerializer.Deserialize<CrownDeedSaveData>(json, SaveOptions) ?? throw new JsonException(); }
        catch (Exception e) when (e is JsonException or NotSupportedException) { throw new FormatException("The Crown & Deed save is not valid JSON.", e); }
        if (s.Version is not (1 or 2) || s.Players is null || s.Properties is null || s.Payments is null || s.PendingBankAuctions is null
            || s.ChanceDeck is null || s.ChestDeck is null || s.Status is null || s.LastCard is null)
            throw new FormatException("The Crown & Deed save version or fields are invalid.");
        if (s.Players.Any(p => p is null) || s.Properties.Any(p => p is null) || s.Payments.Any(p => p is null))
            throw new FormatException("The Crown & Deed save contains missing players, properties, or payments.");
        if (s.Version == 1) MigrateLegacySave(s);
        bool gamePhase = s.Phase is CrownDeedPhase.AwaitingRoll or CrownDeedPhase.AwaitingPurchase or CrownDeedPhase.Auction
            or CrownDeedPhase.ManageProperties or CrownDeedPhase.Debt or CrownDeedPhase.AwaitingEndTurn or CrownDeedPhase.GameOver;
        if (!gamePhase || s.Humans < 0 || s.Ais < 0 || s.Humans + s.Ais is < 2 or > 6 || s.Players.Count != s.Humans + s.Ais
            || s.Players.Count(p => !p.IsAi) != s.Humans || s.Players.Count(p => p.IsAi) != s.Ais
            || s.ActivePlayerIndex < 0 || s.ActivePlayerIndex >= s.Players.Count || s.TurnNumber < 1 || s.TurnNumber > 10_000_000
            || s.DoublesCount is < 0 or > 3 || s.Status.Length > 2000 || s.LastCard.Length > 2000)
            throw new FormatException("The Crown & Deed save contains invalid gameplay state.");
        var ids = s.Players.Select(p => p?.Id ?? 0).ToHashSet();
        if (ids.Count != s.Players.Count || ids.Any(id => id is < 1 or > 6) || s.Players.Any(p => p is null || p.Name is null
            || p.Name.Length is < 1 or > 40 || p.Money is < 0 or > 100_000_000 || p.Position is < 0 or > 39
            || p.ColorIndex is < 0 or > 5 || p.JailTurns is < 0 or > 2 || p.GetOutOfJailCards is < 0 or > 2
            || p.InJail && p.Position != CivicWatchIndex || p.Bankrupt && p.Money != 0))
            throw new FormatException("The Crown & Deed save contains invalid players.");
        var propertyIndices = Spaces.Where(IsPurchasable).Select(p => p.Index).ToHashSet();
        if (s.Properties.Count != propertyIndices.Count || !s.Properties.Select(p => p?.SpaceIndex ?? -1).ToHashSet().SetEquals(propertyIndices)
            || s.Properties.Any(p => p is null || p.OwnerId is { } id && (!ids.Contains(id) || s.Players.First(player => player.Id == id).Bankrupt)
                || p.Houses is < 0 or > 5 || p.Houses > 0 && (p.OwnerId is null || p.Mortgaged || Spaces[p.SpaceIndex].Kind != CrownDeedSpaceKind.Property)
                || p.Mortgaged && p.OwnerId is null))
            throw new FormatException("The Crown & Deed save contains invalid property ownership.");
        foreach (var group in s.Properties.Where(p => Spaces[p.SpaceIndex].Group != CrownDeedGroup.None).GroupBy(p => Spaces[p.SpaceIndex].Group))
        {
            if (group.Any(p => p.Houses > 0) && (group.Select(p => p.OwnerId).Distinct().Count() != 1
                || group.Any(p => p.Mortgaged) || group.Max(p => p.Houses) - group.Min(p => p.Houses) > 1))
                throw new FormatException("The Crown & Deed save contains invalid buildings.");
        }
        if (s.Properties.Where(p => p.Houses < 5).Sum(p => p.Houses) > 32 || s.Properties.Count(p => p.Houses == 5) > 12)
            throw new FormatException("The Crown & Deed save exceeds the city treasury's building supply.");
        if (s.ChanceDeck.Count != 16 || !s.ChanceDeck.ToHashSet().SetEquals(Enumerable.Range(0, 16))
            || s.ChestDeck.Count != 16 || !s.ChestDeck.ToHashSet().SetEquals(Enumerable.Range(0, 16))
            || s.ChanceIndex is < 0 or > 15 || s.ChestIndex is < 0 or > 15 || s.Dice.First is < 0 or > 6 || s.Dice.Second is < 0 or > 6
            || (s.Dice.First == 0) != (s.Dice.Second == 0)) throw new FormatException("The Crown & Deed save contains invalid cards or dice.");
        if (s.ChanceFreeCardHolderId is { } chanceHolder && (!ids.Contains(chanceHolder) || s.Players.First(p => p.Id == chanceHolder).Bankrupt)
            || s.ChestFreeCardHolderId is { } chestHolder && (!ids.Contains(chestHolder) || s.Players.First(p => p.Id == chestHolder).Bankrupt)
            || s.Players.Any(p => p.GetOutOfJailCards != (s.ChanceFreeCardHolderId == p.Id ? 1 : 0) + (s.ChestFreeCardHolderId == p.Id ? 1 : 0)))
            throw new FormatException("The Crown & Deed save contains invalid held Safe-Conduct Passes.");
        if (s.PendingPropertyIndex is { } pending && (!propertyIndices.Contains(pending) || s.Properties.First(p => p.SpaceIndex == pending).OwnerId is not null)
            || s.SelectedPropertyIndex is { } selected && !propertyIndices.Contains(selected)
            || s.Phase == CrownDeedPhase.AwaitingPurchase && (s.PendingPropertyIndex is null || s.PendingPropertyIndex != s.Players[s.ActivePlayerIndex].Position)
            || s.Phase is not (CrownDeedPhase.AwaitingPurchase or CrownDeedPhase.Auction) && s.PendingPropertyIndex is not null)
            throw new FormatException("The Crown & Deed save contains invalid property decisions.");
        if (s.Phase == CrownDeedPhase.Auction && (s.Auction is null || s.PendingPropertyIndex != s.Auction.SpaceIndex)
            || s.Phase != CrownDeedPhase.Auction && s.Auction is not null)
            throw new FormatException("The Crown & Deed save contains an invalid auction.");
        if (s.Auction is { } a && (a.PassedPlayerIds is null || a.Bid is < 0 or > 100_000_000 || !ids.Contains(a.CurrentBidderId)
            || s.Players.First(p => p.Id == a.CurrentBidderId).Bankrupt || a.PassedPlayerIds.Contains(a.CurrentBidderId)
            || a.PassedPlayerIds.Count != a.PassedPlayerIds.Distinct().Count() || a.PassedPlayerIds.Any(id => !ids.Contains(id))
            || a.BidderId is { } bidder && (!ids.Contains(bidder) || s.Players.First(p => p.Id == bidder).Bankrupt
                || s.Players.First(p => p.Id == bidder).Money < a.Bid || a.PassedPlayerIds.Contains(bidder))
            || (a.Bid == 0) != (a.BidderId is null))) throw new FormatException("The Crown & Deed save contains invalid bids.");
        bool inDebt = s.Phase == CrownDeedPhase.Debt || s.Phase == CrownDeedPhase.ManageProperties && s.ManageReturnPhase == CrownDeedPhase.Debt;
        if (s.DebtContinuation is not ("finish" or "jail-move") || s.DebtAmount is < 0 or > 100_000_000
            || s.DebtPlayerId is { } debtor && (!ids.Contains(debtor) || s.Players.First(p => p.Id == debtor).Bankrupt)
            || s.DebtCreditorId is { } creditor && !ids.Contains(creditor)
            || inDebt && (s.DebtAmount <= 0 || s.DebtPlayerId is null || s.Payments.Count == 0
                || s.Payments[0].PlayerId != s.DebtPlayerId || s.Payments[0].Amount != s.DebtAmount || s.Payments[0].CreditorId != s.DebtCreditorId)
            || inDebt && s.DebtPlayerId is { } owing && s.Players.First(p => p.Id == owing).Money >= s.DebtAmount
            || !inDebt && (s.DebtAmount != 0 || s.DebtPlayerId is not null || s.DebtCreditorId is not null || s.Payments.Count != 0)
            || s.Payments.Any(p => p is null || !ids.Contains(p.PlayerId) || p.Amount is < 1 or > 100_000_000
                || p.CreditorId is { } payee && (!ids.Contains(payee) || payee == p.PlayerId)))
            throw new FormatException("The Crown & Deed save contains invalid debt.");
        if (s.Phase == CrownDeedPhase.ManageProperties && (s.ManageReturnPhase is not (CrownDeedPhase.AwaitingRoll or CrownDeedPhase.AwaitingEndTurn or CrownDeedPhase.Debt)
            || s.SelectedPropertyIndex is null || s.Properties.First(p => p.SpaceIndex == s.SelectedPropertyIndex).OwnerId != (s.DebtPlayerId ?? s.Players[s.ActivePlayerIndex].Id)
            || s.Players.First(p => p.Id == (s.DebtPlayerId ?? s.Players[s.ActivePlayerIndex].Id)).IsAi))
            throw new FormatException("The Crown & Deed save contains invalid management controls.");
        if (s.PendingBankAuctions.Any(index => !propertyIndices.Contains(index) || s.Properties.First(p => p.SpaceIndex == index).OwnerId is not null)
            || s.PendingBankAuctions.Count != s.PendingBankAuctions.Distinct().Count()) throw new FormatException("The Crown & Deed save contains invalid bank auctions.");
        int alive = s.Players.Count(p => !p.Bankrupt);
        if (s.Phase == CrownDeedPhase.GameOver && (alive != 1 || s.WinnerId != s.Players.First(p => !p.Bankrupt).Id)
            || s.Phase != CrownDeedPhase.GameOver && (alive < 2 || s.WinnerId is not null)
            || s.Players[s.ActivePlayerIndex].Bankrupt && s.Phase is not (CrownDeedPhase.AwaitingEndTurn or CrownDeedPhase.Auction or CrownDeedPhase.GameOver or CrownDeedPhase.Debt or CrownDeedPhase.ManageProperties))
            throw new FormatException("The Crown & Deed save contains invalid turn or winner state.");
        ResolveSavedPieces(s);
        return s;
    }
}
