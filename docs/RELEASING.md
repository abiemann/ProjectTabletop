# Windows CI and releases

## What runs

`.github/workflows/windows.yml` runs on pull requests, pushes to `main`, manual dispatch and `v*` tags. It uses `windows-2025-vs2026`, the exact .NET SDK in `global.json`, committed NuGet lock files, and actions pinned to commit hashes. Restore treats known dependency-vulnerability warnings as errors.

The build job compiles Release x64, runs the xUnit regression groups, publishes the app and ControlMcp as self-contained folders, adds the x64 Visual C++ CRT (14.51 or later), collects dependency notices, verifies models/artwork/version/payload hashes, and compiles an Inno Setup installer. The optional OpenCV FFmpeg backend is excluded; diagnostic video uses OpenCV's built-in MJPEG backend, covered by the production recorder regression. Windows camera capture and media playback retain their existing Windows API paths.

The smoke test silently installs into an isolated directory, checks the installed payload, starts the actual Release app with separate app data, navigates the boards, renders a Blackjack preview, shuts down, uninstalls and verifies that user data survives. It never starts a camera or projector. The hosted runner has development tools and system components; this is not a substitute for acceptance on an ordinary clean Windows 11 PC.

Passing runs upload the setup, `SHA256SUMS.txt` and a payload manifest as an Actions artifact. Test results and install/startup evidence are separate artifacts, retained for 14 days. Tag runs publish the installer to a GitHub Release only after all gates succeed. A failed upload leaves a draft; retrying may replace assets in that draft, but an already published release is never overwritten.

## Cut a release

1. Update `Directory.Build.props` with the intended three-part version. Update README, installation instructions, `NEW_PROJECT.md`, affected design/task documentation, and `docs/releases/<version>.md` against the final implementation. Separate verified results from remaining physical acceptance.
2. If dependencies change, regenerate and review the solution's lock files with `dotnet restore ProjectTabletop.sln -p:Platform=x64 --use-lock-file --force-evaluate`. Review new package/native license terms and any additions to `packaging/licenses`.
3. Validate and commit all source and documentation before building release artifacts or creating a tag. Push the commit to `main` and wait for Windows CI to pass, including the installer smoke test.
4. Tag that exact source commit, for example `git tag -a v1.0.0 -m "Project Tabletop 1.0.0"`, then `git push origin v1.0.0`.
5. Wait for the tag workflow and inspect the resulting release assets. Download the setup from GitHub and check its SHA-256 against the attached checksum file. For a private repository, only authorized accounts can access the release.

The workflow publishes with the built-in `GITHUB_TOKEN`, whose write permission is limited to the release job. Pull-request code has read-only repository permission. It does not change repository visibility. No signing identity is configured; installers are unsigned until a separate certificate/signing service is deliberately added.

## Reproduce the package locally

Use Windows 11 x64, Visual Studio 2026 with WinUI and C++ tools, SDK 10.0.26100, .NET SDK 10.0.401, PowerShell 7, and Inno Setup 6.3 or newer. Close existing app instances before installation smoke tests. Use fresh staging, output and test directories.

```powershell
dotnet restore ProjectTabletop.sln -p:Platform=x64 --locked-mode
dotnet build ProjectTabletop.sln -c Release -p:Platform=x64 --no-restore
dotnet test tests/ProjectTabletop.Tests/ProjectTabletop.Tests.csproj -c Release --no-build --no-restore
./scripts/Publish-Windows.ps1 -StagingDirectory artifacts/staging -Version 1.0.0
./scripts/Build-Installer.ps1 -StagingDirectory artifacts/staging -Version 1.0.0 -OutputDirectory artifacts/release
./scripts/Test-InstalledApp.ps1 -SetupPath artifacts/release/ProjectTabletop-1.0.0-win-x64-setup.exe -Version 1.0.0 -WorkDirectory artifacts/install-smoke
```

The installer smoke test creates and removes the per-user installation registration and shortcuts. Prefer a disposable Windows test machine rather than a PC with an existing installed copy.

## Remaining physical acceptance

Before claiming broad hardware reliability, test first-run setup and repeated navigation on all boards with naturally grouped palm-down hands; intact-caption hold cancellation; camera freeze, disconnect and reconnect; projector removal and display mode changes; save/resume and image exports; and a sustained session with observed presentation performance. Recheck the previously interrupted all-board native acquisition verification. Real-card recognition, arbitrary hand-height correction and depth-based touch remain outside the 1.0 acceptance claims.
