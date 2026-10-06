# Install Project Tabletop 1.0

## Requirements

- Windows 11 on an Intel/AMD x64 PC, with a working Direct3D graphics driver.
- For projection: a projector configured as an **extended** display, separate from the laptop controls, and a Windows-compatible camera with the whole board in view.
- A fixed, matte board with visible edges and suitable room lighting.

The setup includes .NET, the Windows App SDK, the native Visual C++ runtime, artwork and hand models. No SDK, Visual Studio, model download or online account is required to run the installed application. GitHub authentication is required to download from a private repository. ARM64 and 32-bit Windows are not supported by this installer.

## Install and start

1. Open the repository's [latest release](https://github.com/abiemann/ProjectTabletop/releases/latest).
2. Download `ProjectTabletop-1.0.0-win-x64-setup.exe`. The automatically generated **Source code** archives are for developers, not the Windows setup.
3. Optionally compare `Get-FileHash .\ProjectTabletop-1.0.0-win-x64-setup.exe -Algorithm SHA256` with `SHA256SUMS.txt` from the same release.
4. Run setup. It installs for your Windows account under `%LOCALAPPDATA%\Programs\ProjectTabletop` and adds **Project Tabletop** to Start. An optional desktop shortcut is available.
5. Launch **Project Tabletop**. The first release is unsigned; Windows may show an unknown-publisher or reputation warning. The checksums identify the release files but are not a signing certificate.

Select the projector and open its output. Select the overhead camera, allow camera access in Windows if prompted, and start board setup. Keep the board still through the black/white scan and five alignment spots. Keep hands near the board surface. Use palm-down grouped fingers, aim with the middle fingertip, and separate the index sideways to select; pinch also works on gesture controls. Long-press controls require covering their stationary lettering for a second and uncovering it before another press.

Blackjack, Crown & Deed, Globe and other available laptop previews can be explored without opening projector output. Read the [README](https://github.com/abiemann/ProjectTabletop/blob/main/README.md) for each board's controls and capture behavior.

Open **Settings**, then select **Licenses and notices** in the laptop Settings panel, or select **Licenses** on the projected Settings board. Both open the readable license viewer on the laptop. Choose a document to view and copy its text. **Open selected file** opens the original document, and **Open all dependency notices** opens the installed license collection. Hand-model licenses and credits are included in the same selector. The laptop Settings panel works without a camera or projector.

## Your files and privacy

- Settings, calibration, saved games, logs and local diagnostic recordings: `%LOCALAPPDATA%\ProjectTabletop`.
- Photo Copy exports: the Windows Pictures folder, under `Project Tabletop`.
- Paint exports: Pictures, under `Project Tabletop\Paint`.
- The Hand-Tracking tester automatically records local camera clips when it detects a hand. Other boards do not start camera recordings. Camera frames and inference remain local.
- Crown & Deed requires **Save and Exit** to preserve a game across app restarts. Paint artwork must be saved before leaving the board.

Uninstall through **Settings → Apps → Installed apps → Project Tabletop**. Uninstall removes the program and shortcuts, preserving settings, saved games and pictures. To remove those too, back them up first and delete the corresponding user folders yourself. Updates use the same installation identity and preserve these folders.

## Local-control executable

The optional MCP/diagnostic client is installed at `%LOCALAPPDATA%\Programs\ProjectTabletop\ControlMcp\ProjectTabletop.ControlMcp.exe`. Launch it with no arguments for an MCP client, or use `--once get_status` while the app is running. It uses a current-user-only named pipe, not a network listener.

## Troubleshooting

If the app cannot see the camera, check Windows camera privacy settings and close other applications using it. For projector setup, use Extend rather than Duplicate and select a display separate from the controls. Rescan after moving the board, camera or projector. If hand acquisition is difficult, check the camera view, room lighting and projector brightness, and keep the hand close to the board.

For a startup problem, check `%LOCALAPPDATA%\ProjectTabletop` for the application log and reinstall the complete setup. Do not copy individual DLLs between versions. Report reproducible problems with your Windows version, camera/projector model and relevant local logs; review recordings before sharing them.
