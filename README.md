# ProjectTabletop

ProjectTabletop is a local Windows C# app for projection mapping onto movable tabletop cards. A laptop window handles camera selection, snapshots, training, media assignment, and calibration. A separate window sends the animated scene to the projector. The app is designed to recognize several distinct cards at once and assign each an image or looping video overlay; real-card recognition and alignment still need testing.

## Build and run

Open `ProjectTabletop.sln` in Visual Studio 2026 on Windows 11, select **x64**, and start `ProjectTabletop.App`. The app targets .NET 10 and includes a self-contained Windows App SDK runtime. From a terminal:

```powershell
dotnet build ProjectTabletop.sln -c Debug -p:Platform=x64
dotnet run --project src/ProjectTabletop.App/ProjectTabletop.App.csproj -c Debug -p:Platform=x64
```

The app uses WinUI 3, Win2D, Windows MediaPlayer frame-server video, MediaFrameReader, and OpenCvSharp. Video frames are copied to reusable Direct3D surfaces for composition; camera frames are copied to CPU memory for recognition. Choose local Windows-decodable video or PNG/JPEG image files. H.264 MP4 is the recommended first video format. No media files or trained card images are bundled.

The square stage center-crops wide background media to avoid stretching. Piece media is center-cropped to its fitted outline before clipping; some source edges may be omitted.

## First setup

1. Connect the projector as an extended Windows display. Choose it under **Projection display**, open output, and keep it full screen during calibration and play. The picker shows both the physical display mode and Windows layout size; calibration uses the fullscreen canvas layout, which may be smaller than the physical pixel resolution.
2. Choose **Android Webcam** for the phone on the boom. The app shows its negotiated camera resolution and frame rate after it starts. Confirm the entire board and projected square have margin inside the preview before calibrating.
3. Press **Start board setup** to open the projector output, start the selected webcam, and show the grid with “Center the white board in the grid.” Orange guides animate onto the projected grid corners. An orange outline and corner brackets mark the detected physical board in the camera preview. The detector requires the complete outer board and inner projected grid to be visible; it reports no board when uncertain. Use the stage controls to measure and fit a 21 × 21 inch square on the white board, keeping the rest of the projector image black. Press **End board setup** before returning to media playback.
4. Choose **Board corners** and click the four projected crosshair centers in the live camera view, in the order shown. Enter the measured card-top height. Choose **Raised top points**; for each crosshair, move a card until the target lands on its top center, then click that center in the camera view. Save calibration. Recalibrate if the camera, projector, board, image fitting, output mode, or card height changes.
5. Save several full-resolution raw PNGs with each uniquely patterned card at different positions and angles for offline labeling and regression work. The **Save raw PNG** button works without annotations. For in-app labeling, freeze a snapshot, enter a stable piece ID, click around its top outline, mark its front direction, and add the labeled sample. Repeat for every card, then press **Train**. The vision tuning controls can adjust candidate segmentation, thresholds, and minimum area; empty-board difference is intended for a fixed projected image.
6. Assign each piece ID a local image or looping video. Select a background image or looping video. Inspect the live camera detections and projector overlays. Record held-out real frames for regression checks before relying on the alignment.

The vision profile and labeled camera snapshots default to `%LOCALAPPDATA%\ProjectTabletop\VisionAutosave`; unannotated captures go to `%LOCALAPPDATA%\ProjectTabletop\RawSnapshots`; calibration is saved to `%LOCALAPPDATA%\ProjectTabletop\calibration.json`. Set `PROJECT_TABLETOP_DATA_DIR` to another folder before launch to redirect these files. The app loads its autosaved vision profile at startup; the UI can also save or load a profile from a chosen folder.

## Validation status

- The full solution build passed with zero warnings and errors on September 26, 2026.
- On September 26, 2026, Windows reported the connected secondary display at **3840 × 2160, 60 Hz physical** with a **1280 × 720 layout**. The test grid and an unrelated local **1920 × 1080, 30 fps MP4** rendered within the centered square with black margins. A continuous run longer than ten minutes with the webcam active showed looping video and a responsive app; sampled output and preview rates were about 60 draw callbacks/s with no slow callbacks in those samples. At the end the app reported 30,017 video frame-ready events and 18,564 GPU surface copies. These counts do not measure displayed or dropped frames, and 4K/60 video decoding remains untested.
- The intended overhead camera is the phone on the lower boom opposite the higher projector. Windows exposes it as **Android Webcam** at **1920 × 1080, 30 fps** alongside the separate **USB2.0 HD UVC WebCam**. The app selected the phone, saved full-resolution PNGs, and captured the entire projected test grid in its earlier view. That capture clipped the outer board’s bottom edge; the camera has since been moved, but the new framing and live board detector have not yet been verified. The earlier room capture came from the other camera. Vision-profile save/reload passed. No real-card capture, training, or live tracking has been verified yet.
- Board setup now projects the grid, centered instruction, and animated orange target guides. A Canny contour detector returns four physical-board corners only when it can distinguish a complete outer board from the inner projected grid. Synthetic board/grid regression passes, and the earlier clipped phone capture correctly returns no board. The orange projector guides mark target grid corners; orange camera-preview brackets mark detected board corners. Live detection and alignment with the repositioned camera remain to be checked.
- The calibration library passes four-point, interior roundtrip, invalid geometry, and save/load checks. Run `dotnet run --project src/ProjectTabletop.Calibration/Verification/ProjectTabletop.Calibration.Verification.csproj`.
- The vision library passes a synthetic multi-card regression, including two cards with the same outline and distinct bright patterns plus an unknown card: 2/2 known cards identified, 0 false positives, 0.20-pixel mean center error, 2.5° mean angle error, and 0.927 mean outline IoU. Run `dotnet run --project src/ProjectTabletop.Vision/Regression/ProjectTabletop.Vision.Regression.csproj`. This checks code flow and geometry; it does not prove tracking under moving projection, glare, hand occlusion, or the final card materials.
- The app reports compositor draw callbacks, Win2D slow callbacks, video frame-ready events, and GPU surface copies. These are **not** actual presentation or dropped-frame measurements.

The actual white patterned cards and captured training frames are still needed to label, train, and regression-test the physical setup. Board and raised-top calibration have not been measured on the physical stage. The initial segmentation and SVM classifier are adjustable baselines; their accuracy must be judged on real images.

See [NEW_PROJECT.md](NEW_PROJECT.md) for the optics, physical setup, and engineering targets. The current app scope includes multiple simultaneously tracked patterned cards, extending that document's original one-piece proof of concept.
