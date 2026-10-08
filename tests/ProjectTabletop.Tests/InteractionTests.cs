namespace ProjectTabletop.Tests;

// Mirrors src/ProjectTabletop.Interaction/Verification/Program.cs, one test per check group.
public class InteractionTests
{
    [Fact] public void Slots() => SlotRegression.Run();
    [Fact] public void SlotsBoard() => SlotBoardRegression.Run();
    [Fact] public void BoardOpened() => BoardOpenedRegression.Run();
    [Fact] public void CrownAndDeed() => CrownDeedRegression.Run();
    [Fact] public void CrownAndDeedBoard() => CrownDeedBoardRegression.Run();
    [Fact] public void CrownAndDeedDrawer() => CrownDeedDrawerRegression.Run();
    [Fact] public void CrownAndDeedRollEvents() => CrownDeedRollEventRegression.Run();
    [Fact] public void CrownAndDeedPresentation() => CrownDeedPresentationRegression.Run();
    [Fact] public void GlobeBoard() => GlobeBoardRegression.Run();
    [Fact] public void MenuScroll() => MenuScrollRegression.Run();
    [Fact] public void Roulette() => RouletteRegression.Run();
    [Fact] public void RouletteBoard() => RouletteBoardRegression.Run();
    [Fact] public void MenuAndNavigation() => BoardInteractionRegression.CheckMenuAndNavigation();
    [Fact] public void OffTargetAndBounds() => BoardInteractionRegression.CheckOffTargetAndBounds();
    [Fact] public void HeldPinchAndDropout() => BoardInteractionRegression.CheckHeldPinchAndDropout();
    [Fact] public void IndependentHands() => BoardInteractionRegression.CheckIndependentHands();
    [Fact] public void Freshness() => BoardInteractionRegression.CheckFreshness();
    [Fact] public void ResetAndExternalNavigation() => BoardInteractionRegression.CheckResetAndExternalNavigation();
    [Fact] public void PhotoCopyNavigation() => BoardInteractionRegression.CheckPhotoCopyNavigation();
    [Fact] public void PaintBoard() => PaintBoardRegression.Run();
    [Fact] public void WaterGardenBoard() => WaterGardenBoardRegression.Run();
    [Fact] public void FootballGame() => FootballGameRegression.Run();
    [Fact] public void FootballBoard() => FootballBoardRegression.Run();
    [Fact] public void AnchoredSelection() => BoardInteractionRegression.CheckAnchoredSelection();
    [Fact] public void AnchorFreshnessAndConsumption() => BoardInteractionRegression.CheckAnchorFreshnessAndConsumption();
    [Fact] public void AnchorNavigationAndReset() => BoardInteractionRegression.CheckAnchorNavigationAndReset();
    [Fact] public void PhotoCopySpiral() => BoardInteractionRegression.CheckPhotoCopySpiral();
    [Fact] public void Blackjack() => BlackjackRegression.Run();
    [Fact] public void BlackjackBoard() => BlackjackBoardRegression.Run();
    [Fact] public void BoardFingerSelection() => BoardFingerSelectionRegression.Run();
    [Fact] public void PhotoCopyShutter() => PhotoCopyShutterRegression.Run();
    [Fact] public void PhotoCopyDrawer() => PhotoCopyDrawerRegression.Run();
}
