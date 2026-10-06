using ProjectTabletop.Interaction;

SlotRegression.Run();
SlotBoardRegression.Run();
BoardOpenedRegression.Run();
MonopolyRegression.Run();
MonopolyBoardRegression.Run();
MonopolyDrawerRegression.Run();
MonopolyRollEventRegression.Run();
MonopolyPresentationRegression.Run();
GlobeBoardRegression.Run();
MenuScrollRegression.Run();
RouletteRegression.Run();
RouletteBoardRegression.Run();
BoardInteractionRegression.CheckMenuAndNavigation();
BoardInteractionRegression.CheckOffTargetAndBounds();
BoardInteractionRegression.CheckHeldPinchAndDropout();
BoardInteractionRegression.CheckIndependentHands();
BoardInteractionRegression.CheckFreshness();
BoardInteractionRegression.CheckResetAndExternalNavigation();
BoardInteractionRegression.CheckPhotoCopyNavigation();
PaintBoardRegression.Run();
BoardInteractionRegression.CheckAnchoredSelection();
BoardInteractionRegression.CheckAnchorFreshnessAndConsumption();
BoardInteractionRegression.CheckAnchorNavigationAndReset();
BoardInteractionRegression.CheckPhotoCopySpiral();
BlackjackRegression.Run();
BlackjackBoardRegression.Run();
BoardFingerSelectionRegression.Run();
PhotoCopyShutterRegression.Run();
PhotoCopyDrawerRegression.Run();
Console.WriteLine("Board interaction verification passed: menu/navigation, shared hit targets, off-target consumption, " +
    "held-pinch suppression, dropouts, independent hands, freshness, anchored selection and navigation barriers, " +
    "reset, Photo Copy restarts and dense full-board inward spiral placement.");
