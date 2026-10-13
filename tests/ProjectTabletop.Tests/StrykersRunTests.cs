namespace ProjectTabletop.Tests;

public class StrykersRunTests
{
    [Fact] public void Game() => StrykersRunGameRegression.Run();
    [Fact] public void Campaign() => StrykersRunCampaignRegression.Run();
    [Fact] public void Board() => StrykersRunBoardRegression.Run();
    [Fact] public void PatternRail() => PatternRailRegression.Run();
}
