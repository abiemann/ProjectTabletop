namespace ProjectTabletop.Interaction;

public sealed partial class CrownDeedGame
{
    public const int CrownGateIndex = 0, CivicWatchIndex = 13;

    /// <summary>Original district identities; enum values remain stable for saved games.</summary>
    public static string GroupName(CrownDeedGroup group) => group switch
    {
        CrownDeedGroup.Brown => "Lantern Ward",
        CrownDeedGroup.Orange => "Foundry District",
        CrownDeedGroup.Yellow => "Tideglass Quays",
        CrownDeedGroup.LightBlue => "Greenbough",
        CrownDeedGroup.DarkBlue => "Crown Quarter",
        CrownDeedGroup.Pink => "Songbird Heights",
        CrownDeedGroup.Red => "Spice Market",
        CrownDeedGroup.Green => "Moonrise",
        _ => "City estate"
    };

    public static IReadOnlyList<CrownDeedSpace> Spaces { get; } = Array.AsReadOnly(new[]
    {
        Special(0, "Crown Gate", CrownDeedSpaceKind.Go),
        Property(1, "Lantern Row", CrownDeedGroup.Brown, 60, 50, 2, 10, 30, 90, 160, 250),
        Special(2, "Guild Ledger", CrownDeedSpaceKind.CommunityChest),
        Property(3, "Copper Lane", CrownDeedGroup.Brown, 60, 50, 4, 20, 60, 180, 320, 450),
        Special(4, "Glassworks", CrownDeedSpaceKind.Utility, 150),
        Special(5, "North Ferry", CrownDeedSpaceKind.Railroad, 200),
        Property(6, "Foundry Walk", CrownDeedGroup.Orange, 180, 100, 14, 70, 200, 550, 750, 950),
        Special(7, "City Charter", CrownDeedSpaceKind.Chance),
        Property(8, "Forge Lane", CrownDeedGroup.Orange, 180, 100, 14, 70, 200, 550, 750, 950),
        Property(9, "Kiln Terrace", CrownDeedGroup.Orange, 200, 100, 16, 80, 220, 600, 800, 1000),
        Special(10, "Market Levy", CrownDeedSpaceKind.Tax, 200),
        Property(11, "Tideglass Quay", CrownDeedGroup.Yellow, 260, 150, 22, 110, 330, 800, 975, 1150),
        Property(12, "Sailmaker Reach", CrownDeedGroup.Yellow, 260, 150, 22, 110, 330, 800, 975, 1150),
        Special(13, "Civic Watch", CrownDeedSpaceKind.Jail),
        Property(14, "Beacon Esplanade", CrownDeedGroup.Yellow, 280, 150, 24, 120, 360, 850, 1025, 1200),
        Special(15, "Guild Ledger", CrownDeedSpaceKind.CommunityChest),
        Special(16, "East Tram", CrownDeedSpaceKind.Railroad, 200),
        Property(17, "Juniper Close", CrownDeedGroup.LightBlue, 100, 50, 6, 30, 90, 270, 400, 550),
        Property(18, "Rowan Crescent", CrownDeedGroup.LightBlue, 100, 50, 6, 30, 90, 270, 400, 550),
        Special(19, "City Charter", CrownDeedSpaceKind.Chance),
        Property(20, "Willow Court", CrownDeedGroup.LightBlue, 120, 50, 8, 40, 100, 300, 450, 600),
        Property(21, "Amber Exchange", CrownDeedGroup.DarkBlue, 350, 200, 35, 175, 500, 1100, 1300, 1500),
        Property(22, "Royal Arcade", CrownDeedGroup.DarkBlue, 400, 200, 50, 200, 600, 1400, 1700, 2000),
        Special(23, "Lantern Court", CrownDeedSpaceKind.FreeParking),
        Special(24, "Festival Dues", CrownDeedSpaceKind.Tax, 100),
        Special(25, "South Ferry", CrownDeedSpaceKind.Railroad, 200),
        Property(26, "Starling Gardens", CrownDeedGroup.Pink, 140, 100, 10, 50, 150, 450, 625, 750),
        Special(27, "Signal Tower", CrownDeedSpaceKind.Utility, 150),
        Property(28, "Wren Mews", CrownDeedGroup.Pink, 140, 100, 10, 50, 150, 450, 625, 750),
        Property(29, "Kestrel Heights", CrownDeedGroup.Pink, 160, 100, 12, 60, 180, 500, 700, 900),
        Special(30, "Guild Ledger", CrownDeedSpaceKind.CommunityChest),
        Property(31, "Pilgrim Market", CrownDeedGroup.Red, 220, 150, 18, 90, 250, 700, 875, 1050),
        Property(32, "Saffron Walk", CrownDeedGroup.Red, 220, 150, 18, 90, 250, 700, 875, 1050),
        Special(33, "Civic Review", CrownDeedSpaceKind.GoToJail),
        Property(34, "Suncrest Avenue", CrownDeedGroup.Red, 240, 150, 20, 100, 300, 750, 925, 1100),
        Special(35, "West Tram", CrownDeedSpaceKind.Railroad, 200),
        Special(36, "City Charter", CrownDeedSpaceKind.Chance),
        Property(37, "Moonstone Bay", CrownDeedGroup.Green, 300, 200, 26, 130, 390, 900, 1100, 1275),
        Property(38, "Silverlight Rise", CrownDeedGroup.Green, 300, 200, 26, 130, 390, 900, 1100, 1275),
        Property(39, "Starlit Promenade", CrownDeedGroup.Green, 320, 200, 28, 150, 450, 1000, 1200, 1400)
    });

    private static CrownDeedSpace Special(int index, string name, CrownDeedSpaceKind kind, int price = 0) =>
        new(index, name, kind, CrownDeedGroup.None, price, 0, Array.Empty<int>());
    private static CrownDeedSpace Property(int index, string name, CrownDeedGroup group, int price, int houseCost, params int[] rents) =>
        new(index, name, CrownDeedSpaceKind.Property, group, price, houseCost, Array.AsReadOnly(rents));
}
