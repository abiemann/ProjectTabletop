using System.Text.Json.Serialization;

namespace ProjectTabletop.Interaction;

public enum CrownDeedPhase
{
    Landing, Setup, AwaitingRoll, AwaitingPurchase, Auction, ManageProperties, Debt,
    AwaitingEndTurn, GameOver, ExitConfirmation, Saving
}
public enum CrownDeedSpaceKind { Go, Property, Chance, CommunityChest, Tax, Railroad, Utility, Jail, FreeParking, GoToJail }
public enum CrownDeedGroup { None, Brown, LightBlue, Pink, Orange, Red, Yellow, Green, DarkBlue }
public sealed record CrownDeedSpace(int Index, string Name, CrownDeedSpaceKind Kind, CrownDeedGroup Group,
    int Price, int HouseCost, IReadOnlyList<int> Rents);
public readonly record struct CrownDeedDice(int First, int Second)
{
    public int Total => First + Second;
    public bool IsDouble => First == Second;
}
public sealed record CrownDeedPlayerSnapshot(int Id, string Name, bool IsAi, int Money, int Position,
    bool InJail, int JailTurns, int GetOutOfJailCards, bool Bankrupt, int ColorIndex)
{
    /// <summary>Silver piece design; player and ownership colours remain independent.</summary>
    public int PieceIndex { get; init; } = ColorIndex;
}
public sealed record CrownDeedPropertySnapshot(int SpaceIndex, int? OwnerId, int Houses, bool Mortgaged);
public sealed record CrownDeedAuctionSnapshot(int SpaceIndex, int Bid, int? BidderId, int CurrentBidderId,
    IReadOnlyList<int> PassedPlayerIds);
public sealed record CrownDeedSnapshot(CrownDeedPhase Phase, int HumanPlayers, int AiPlayers,
    IReadOnlyList<CrownDeedPlayerSnapshot> Players, IReadOnlyList<CrownDeedPropertySnapshot> Properties,
    int ActivePlayerIndex, CrownDeedDice Dice, string Status, string LastCard, int TurnNumber,
    IReadOnlyList<string> AvailableActions, int? SelectedPropertyIndex, int? PendingPropertyIndex,
    CrownDeedAuctionSnapshot? Auction, int? WinnerId, long Revision, bool CanResume,
    int DebtAmount, int? DebtPlayerId, int? DebtCreditorId)
{
    /// <summary>Independent, immutable piece choices for the active setup slots, humans then AI.</summary>
    public IReadOnlyList<int> SetupPieces { get; init; } = [];
    public CrownDeedPlayerSnapshot? ActivePlayer => ActivePlayerIndex >= 0 && ActivePlayerIndex < Players.Count
        ? Players[ActivePlayerIndex] : null;
    public bool IsActiveGame => Players.Count > 0 && Phase != CrownDeedPhase.GameOver;
}

/// <summary>Versioned save payload. Property ownership refers to stable player IDs.</summary>
public sealed class CrownDeedSaveData
{
    [JsonRequired]
    public int Version { get; set; } = 2;
    public CrownDeedPhase Phase { get; set; }
    public int Humans { get; set; } = 1;
    public int Ais { get; set; } = 1;
    public List<CrownDeedSavedPlayer> Players { get; set; } = [];
    public List<CrownDeedSavedProperty> Properties { get; set; } = [];
    public int ActivePlayerIndex { get; set; }
    public CrownDeedDice Dice { get; set; }
    public string Status { get; set; } = "";
    public string LastCard { get; set; } = "";
    public int TurnNumber { get; set; }
    public int DoublesCount { get; set; }
    public bool ExtraRoll { get; set; }
    public int? PendingPropertyIndex { get; set; }
    public int? SelectedPropertyIndex { get; set; }
    public CrownDeedPhase ManageReturnPhase { get; set; }
    public int DebtAmount { get; set; }
    public int? DebtPlayerId { get; set; }
    public int? DebtCreditorId { get; set; }
    public string DebtContinuation { get; set; } = "finish";
    public List<CrownDeedSavedPayment> Payments { get; set; } = [];
    public List<int> PendingBankAuctions { get; set; } = [];
    public CrownDeedSavedAuction? Auction { get; set; }
    public List<int> ChanceDeck { get; set; } = [];
    public List<int> ChestDeck { get; set; } = [];
    public int ChanceIndex { get; set; }
    public int ChestIndex { get; set; }
    public int? ChanceFreeCardHolderId { get; set; }
    public int? ChestFreeCardHolderId { get; set; }
    public int? WinnerId { get; set; }
}
public sealed class CrownDeedSavedPlayer
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
    /// <summary>Absent in earlier saves; resolved deterministically from the player's colour.</summary>
    public int? PieceIndex { get; set; }
}
public sealed class CrownDeedSavedProperty
{
    public int SpaceIndex { get; set; }
    public int? OwnerId { get; set; }
    public int Houses { get; set; }
    public bool Mortgaged { get; set; }
}
public sealed class CrownDeedSavedPayment
{
    public int PlayerId { get; set; }
    public int? CreditorId { get; set; }
    public int Amount { get; set; }
}
public sealed class CrownDeedSavedAuction
{
    public int SpaceIndex { get; set; }
    public int Bid { get; set; }
    public int? BidderId { get; set; }
    public int CurrentBidderId { get; set; }
    public List<int> PassedPlayerIds { get; set; } = [];
}
