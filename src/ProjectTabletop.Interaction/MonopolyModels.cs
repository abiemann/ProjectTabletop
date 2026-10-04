using System.Text.Json.Serialization;

namespace ProjectTabletop.Interaction;

public enum MonopolyPhase
{
    Landing, Setup, AwaitingRoll, AwaitingPurchase, Auction, ManageProperties, Debt,
    AwaitingEndTurn, GameOver, ExitConfirmation, Saving
}
public enum MonopolySpaceKind { Go, Property, Chance, CommunityChest, Tax, Railroad, Utility, Jail, FreeParking, GoToJail }
public enum MonopolyGroup { None, Brown, LightBlue, Pink, Orange, Red, Yellow, Green, DarkBlue }
public sealed record MonopolySpace(int Index, string Name, MonopolySpaceKind Kind, MonopolyGroup Group,
    int Price, int HouseCost, IReadOnlyList<int> Rents);
public readonly record struct MonopolyDice(int First, int Second)
{
    public int Total => First + Second;
    public bool IsDouble => First == Second;
}
public sealed record MonopolyPlayerSnapshot(int Id, string Name, bool IsAi, int Money, int Position,
    bool InJail, int JailTurns, int GetOutOfJailCards, bool Bankrupt, int ColorIndex);
public sealed record MonopolyPropertySnapshot(int SpaceIndex, int? OwnerId, int Houses, bool Mortgaged);
public sealed record MonopolyAuctionSnapshot(int SpaceIndex, int Bid, int? BidderId, int CurrentBidderId,
    IReadOnlyList<int> PassedPlayerIds);
public sealed record MonopolySnapshot(MonopolyPhase Phase, int HumanPlayers, int AiPlayers,
    IReadOnlyList<MonopolyPlayerSnapshot> Players, IReadOnlyList<MonopolyPropertySnapshot> Properties,
    int ActivePlayerIndex, MonopolyDice Dice, string Status, string LastCard, int TurnNumber,
    IReadOnlyList<string> AvailableActions, int? SelectedPropertyIndex, int? PendingPropertyIndex,
    MonopolyAuctionSnapshot? Auction, int? WinnerId, long Revision, bool CanResume,
    int DebtAmount, int? DebtPlayerId, int? DebtCreditorId)
{
    public MonopolyPlayerSnapshot? ActivePlayer => ActivePlayerIndex >= 0 && ActivePlayerIndex < Players.Count
        ? Players[ActivePlayerIndex] : null;
    public bool IsActiveGame => Players.Count > 0 && Phase != MonopolyPhase.GameOver;
}

/// <summary>Versioned save payload. Property ownership refers to stable player IDs.</summary>
public sealed class MonopolySaveData
{
    [JsonRequired]
    public int Version { get; set; } = 2;
    public MonopolyPhase Phase { get; set; }
    public int Humans { get; set; } = 1;
    public int Ais { get; set; } = 1;
    public List<MonopolySavedPlayer> Players { get; set; } = [];
    public List<MonopolySavedProperty> Properties { get; set; } = [];
    public int ActivePlayerIndex { get; set; }
    public MonopolyDice Dice { get; set; }
    public string Status { get; set; } = "";
    public string LastCard { get; set; } = "";
    public int TurnNumber { get; set; }
    public int DoublesCount { get; set; }
    public bool ExtraRoll { get; set; }
    public int? PendingPropertyIndex { get; set; }
    public int? SelectedPropertyIndex { get; set; }
    public MonopolyPhase ManageReturnPhase { get; set; }
    public int DebtAmount { get; set; }
    public int? DebtPlayerId { get; set; }
    public int? DebtCreditorId { get; set; }
    public string DebtContinuation { get; set; } = "finish";
    public List<MonopolySavedPayment> Payments { get; set; } = [];
    public List<int> PendingBankAuctions { get; set; } = [];
    public MonopolySavedAuction? Auction { get; set; }
    public List<int> ChanceDeck { get; set; } = [];
    public List<int> ChestDeck { get; set; } = [];
    public int ChanceIndex { get; set; }
    public int ChestIndex { get; set; }
    public int? ChanceFreeCardHolderId { get; set; }
    public int? ChestFreeCardHolderId { get; set; }
    public int? WinnerId { get; set; }
}
public sealed class MonopolySavedPlayer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsAi { get; set; }
    public int Money { get; set; }
    public int Position { get; set; }
    public bool InJail { get; set; }
    public int JailTurns { get; set; }
    public int GetOutOfJailCards { get; set; }
    public bool Bankrupt { get; set; }
    public int ColorIndex { get; set; }
}
public sealed class MonopolySavedProperty
{
    public int SpaceIndex { get; set; }
    public int? OwnerId { get; set; }
    public int Houses { get; set; }
    public bool Mortgaged { get; set; }
}
public sealed class MonopolySavedPayment
{
    public int PlayerId { get; set; }
    public int? CreditorId { get; set; }
    public int Amount { get; set; }
}
public sealed class MonopolySavedAuction
{
    public int SpaceIndex { get; set; }
    public int Bid { get; set; }
    public int? BidderId { get; set; }
    public int CurrentBidderId { get; set; }
    public List<int> PassedPlayerIds { get; set; } = [];
}
