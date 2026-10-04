namespace ProjectTabletop.Interaction;

public sealed partial class MonopolyGame
{
    public const int CrownGateIndex = 0, CivicWatchIndex = 13;

    /// <summary>Original district identities; enum values remain stable for saved games.</summary>
    public static string GroupName(MonopolyGroup group) => group switch
    {
        MonopolyGroup.Brown => "Lantern Ward",
        MonopolyGroup.Orange => "Foundry District",
        MonopolyGroup.Yellow => "Tideglass Quays",
        MonopolyGroup.LightBlue => "Greenbough",
        MonopolyGroup.DarkBlue => "Crown Quarter",
        MonopolyGroup.Pink => "Songbird Heights",
        MonopolyGroup.Red => "Spice Market",
        MonopolyGroup.Green => "Moonrise",
        _ => "City estate"
    };

    public static IReadOnlyList<MonopolySpace> Spaces { get; } = Array.AsReadOnly(new[]
    {
        Special(0, "Crown Gate", MonopolySpaceKind.Go),
        Property(1, "Lantern Row", MonopolyGroup.Brown, 60, 50, 2, 10, 30, 90, 160, 250),
        Special(2, "Guild Ledger", MonopolySpaceKind.CommunityChest),
        Property(3, "Copper Lane", MonopolyGroup.Brown, 60, 50, 4, 20, 60, 180, 320, 450),
        Special(4, "Glassworks", MonopolySpaceKind.Utility, 150),
        Special(5, "North Ferry", MonopolySpaceKind.Railroad, 200),
        Property(6, "Foundry Walk", MonopolyGroup.Orange, 180, 100, 14, 70, 200, 550, 750, 950),
        Special(7, "City Charter", MonopolySpaceKind.Chance),
        Property(8, "Forge Lane", MonopolyGroup.Orange, 180, 100, 14, 70, 200, 550, 750, 950),
        Property(9, "Kiln Terrace", MonopolyGroup.Orange, 200, 100, 16, 80, 220, 600, 800, 1000),
        Special(10, "Market Levy", MonopolySpaceKind.Tax, 200),
        Property(11, "Tideglass Quay", MonopolyGroup.Yellow, 260, 150, 22, 110, 330, 800, 975, 1150),
        Property(12, "Sailmaker Reach", MonopolyGroup.Yellow, 260, 150, 22, 110, 330, 800, 975, 1150),
        Special(13, "Civic Watch", MonopolySpaceKind.Jail),
        Property(14, "Beacon Esplanade", MonopolyGroup.Yellow, 280, 150, 24, 120, 360, 850, 1025, 1200),
        Special(15, "Guild Ledger", MonopolySpaceKind.CommunityChest),
        Special(16, "East Tram", MonopolySpaceKind.Railroad, 200),
        Property(17, "Juniper Close", MonopolyGroup.LightBlue, 100, 50, 6, 30, 90, 270, 400, 550),
        Property(18, "Rowan Crescent", MonopolyGroup.LightBlue, 100, 50, 6, 30, 90, 270, 400, 550),
        Special(19, "City Charter", MonopolySpaceKind.Chance),
        Property(20, "Willow Court", MonopolyGroup.LightBlue, 120, 50, 8, 40, 100, 300, 450, 600),
        Property(21, "Amber Exchange", MonopolyGroup.DarkBlue, 350, 200, 35, 175, 500, 1100, 1300, 1500),
        Property(22, "Royal Arcade", MonopolyGroup.DarkBlue, 400, 200, 50, 200, 600, 1400, 1700, 2000),
        Special(23, "Lantern Court", MonopolySpaceKind.FreeParking),
        Special(24, "Festival Dues", MonopolySpaceKind.Tax, 100),
        Special(25, "South Ferry", MonopolySpaceKind.Railroad, 200),
        Property(26, "Starling Gardens", MonopolyGroup.Pink, 140, 100, 10, 50, 150, 450, 625, 750),
        Special(27, "Signal Tower", MonopolySpaceKind.Utility, 150),
        Property(28, "Wren Mews", MonopolyGroup.Pink, 140, 100, 10, 50, 150, 450, 625, 750),
        Property(29, "Kestrel Heights", MonopolyGroup.Pink, 160, 100, 12, 60, 180, 500, 700, 900),
        Special(30, "Guild Ledger", MonopolySpaceKind.CommunityChest),
        Property(31, "Pilgrim Market", MonopolyGroup.Red, 220, 150, 18, 90, 250, 700, 875, 1050),
        Property(32, "Saffron Walk", MonopolyGroup.Red, 220, 150, 18, 90, 250, 700, 875, 1050),
        Special(33, "Civic Review", MonopolySpaceKind.GoToJail),
        Property(34, "Suncrest Avenue", MonopolyGroup.Red, 240, 150, 20, 100, 300, 750, 925, 1100),
        Special(35, "West Tram", MonopolySpaceKind.Railroad, 200),
        Special(36, "City Charter", MonopolySpaceKind.Chance),
        Property(37, "Moonstone Bay", MonopolyGroup.Green, 300, 200, 26, 130, 390, 900, 1100, 1275),
        Property(38, "Silverlight Rise", MonopolyGroup.Green, 300, 200, 26, 130, 390, 900, 1100, 1275),
        Property(39, "Starlit Promenade", MonopolyGroup.Green, 320, 200, 28, 150, 450, 1000, 1200, 1400)
    });

    private static MonopolySpace Special(int index, string name, MonopolySpaceKind kind, int price = 0) =>
        new(index, name, kind, MonopolyGroup.None, price, 0, Array.Empty<int>());
    private static MonopolySpace Property(int index, string name, MonopolyGroup group, int price, int houseCost, params int[] rents) =>
        new(index, name, MonopolySpaceKind.Property, group, price, houseCost, Array.AsReadOnly(rents));
}
