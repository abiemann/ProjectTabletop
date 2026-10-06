namespace ProjectTabletop.Tests;

// Mirrors src/ProjectTabletop.Calibration/Verification/Program.cs, one test per check group.
public class CalibrationTests
{
    [Fact] public Task HomographyRegistrationAndPersistence() => CalibrationCoreRegression.RunAsync();
    [Fact] public void BoardSizeEstimate() => BoardSizeEstimateRegression.Run();
    [Fact] public void BoardOrientation() => BoardOrientationRegression.Run();
}
