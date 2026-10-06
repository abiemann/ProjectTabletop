using OpenCvSharp;
using ProjectTabletop.Vision;

if (args is ["--hand-caption-resolution"])
{
    HandAcquisitionCaptionResolutionRegression.Run();
    return;
}

if (args is ["--hand-coherent-shape"])
{
    HandAcquisitionCoherentShapeRegression.Run();
    return;
}

if (args is ["--hand-reflectance-fit"])
{
    HandAcquisitionReflectanceFitRegression.Run();
    return;
}

if (args is ["--hand-acquisition-compact-native-replay", var compactNativeSnapshot])
{
    HandAcquisitionCompactControlRegression.ReplayNative(compactNativeSnapshot);
    return;
}

if (args is ["--hand-acquisition-compact"])
{
    HandAcquisitionCompactControlRegression.Run();
    return;
}

if (args is ["--hand-acquisition-compact-replay", var compactSnapshot, var compactOccupied])
{
    HandAcquisitionCompactControlRegression.Replay(compactSnapshot, compactOccupied);
    return;
}

if (args is ["--hand-local-context"])
{
    HandAcquisitionLocalContextRegression.Run();
    HandAcquisitionHintRegression.Run();
    return;
}

if (args is ["--hand-acquisition-gold"])
{
    HandAcquisitionGoldDealRegression.Run();
    return;
}
if (args is ["--hand-acquisition-light"] or ["--hand-acquisition-illumination"])
{
    HandAcquisitionIlluminatedRenewalRegression.Run();
    return;
}
if (args is ["--hand-acquisition-history"])
{
    HandAcquisitionOpticalHistoryRegression.Run();
    return;
}
if (args is ["--paint"])
{
    PaintDisturbanceRegression.Run();
    return;
}
if (args is ["--boards"])
{
    BoardDetectionRegression.CheckBoardDetection();
    BoardDetectionRegression.CheckProjectorFieldIsNotBoard();
    BoardDetectionRegression.CheckUniformIllumination();
    BoardDetection white = BoardDetectionRegression.CheckHardwareWhiteScan();
    BoardDetectionRegression.CheckHardwareAmbientScan(white);
    BoardDetectionRegression.CheckMovedHardwareFrames(white);
    BoardDetectionRegression.CheckProjectedCornerMarkers();
    BoardDetectionRegression.CheckCalibrationSpot();
    AmbientBoardEdgeSupportRegression.Run();
    BoardCrossCheckRegression.Run();
    return;
}
if (args is ["--board-cross-check"])
{
    BoardCrossCheckRegression.Run();
    return;
}
if (args is ["--ambient-edges"])
{
    AmbientBoardEdgeSupportRegression.Run();
    return;
}
if (args is ["--hand-acquisition"])
{
    HandAcquisitionMotionRegression.Run();
    HandAcquisitionPresenceRegression.Run();
    HandTrackingSearchRegionsRegression.Run();
    HandPalmColorCorrectionRegression.Run();
    return;
}
if (args is ["--hand-color-correction"])
{
    HandPalmColorCorrectionRegression.Run();
    return;
}
if (args is ["--hand-search-regions"])
{
    HandTrackingSearchRegionsRegression.Run();
    return;
}
if (args is ["--hand-candidates"])
{
    HandCandidateContinuityRegression.Run();
    return;
}
if (args is ["--hand-visuals"])
{
    HandVisualSmoothingRegression.Run();
    HandSpotlightSmoothingRegression.Run();
    return;
}
if (args is ["--photo-copy"])
{
    PhotoCopySelectionRegression.Run();
    PhotoCopyRegression.Run();
    PhotoObjectRegression.Run();
    PhotoObjectTargetRegression.Run();
    PhotoCopyCameraImageRegression.Run();
    return;
}
if (args is ["--hands"])
{
    HandAcquisitionMotionRegression.Run();
    HandAcquisitionPresenceRegression.Run();
    HandTrackingSearchRegionsRegression.Run();
    HandPalmColorCorrectionRegression.Run();
    HandCandidateContinuityRegression.Run();
    HandTrackingRegression.Run();
    HandSpreadRegression.Run();
    FourFingerPoseRegression.Run();
    return;
}
if (args is ["--hand-poses"])
{
    HandSpreadRegression.Run();
    HandGestureRegression.Run();
    FourFingerPoseRegression.Run();
    return;
}
if (args is ["--four-fingers"] or ["--finger-selection"])
{
    FourFingerPoseRegression.Run();
    HandSpreadRegression.Run();
    HandGestureRegression.Run();
    return;
}
if (args is ["--hand-diagnostics"])
{
    HandTrackingDiagnosticsRegression.Run();
    return;
}
if (args is ["--photo-objects"])
{
    PhotoCopySelectionRegression.Run();
    PhotoObjectRegression.Run();
    PhotoObjectTargetRegression.Run();
    PhotoCopyCameraImageRegression.Run();
    return;
}
if (args is ["--hand-spotlights"])
{
    HandSpotlightRegression.Run();
    HandSpotlightSmoothingRegression.Run();
    return;
}
if (args is ["--markers"])
{
    BoardDetectionRegression.CheckProjectedCornerMarkers();
    return;
}
if (args is ["--markers", var extraMarkerFrame])
{
    BoardDetectionRegression.CheckProjectedCornerMarkers(extraMarkerFrame);
    return;
}

SyntheticPieceRegression.Run();
if (args is ["--vision-persistence"]) return;

BoardDetectionRegression.CheckBoardDetection();
BoardDetectionRegression.CheckProjectorFieldIsNotBoard();
BoardDetectionRegression.CheckUniformIllumination();
BoardDetection whiteHardwareBoard = BoardDetectionRegression.CheckHardwareWhiteScan();
BoardDetectionRegression.CheckHardwareAmbientScan(whiteHardwareBoard);
BoardDetectionRegression.CheckMovedHardwareFrames(whiteHardwareBoard);
BoardDetectionRegression.CheckProjectedCornerMarkers();
BoardDetectionRegression.CheckCalibrationSpot();
AmbientBoardEdgeSupportRegression.Run();
HandAcquisitionMotionRegression.Run();
HandAcquisitionPresenceRegression.Run();
HandTrackingSearchRegionsRegression.Run();
HandPalmColorCorrectionRegression.Run();
HandCandidateContinuityRegression.Run();
HandTrackingRegression.Run();
HandSpreadRegression.Run();
FourFingerPoseRegression.Run();
HandVisualSmoothingRegression.Run();
HandSpotlightRegression.Run();
HandSpotlightSmoothingRegression.Run();
PaintDisturbanceRegression.Run();
PhotoCopyRegression.Run();
PhotoObjectRegression.Run();
PhotoObjectTargetRegression.Run();
if (args.Length == 1)
{
    using Mat actual = Cv2.ImRead(args[0], ImreadModes.Unchanged);
    using Mat bgra = new();
    Cv2.CvtColor(actual, bgra, ColorConversionCodes.BGR2BGRA);
    BoardDetection? observed = BoardDetector.Detect(bgra.Width, bgra.Height,
        (int)bgra.Step(), BoardDetectionRegression.BytesOf(bgra));
    Console.WriteLine($"Captured board: {(observed is null ? "not found" :
        string.Join(", ", observed.Corners.Select(p => $"({p.X:F0},{p.Y:F0})")))}");
    BoardDetection? field = BoardDetector.DetectProjectedField(bgra.Width, bgra.Height,
        (int)bgra.Step(), BoardDetectionRegression.BytesOf(bgra));
    BoardDetection? scanned = BoardDetector.DetectUniformIllumination(bgra.Width, bgra.Height,
        (int)bgra.Step(), BoardDetectionRegression.BytesOf(bgra));
    BoardDetection? ambient = BoardDetector.DetectAmbientBoard(bgra.Width, bgra.Height,
        (int)bgra.Step(), BoardDetectionRegression.BytesOf(bgra));
    Console.WriteLine($"Uniform scan field: {BoardDetectionRegression.Format(field)}; cardboard: {BoardDetectionRegression.Format(scanned)}; " +
        $"ambient board: {BoardDetectionRegression.Format(ambient)}");
}
