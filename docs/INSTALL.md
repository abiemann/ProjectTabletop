# Install Project Tabletop 1.2

## Requirements

- Windows 11 on an Intel/AMD x64 PC, with a working Direct3D graphics driver.
- For projection: a projector configured as an **extended** display, separate from the laptop controls, and a Windows-compatible camera with the whole board in view.
- A fixed, matte board with visible edges and suitable room lighting.

The setup includes .NET, the Windows App SDK, the native Visual C++ runtime, artwork and hand models. No SDK, Visual Studio, model download or online account is required to run the installed application. ARM64 and 32-bit Windows are not supported by this installer.

## Install and start

1. Open the repository's [latest release](https://github.com/abiemann/ProjectTabletop/releases/latest).
2. Download `ProjectTabletop-1.2.0-win-x64-setup.exe`. The automatically generated **Source code** archives are for developers, not the Windows setup.
3. Optionally compare `Get-FileHash .\ProjectTabletop-1.2.0-win-x64-setup.exe -Algorithm SHA256` with `SHA256SUMS.txt` from the same release.
4. Run setup. It installs for your Windows account under `%LOCALAPPDATA%\Programs\ProjectTabletop` and adds **Project Tabletop** to Start. An optional desktop shortcut is available.
5. Launch **Project Tabletop**. The setup is unsigned; Windows may show an unknown-publisher or reputation warning. The checksums identify the release files but are not a signing certificate.

Select the overhead camera and projector. Click **Start camera**, then **Open output**, then **Start board setup**; allow camera access in Windows if prompted. Keep the board still through the black/white scan and five alignment spots. Keep hands near the board surface. Use palm-down grouped fingers, aim with the middle fingertip, and separate the index sideways to select; pinch also works on gesture controls. Long-press controls require visibly breaking their stationary lettering for a second. Most controls require uncovering before another press; Globe zoom and Water Garden's **Duck+** repeat while their lettering stays covered.

Under **Camera Input**, **Frame rate** offers **30, 60 and 100 fps**, with a separate saved preference for each webcam. The app tries the requested native rate and falls back to an available mode when necessary; check the displayed resolution and frame rate for the actual result. Changing the rate while the camera is running restarts capture, so run board setup again afterward.

During board setup, **Estimated camera latency** appears on the laptop, using the alignment dots and subtracting **Projector latency (ms)**. Enter your projector's latency there; the default is 17 ms. This estimates camera exposure, buffering and delivery together. It is not shown on the projected board.

Blackjack, Crown & Deed, Globe, Water Garden and other available laptop previews can be explored without opening projector output. Read the [README](https://github.com/abiemann/ProjectTabletop/blob/v1.2.0/README.md) for each board's controls and capture behavior.

In the released **1.2.0 installer**, stir **Water Garden** with a real stick using its older stick-tip workflow in the laptop's **Hand interaction** panel. See [stick-tip tracking](https://github.com/abiemann/ProjectTabletop/blob/v1.2.0/README.md#stick-tip-tracking) for the setup steps. While the tip is detected over the water, the garden's bottom controls are disabled; move it away and uncover the control before starting a new press.

**Current source builds:** Water Garden and Football share guided double-bar training in **SETUP → BOARD CONTROL → Marker setup**. Choose the Water Garden / Paint marker or a Football player, place one complete marker in the neutral target, follow five targets over the actual board, slide between the two movement rings and back, lift it away, then choose **Save marker**. Movement must remain tracked across the route in both directions; failed checks show retry guidance before saving becomes available. **Cancel** preserves the old profile; **Recheck saved marker** tests existing geometry. Profiles are separate for each camera and controller. On first use without a saved marker, the board opens the trainer after camera and calibration are ready. Football skips finger-input players and trains the second marker only for two-human play. Cancel dismisses it for the current board visit; saving stops subsequent automatic prompts. Training is available through the guided **Marker setup** flow. Water Garden's navigation arrow remains usable while its marker is present; pond actions stay protected. All long presses check actual text/arrow corruption without an extra whole-button coverage requirement. See [current marker setup](../README.md#marker-setup), including validation status and local commands. The earlier Water Garden guided walkthrough passed a live check. The expanded Football flow, including both movement crossings and Save marker, passed a self-guided physical-tabletop check on 2026-10-16. Water Garden's added movement step is covered offline and still needs its own physical-tabletop acceptance. These source changes are not in the 1.2.0 installer above.

## Football quick start for the released 1.2.0 installer

1. Complete board setup, then open **Football** from the first menu page or the laptop. In **Football game**, choose **One player against AI** or **Two human players**.
2. Open **Hand interaction → Football input**. For a double-bar marker, use two equal parallel black rectangles with aligned ends and a clear gap, facing the webcam near the board surface. Choose **Black bar / pair**, select **Learn player 1 marker**, then click inside one bar in the live camera preview. Repeat for player 2 in two-human mode. Alternatively, choose **Index fingertip** and point with a palm-down hand. See [Football marker setup](https://github.com/abiemann/ProjectTabletop/blob/v1.2.0/README.md#football) for dimensions and learning details.
3. Keep player 1's marker or finger in the left half and player 2's in the right. The cars follow in the preview, but play begins only after **PLAY**. Cover its lettering for one second, then uncover it; laptop clicks also work. Fresh player input starts the countdown. First to five goals wins.
4. Controls hide during play. To reach **RESET** or **EXIT**, remove a human player's marker or hand until the match pauses and the controls return. **RESET** starts a fresh match at 0–0; returning the marker without resetting resumes the existing match. After a win, **PLAY** returns once the confetti settles and the buttons finish sliding into place.
5. For whistles, separate goal cheers, chants and continuous stadium ambience, open projector output and turn on **Enable Audio on Display Device** under **Projection Output**. This uses the selected display's matching Windows audio output; the sounds are included for offline play.

## Licenses

Open **Settings**, then select **Licenses and notices** in the laptop Settings panel, or select **Licenses** on the projected Settings board. Both open the readable license viewer on the laptop. Choose a document to view and copy its text. **Open all dependency notices** opens the installed license collection; formatted HTML/PDF documents identify their file within that folder. Hand-model licenses and credits are included in the same selector. The laptop Settings panel works without a camera or projector.

## Your files and privacy

- Settings, calibration, saved games, logs and local diagnostic recordings: `%LOCALAPPDATA%\ProjectTabletop`.
- Current source Photo Copy exports: `%USERPROFILE%\Pictures\Photo Copy`.
- Current source Paint exports: `%USERPROFILE%\Pictures\Paint`.
- The Hand-Tracking tester automatically records local camera clips when it detects a hand. Other boards do not start camera recordings. Camera frames and inference remain local.
- Crown & Deed requires **Save and Exit** to preserve a game across app restarts. Paint artwork must be saved before leaving the board.

Uninstall through **Settings → Apps → Installed apps → Project Tabletop**. Uninstall removes the program and shortcuts, preserving settings, saved games and pictures. To remove those too, back them up first and delete the corresponding user folders yourself. Updates use the same installation identity and preserve these folders.

## Local-control executable

The optional MCP/diagnostic client is installed at `%LOCALAPPDATA%\Programs\ProjectTabletop\ControlMcp\ProjectTabletop.ControlMcp.exe`. Launch it with no arguments for an MCP client, or use `--once get_status` while the app is running. It uses a current-user-only named pipe, not a network listener.

## Troubleshooting

If the app cannot see the camera, check Windows camera privacy settings and close other applications using it. For projector setup, use Extend rather than Duplicate and select a display separate from the controls. Rescan after moving the board, camera or projector. If hand acquisition is difficult, check the camera view, room lighting and projector brightness, and keep the hand close to the board.

For a startup problem, check `%LOCALAPPDATA%\ProjectTabletop` for the application log and reinstall the complete setup. Do not copy individual DLLs between versions. Report reproducible problems with your Windows version, camera/projector model and relevant local logs; review recordings before sharing them.
