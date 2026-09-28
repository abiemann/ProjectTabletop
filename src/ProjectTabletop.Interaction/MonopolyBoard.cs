namespace ProjectTabletop.Interaction;

public sealed partial class MonopolyGame
{
    public static IReadOnlyList<MonopolySpace> Spaces { get; } = Array.AsReadOnly(new[]
    {
        Special(0, "GO", MonopolySpaceKind.Go),
        Property(1, "Mediterranean Avenue", MonopolyGroup.Brown, 60, 50, 2, 10, 30, 90, 160, 250),
        Special(2, "Community Chest", MonopolySpaceKind.CommunityChest),
        Property(3, "Baltic Avenue", MonopolyGroup.Brown, 60, 50, 4, 20, 60, 180, 320, 450),
        Special(4, "Income Tax", MonopolySpaceKind.Tax, 200),
        Special(5, "Reading Railroad", MonopolySpaceKind.Railroad, 200),
        Property(6, "Oriental Avenue", MonopolyGroup.LightBlue, 100, 50, 6, 30, 90, 270, 400, 550),
        Special(7, "Chance", MonopolySpaceKind.Chance),
        Property(8, "Vermont Avenue", MonopolyGroup.LightBlue, 100, 50, 6, 30, 90, 270, 400, 550),
        Property(9, "Connecticut Avenue", MonopolyGroup.LightBlue, 120, 50, 8, 40, 100, 300, 450, 600),
        Special(10, "Jail / Just Visiting", MonopolySpaceKind.Jail),
        Property(11, "St. Charles Place", MonopolyGroup.Pink, 140, 100, 10, 50, 150, 450, 625, 750),
        Special(12, "Electric Company", MonopolySpaceKind.Utility, 150),
        Property(13, "States Avenue", MonopolyGroup.Pink, 140, 100, 10, 50, 150, 450, 625, 750),
        Property(14, "Virginia Avenue", MonopolyGroup.Pink, 160, 100, 12, 60, 180, 500, 700, 900),
        Special(15, "Pennsylvania Railroad", MonopolySpaceKind.Railroad, 200),
        Property(16, "St. James Place", MonopolyGroup.Orange, 180, 100, 14, 70, 200, 550, 750, 950),
        Special(17, "Community Chest", MonopolySpaceKind.CommunityChest),
        Property(18, "Tennessee Avenue", MonopolyGroup.Orange, 180, 100, 14, 70, 200, 550, 750, 950),
        Property(19, "New York Avenue", MonopolyGroup.Orange, 200, 100, 16, 80, 220, 600, 800, 1000),
        Special(20, "Free Parking", MonopolySpaceKind.FreeParking),
        Property(21, "Kentucky Avenue", MonopolyGroup.Red, 220, 150, 18, 90, 250, 700, 875, 1050),
        Special(22, "Chance", MonopolySpaceKind.Chance),
        Property(23, "Indiana Avenue", MonopolyGroup.Red, 220, 150, 18, 90, 250, 700, 875, 1050),
        Property(24, "Illinois Avenue", MonopolyGroup.Red, 240, 150, 20, 100, 300, 750, 925, 1100),
        Special(25, "B. & O. Railroad", MonopolySpaceKind.Railroad, 200),
        Property(26, "Atlantic Avenue", MonopolyGroup.Yellow, 260, 150, 22, 110, 330, 800, 975, 1150),
        Property(27, "Ventnor Avenue", MonopolyGroup.Yellow, 260, 150, 22, 110, 330, 800, 975, 1150),
        Special(28, "Water Works", MonopolySpaceKind.Utility, 150),
        Property(29, "Marvin Gardens", MonopolyGroup.Yellow, 280, 150, 24, 120, 360, 850, 1025, 1200),
        Special(30, "Go to Jail", MonopolySpaceKind.GoToJail),
        Property(31, "Pacific Avenue", MonopolyGroup.Green, 300, 200, 26, 130, 390, 900, 1100, 1275),
        Property(32, "North Carolina Avenue", MonopolyGroup.Green, 300, 200, 26, 130, 390, 900, 1100, 1275),
        Special(33, "Community Chest", MonopolySpaceKind.CommunityChest),
        Property(34, "Pennsylvania Avenue", MonopolyGroup.Green, 320, 200, 28, 150, 450, 1000, 1200, 1400),
        Special(35, "Short Line", MonopolySpaceKind.Railroad, 200),
        Special(36, "Chance", MonopolySpaceKind.Chance),
        Property(37, "Park Place", MonopolyGroup.DarkBlue, 350, 200, 35, 175, 500, 1100, 1300, 1500),
        Special(38, "Luxury Tax", MonopolySpaceKind.Tax, 100),
        Property(39, "Boardwalk", MonopolyGroup.DarkBlue, 400, 200, 50, 200, 600, 1400, 1700, 2000)
    });

    private static MonopolySpace Special(int index, string name, MonopolySpaceKind kind, int price = 0) =>
        new(index, name, kind, MonopolyGroup.None, price, 0, Array.Empty<int>());
    private static MonopolySpace Property(int index, string name, MonopolyGroup group, int price, int houseCost, params int[] rents) =>
        new(index, name, MonopolySpaceKind.Property, group, price, houseCost, Array.AsReadOnly(rents));
}
