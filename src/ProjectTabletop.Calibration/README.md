# ProjectTabletop calibration

This .NET 10 library maps raw camera pixels to projector window pixels. A homography applies to
one physical plane, so the board and the raised top of a piece have separate measurements. The
piece-top height is stored in millimeters for setup records; the homography is measured at that
height rather than derived from height alone.

```csharp
using ProjectTabletop.Calibration;

// Each list traces the same four physical points around its perimeter.
Point2[] boardCamera = [new(100, 80), new(1800, 90), new(1790, 1000), new(110, 990)];
Point2[] boardProjector = [new(900, 40), new(2940, 40), new(2940, 2080), new(900, 2080)];
Point2[] topCamera = [new(700, 400), new(1100, 405), new(1095, 650), new(705, 645)];
Point2[] topProjector = [new(1400, 700), new(1880, 700), new(1880, 994), new(1400, 994)];

var session = new CalibrationSession(
    cameraId: "camera-id", cameraWidth: 1920, cameraHeight: 1080,
    projectorDisplayId: "projector-id", projectorWidth: 3840, projectorHeight: 2160,
    boardPlane: new PlaneCalibration(boardCamera, boardProjector),
    pieceTopPlane: new PlaneCalibration(topCamera, topProjector),
    pieceTopHeightMillimeters: 5);

Point2 boatCenter = session.MapCameraToProjector(new Point2(900, 525), CalibrationPlane.PieceTop);
await CalibrationSessionStore.SaveAsync("calibration.json", session);
var restored = await CalibrationSessionStore.LoadAsync("calibration.json");
```

The four correspondences need to be accurately measured. A snapshot alone cannot infer the
camera-to-projector mapping. Use the piece-top mapping for the detected top outline and the
board mapping for background or board landmarks. The transforms do not clip to display bounds.
Measure the top-plane correspondences with a target at the piece's real height and at widely
separated board locations; four corners of the piece in one small area would require extrapolation
across the rest of the stage. Check the fitted mapping against additional points before projection.
Check `MatchesHardware` before reusing a saved session, and recalibrate after either device,
the board, projector image fitting, or the piece height changes. The software cannot detect
physical mount movement from saved identifiers alone.

Run `dotnet run --project src/ProjectTabletop.Calibration/Verification/ProjectTabletop.Calibration.Verification.csproj`
from the repository root to check affine and perspective mappings, reverse transforms, invalid
corners, and saved-session roundtrips.
