# Install Project Tabletop 1.3.1

## Requirements

- Windows 11 on an Intel/AMD x64 PC, with a working Direct3D graphics driver.
- For projection: a projector configured as an **extended** display, separate from the laptop controls, and a Windows-compatible camera with the whole board in view.
- A fixed, matte board with visible edges and suitable room lighting.

The setup includes .NET, the Windows App SDK, the native Visual C++ runtime, artwork and hand models. No SDK, Visual Studio, model download or online account is required to run the installed application. ARM64 and 32-bit Windows are not supported by this installer.

## Install and start

1. Open the repository's [latest release](https://github.com/abiemann/ProjectTabletop/releases/latest).
2. Download `ProjectTabletop-1.3.1-win-x64-setup.exe`. The automatically generated **Source code** archives are for developers, not the Windows setup.
3. Optionally compare `Get-FileHash .\ProjectTabletop-1.3.1-win-x64-setup.exe -Algorithm SHA256` with `SHA256SUMS.txt` from the same release.
4. Run setup. It installs for your Windows account under `%LOCALAPPDATA%\Programs\ProjectTabletop` and adds **Project Tabletop** to Start. An optional desktop shortcut is available.
5. Launch **Project Tabletop**. The setup is unsigned; Windows may show an unknown-publisher or reputation warning. The checksums identify the release files but are not a signing certificate.

Select the overhead camera and projector. Click **Start camera**, then **Open output**, then **Start board setup**; allow camera access in Windows if prompted. Keep the board still through the black/white scan and five alignment spots. Keep hands near the board surface. Use palm-down grouped fingers, aim with the middle fingertip, and separate the index sideways to select; pinch also works on gesture controls. Long-press controls require visibly breaking their stationary lettering for a second. Most controls require uncovering before another press; Water Garden's **DUCK+** repeats while its lettering stays covered. Globe zoom and pitch use their dotted bars instead of repeating buttons.

Under **Camera Input**, **Frame rate** offers **30, 60 and 100 fps**, with a separate saved preference for each webcam. The app tries the requested native rate and falls back to an available mode when necessary; check the displayed resolution and frame rate for the actual result. Changing the rate while the camera is running restarts capture, so run board setup again afterward.

During board setup, **Estimated camera latency** appears on the laptop, using the alignment dots and subtracting **Projector latency (ms)**. Enter your projector's latency there; the default is 17 ms. This estimates camera exposure, buffering and delivery together. It is not shown on the projected board.

Blackjack, Crown & Deed, Globe, Water Garden and other available laptop previews can be explored without opening projector output. Read the [README](https://github.com/abiemann/ProjectTabletop/blob/v1.3.1/README.md) for each board's controls and capture behavior. Turn **PROJECTION PREVIEW** off on the laptop to reduce duplicate rendering while keeping projector output and tracking active.

**Automatic camera picture profiles** starts enabled. It saves compatible camera brightness/contrast values as Default and uses a lower-contrast Dark scene profile for Globe, restoring Default for other boards and calibration. It does not change shutter, ISO, white balance or frame rate. Unsupported controls are left alone. After making manual camera adjustments, use **Use current settings as Default**; see [automatic profiles](https://github.com/abiemann/ProjectTabletop/blob/v1.3.1/README.md#automatic-camera-picture-profiles).

## Guided marker setup

Water Garden, Paint and Football share guided double-bar training in **SETUP → BOARD CONTROL → Marker setup**. Use two equal parallel black rectangles with aligned ends and a clear gap on a light backing, facing the webcam close to the board. Choose the Water Garden / Paint marker or a Football player, place one complete marker in the neutral target, follow five targets over the actual board, slide between the two movement rings and back, lift it away, then choose **SAVE MARKER**. Movement must remain tracked across the route in both directions; failed checks show retry guidance before saving becomes available. **Cancel** preserves the old profile; **Recheck saved marker** tests existing geometry.

Profiles are separate for each camera and controller; Paint reuses Water Garden's profile. On first use without a saved marker, these boards open the trainer after camera and calibration are ready. Football skips finger-input players and trains the second marker only for two-human play. Cancelling dismisses setup for the current visit; saving stops subsequent automatic prompts. Water Garden's navigation arrow remains usable while its marker is present; pond actions stay protected. See [marker setup](https://github.com/abiemann/ProjectTabletop/blob/v1.3.1/README.md#marker-setup) for dimensions, lighting guidance and validation limits. The expanded Football trainer passed a self-guided physical-tabletop check; Water Garden's added movement step still needs its own physical acceptance.

## Football quick start

1. Complete board setup, then open **Football** from the first menu page or the laptop. In **Football game**, choose **One player against AI** or **Two human players**.
2. Choose **Double-bar marker** for each human player and complete the projected trainer, saving player 1's marker and player 2's in two-human mode. The crowd stays silent during training and starts after the last required marker is saved. Alternatively, choose **Index fingertip** and point with a palm-down hand. See [Football](https://github.com/abiemann/ProjectTabletop/blob/v1.3.1/README.md#football) for controller settings and marker details.
3. Keep player 1's marker or finger in the left half and player 2's in the right. The cars follow in the preview, but play begins only after **PLAY**. Cover its lettering for one second, then uncover it; laptop clicks also work. Fresh player input starts the countdown. First to five goals wins.
4. Controls hide during play. To reach **RESET** or **EXIT**, remove a human player's marker or hand until the match pauses and the controls return. **RESET** starts a fresh match at 0–0; returning the marker without resetting resumes the existing match. After a win, **PLAY** returns once the confetti settles and the buttons finish sliding into place.
5. For whistles, separate goal cheers, chants and continuous stadium ambience, open projector output and turn on **Enable Audio on Display Device** under **Projection Output**. This uses the selected display's matching Windows audio output; the sounds are included for offline play.

Long rallies now add extra balls after 15 and 30 seconds, up to seven at once. A goal returns the next kickoff to a single ball.

## Globe and Paint

On **Globe**, hold the up-arrow to reveal **EXIT**, **MODEL** and **SAVE**. Touch the left **ZOOM** or right **PITCH** dotted bar to set that value, then lift to retain it. MODEL opens and closes **CLOUDS**, **NO CLOUDS** and **NIGHT** choices. Only the selected Earth model loads; a short **Loading...** message may appear. Reaching across MODEL or SAVE to a model choice cancels the lower buttons' holds. SAVE writes Earth and stars without controls or labels and confirms **Saved to Pictures folder**.

On **Paint**, the trained double bars lay ink behind their motion and continue pouring when stationary. Bare hands do not paint. Hold the bottom-left arrow to reveal **EXIT**, **RESET** and **SAVE**. SAVE exports the painting without controls and confirms **Saved to Pictures folder**. Lift the marker before swiping along the bottom in either direction to hide or restore controls. Globe supports the same swipe toggle; Roulette intentionally keeps its controls visible.

## Licenses

Open **Settings**, then select **Licenses and notices** in the laptop Settings panel, or select **Licenses** on the projected Settings board. Both open the readable license viewer on the laptop; the projected board shows **Please view the licenses in the main app window.** Choose a document to view and copy its text. **Open all dependency notices** opens the installed license collection; formatted HTML/PDF documents identify their file within that folder. Hand-model licenses and credits are included in the same selector. The laptop Settings panel works without a camera or projector.

## Your files and privacy

- Settings, calibration, saved games, logs and local diagnostic recordings: `%LOCALAPPDATA%\ProjectTabletop`.
- Photo Copy exports: `%USERPROFILE%\Pictures\Photo Copy`.
- Paint exports: `%USERPROFILE%\Pictures\Paint`.
- Globe exports: `%USERPROFILE%\Pictures\Globe`.
- The Hand-Tracking tester automatically records local camera clips when it detects a hand. Other boards do not start camera recordings. Camera frames and inference remain local.
- Crown & Deed requires **Save and Exit** to preserve a game across app restarts. Paint artwork must be saved before leaving the board.

Board image exports use local Pictures under your Windows user profile, even when OneDrive redirects the Pictures library. Existing exports are not moved.

Uninstall through **Settings → Apps → Installed apps → Project Tabletop**. Uninstall removes the program and shortcuts, preserving settings, saved games and pictures. To remove those too, back them up first and delete the corresponding user folders yourself. Updates use the same installation identity and preserve these folders.

## Local-control executable

The optional MCP/diagnostic client is installed at `%LOCALAPPDATA%\Programs\ProjectTabletop\ControlMcp\ProjectTabletop.ControlMcp.exe`. Launch it with no arguments for an MCP client, or use `--once get_status` while the app is running. It uses a current-user-only named pipe, not a network listener.

## Troubleshooting

If the app cannot see the camera, check Windows camera privacy settings and close other applications using it. For projector setup, use Extend rather than Duplicate and select a display separate from the controls. Rescan after moving the board, camera or projector. If hand acquisition is difficult, check the camera view, room lighting and projector brightness, and keep the hand close to the board.

For a startup problem, check `%LOCALAPPDATA%\ProjectTabletop` for the application log and reinstall the complete setup. Do not copy individual DLLs between versions. Report reproducible problems with your Windows version, camera/projector model and relevant local logs; review recordings before sharing them.
