namespace ProjectTabletop.Interaction;

public enum SlotSymbol
{
    Ten, Jack, Queen, King, Ace, Dagger, Goblet, Chest, Crown, Wild,
    /// <summary>A fire orb worth a multiple of the bet, or a jackpot, during Dragonfire Respins.</summary>
    Coin,
    /// <summary>Power eggs: green expands the respin grid, red collects, blue boosts, rainbow does all three.</summary>
    EggGreen, EggRed, EggBlue, EggRainbow,
    /// <summary>Three on reels 1, 3 and 5 start free spins; each later one adds a spin.</summary>
    Elixir,
    /// <summary>Lights the key under its reel; five lit keys open the Treasure Vault.</summary>
    Key,
    Empty
}

public enum SlotJackpot { None, Mini, Minor, Major, Grand }

public enum SlotPhase
{
    Idle, Spinning, LineWins,
    RespinIntro, Respinning, RespinEffect, RespinOutro,
    FreeSpinsIntro, FreeSpinsOutro,
    VaultIntro, VaultPicking, VaultOutro
}

public enum SlotEffect { None, Expand, Boost, Collect }

/// <summary>The first successful activation of a dragon's power in this spin.</summary>
public sealed record SlotDragonHatch(SlotEffect Power, DateTimeOffset HatchedAt);

/// <summary>Forces the next spin to land a feature, for demonstrations and verification.</summary>
public enum SlotDemo { None, Respins, FreeSpins, Vault }

public readonly record struct SlotPosition(int Reel, int Row);

/// <param name="Value">Credits carried by a coin or egg; zero for ordinary symbols.</param>
public sealed record SlotCell(SlotSymbol Symbol, decimal Value = 0, SlotJackpot Jackpot = SlotJackpot.None)
{
    public static readonly SlotCell Empty = new(SlotSymbol.Empty);
    public bool IsEgg => Symbol is SlotSymbol.EggGreen or SlotSymbol.EggRed or SlotSymbol.EggBlue or SlotSymbol.EggRainbow;
    /// <summary>Coins and eggs: the symbols that stay in place during respins.</summary>
    public bool IsBonus => Symbol == SlotSymbol.Coin || IsEgg;
}

/// <param name="Line">Zero-based payline index.</param>
/// <param name="Cells">Winning positions in the five-row grid.</param>
public sealed record SlotLineWin(int Line, SlotSymbol Symbol, int Count, decimal Amount, IReadOnlyList<SlotPosition> Cells);

/// <summary>An opened Treasure Vault chest.</summary>
public sealed record SlotVaultChest(int Index, SlotJackpot Gem);

/// <summary>
/// One immutable view of the machine. The grid is five reels by five rows, reel-major
/// (index reel * 5 + row); only rows FirstRow..FirstRow+RowCount-1 are in play.
/// </summary>
public sealed record SlotSnapshot(
    long Revision, SlotPhase Phase, DateTimeOffset PhaseStartedAt, TimeSpan PhaseDuration,
    decimal Balance, decimal Bet, IReadOnlyList<decimal> BetOptions,
    IReadOnlyList<SlotCell> Grid, IReadOnlyList<SlotCell> PreviousGrid, int FirstRow, int RowCount,
    long SpinNumber, bool FreeSpin, IReadOnlyList<SlotLineWin> LineWins, decimal RoundWin,
    bool InRespins, int RespinsLeft, IReadOnlyList<SlotPosition> FreshCells,
    SlotEffect Effect, SlotPosition? EffectCell, int BoostMultiplier, decimal EffectAmount,
    decimal RespinTotal, bool GrandFill,
    IReadOnlyList<bool> Keys, bool InFreeSpins, int FreeSpinsRemaining, int FreeSpinsPlayed,
    int ElixirLevel, decimal FreeSpinsWin,
    IReadOnlyList<SlotVaultChest?> VaultChests, int VaultNewest, SlotJackpot VaultAward,
    IReadOnlyDictionary<SlotJackpot, decimal> Jackpots, decimal BuyCost,
    string Status, string Banner, IReadOnlyList<string> AvailableActions)
{
    /// <summary>The injected start time of this spin, retained through its later phases; MinValue before the first spin.</summary>
    public DateTimeOffset SpinStartedAt { get; init; }

    /// <summary>Dragons revealed by successfully applied powers, retained until the next spin starts.</summary>
    public IReadOnlyList<SlotDragonHatch> DragonHatches { get; init; } = [];

    public SlotCell Cell(int reel, int row) => Grid[reel * SlotGame.Rows + row];
    public bool IsActiveRow(int row) => row >= FirstRow && row < FirstRow + RowCount;
}
