namespace ProjectTabletop.Tests;

// Mirrors the default run of src/ProjectTabletop.Vision/Regression/Program.cs, one
// test per check group, plus three groups the console runs only behind a flag.
// Fixtures and hand models are copied beside this assembly by the project reference.
public class VisionTests
{
    [Fact] public void SyntheticPieceTrainingAndPersistence() => SyntheticPieceRegression.Run();
    [Fact] public void BoardDetection() => BoardDetectionRegression.CheckBoardDetection();
    [Fact] public void ProjectorFieldIsNotBoard() => BoardDetectionRegression.CheckProjectorFieldIsNotBoard();
    [Fact] public void UniformIllumination() => BoardDetectionRegression.CheckUniformIllumination();

    [Fact]
    public void HardwareWhiteAmbientAndMovedFrames()
    {
        var white = BoardDetectionRegression.CheckHardwareWhiteScan();
        BoardDetectionRegression.CheckHardwareAmbientScan(white);
        BoardDetectionRegression.CheckMovedHardwareFrames(white);
    }

    [Fact] public void ProjectedCornerMarkers() => BoardDetectionRegression.CheckProjectedCornerMarkers();
    [Fact] public void CalibrationSpot() => BoardDetectionRegression.CheckCalibrationSpot();
    [Fact] public void AmbientBoardEdgeSupport() => AmbientBoardEdgeSupportRegression.Run();
    [Fact] public void HandAcquisitionMotion() => HandAcquisitionMotionRegression.Run();
    [Fact] public void HandAcquisitionPresence() => HandAcquisitionPresenceRegression.Run();
    [Fact] public void HandTrackingSearchRegions() => HandTrackingSearchRegionsRegression.Run();
    [Fact] public void HandPalmColorCorrection() => HandPalmColorCorrectionRegression.Run();
    [Fact] public void HandCandidateContinuity() => HandCandidateContinuityRegression.Run();
    [Fact] public void HandTracking() => HandTrackingRegression.Run();
    [Fact] public void HandSpread() => HandSpreadRegression.Run();
    [Fact] public void FourFingerPose() => FourFingerPoseRegression.Run();
    [Fact] public void HandVisualSmoothing() => HandVisualSmoothingRegression.Run();
    [Fact] public void HandSpotlight() => HandSpotlightRegression.Run();
    [Fact] public void HandSpotlightSmoothing() => HandSpotlightSmoothingRegression.Run();
    [Fact] public void PaintDisturbance() => PaintDisturbanceRegression.Run();
    [Fact] public void EyeTipDetectionAndTracking() => EyeTipRegression.Run();
    [Fact] public void ColorTipDetectionAndTracking() => ColorTipRegression.Run();
    [Fact] public void PhotoCopy() => PhotoCopyRegression.Run();
    [Fact] public void PhotoObject() => PhotoObjectRegression.Run();
    [Fact] public void PhotoObjectTarget() => PhotoObjectTargetRegression.Run();

    // Console flags only: --board-cross-check, --photo-copy and --hand-diagnostics.
    [Fact] public void BoardCrossCheck() => BoardCrossCheckRegression.Run();
    [Fact] public void PhotoCopyCameraImage() => PhotoCopyCameraImageRegression.Run();
    [Fact] public void HandTrackingDiagnostics() => HandTrackingDiagnosticsRegression.Run();
}
