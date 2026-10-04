namespace ProjectTabletop.Interaction;

public sealed partial class MonopolyGame
{
    // Version 1 followed the former square itinerary. This complete bijection
    // retains each estate's economics, district, ownership and development.
    private static readonly int[] LegacySpaceMap =
    [
        0, 1, 2, 3, 10, 5, 17, 7, 18, 20, 13, 26, 4, 28, 29, 16, 6, 15, 8, 9,
        23, 31, 19, 32, 34, 25, 11, 12, 27, 14, 33, 37, 38, 30, 39, 35, 36, 21, 24, 22
    ];

    private static void MigrateLegacySave(MonopolySaveData save)
    {
        static int Map(int index) => index is >= 0 and < 40 ? LegacySpaceMap[index]
            : throw new FormatException("The legacy city save contains an invalid stop.");
        foreach (var player in save.Players) player.Position = Map(player.Position);
        foreach (var property in save.Properties) property.SpaceIndex = Map(property.SpaceIndex);
        if (save.PendingPropertyIndex is { } pending) save.PendingPropertyIndex = Map(pending);
        if (save.SelectedPropertyIndex is { } selected) save.SelectedPropertyIndex = Map(selected);
        if (save.Auction is { } auction) auction.SpaceIndex = Map(auction.SpaceIndex);
        save.PendingBankAuctions = save.PendingBankAuctions.Select(Map).ToList();
        // Card IDs retain their resolved effects, deck order/cursors and held
        // passes. Fixed-destination cards now name the corresponding city estate.
        // Old display prose is not an instruction and must never be replayed.
        save.Status = "Your company has resumed on the Crown & Deed boulevard.";
        if (!string.IsNullOrEmpty(save.LastCard))
            save.LastCard = "A notice from the previous itinerary was already resolved.";
        save.Version = 2;
    }
}
