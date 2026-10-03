namespace ProjectTabletop.Interaction;

public enum RoulettePhase { Betting, Spinning }
public enum RouletteBetKind { Straight, Red, Black, Even, Odd, Low, High, Dozen, Column }

/// <param name="Number">0..36 for a straight bet; 1..3 for a dozen/column; otherwise zero.</param>
public readonly record struct RouletteBetTarget(RouletteBetKind Kind, int Number = 0)
{
    public string Id => Kind switch
    {
        RouletteBetKind.Straight => $"roulette-number-{Number}",
        RouletteBetKind.Dozen => $"roulette-dozen-{Number}",
        RouletteBetKind.Column => $"roulette-column-{Number}",
        _ => "roulette-" + Kind.ToString().ToLowerInvariant()
    };
    public string Label => Kind switch
    {
        RouletteBetKind.Straight => Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        RouletteBetKind.Dozen => Number switch { 1 => "1st 12", 2 => "2nd 12", _ => "3rd 12" },
        RouletteBetKind.Column => "2 : 1",
        RouletteBetKind.Low => "1 – 18",
        RouletteBetKind.High => "19 – 36",
        _ => Kind.ToString()
    };
}

public sealed record RouletteBet(RouletteBetTarget Target, decimal Amount)
{
    public string Id => Target.Id;
}
/// <summary>TotalReturn includes the returned stake; Profit subtracts every wager in this round.</summary>
public sealed record RouletteResult(long RoundNumber, int Number, int PocketIndex,
    decimal TotalBet, decimal TotalReturn, decimal Profit);

/// <summary>An immutable view of a virtual-credit, European single-zero roulette table. History is newest first.</summary>
public sealed record RouletteSnapshot(long Revision, RoulettePhase Phase, DateTimeOffset RoundStartedAt,
    TimeSpan SpinDuration, long RoundNumber, int? Outcome, int? PocketIndex,
    decimal Balance, decimal Chip, IReadOnlyList<decimal> ChipOptions, decimal TotalBet,
    IReadOnlyList<RouletteBet> Bets, IReadOnlyList<RouletteBet> LastBets,
    decimal LastTotalBet, decimal LastWin, decimal LastProfit, RouletteBetTarget? SelectedBet,
    IReadOnlyList<RouletteResult> History, string Status, IReadOnlyList<string> AvailableActions);
