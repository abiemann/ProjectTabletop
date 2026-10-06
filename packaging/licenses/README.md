# Dependency license sources

The Windows release contains a `ThirdPartyNotices` directory and a
`dependency-notices.json` inventory. They are generated offline from the exact
published dependency graphs, restored NuGet packages, and the upstream texts
checked into this directory. The project license does not replace these terms.

`scripts/Export-DependencyNotices.ps1` copies package-supplied licenses and notices
verbatim, including the .NET self-contained runtime, Windows App SDK components,
WebView2 and Windows ML notices. Windows App SDK content packages are included
even when MSBuild copies their native files outside the normal `deps.json` graph.
SDK build tools and test-only dependencies are not redistributed by this step.
The Windows SDK .NET projection package includes the official Windows SDK
agreement and the C#/WinRT runtime license, pinned to the repository revision in
the distributed `WinRT.Runtime.dll` product version.
The app-local Visual C++ runtime uses the Microsoft Visual C++ V14 Redistributable
and Runtime agreement linked from Microsoft's Visual Studio 2026 license directory.

NuGet packages that omit their license text have exact-version entries in
`fallbacks.json`. Their upstream source URLs and SHA-256 values are recorded in
`sources.json`. The exporter verifies those hashes and refuses unknown license
gaps or a changed OpenCV native binary. To update a dependency, review its
embedded notices and any native components, update the matching source receipts
and fallback entry when needed, and run the exporter again. Do not replace a
missing component license with an unrelated generic license.

## OpenCV native components

The bundled `OpenCvSharpExtern.dll` is the unchanged Windows x64 native library
from OpenCvSharp 4.13.0.20260627, SHA-256
`41e6a418637b7992814a668272e3197306f75c01b151ddb24ac2de183fb9c3f3`.
Its `GetBuildInformation()` reports OpenCV revision
`fe38fc608f6acb8b68953438a62305d8318f4fcd`, contrib revision
`d99ad2a188210cc35067c2e60076eed7c2442bc3`, and a June 27, 2026 Windows build.
The upstream OpenCvSharp build recipe is pinned by its NuGet repository metadata
to `b161e7e012f5101f6d5dc68a835c59db6cc88b18`; its vcpkg baseline is
`1e199d32ad53aab1defda61ce41c380302e3f95c`.

The native notice collection includes the OpenCV and contrib license files,
OpenCV's bundled third-party notices, Intel IPP 2022.2 EULA and third-party
program notices, and the pinned vcpkg image/OCR dependencies and their direct
runtime dependencies. Some upstream license collections describe optional
components as well; retaining those notices does not mean every optional
component is present or used. For dual-licensed Intel ITT, this distribution uses
the BSD option. No Orbbec device library is separately bundled; its upstream
notice is retained for the OpenCV integration headers.

The optional `opencv_videoio_ffmpeg*.dll` is deliberately excluded from the
release. Diagnostic AVI recording and replay explicitly use OpenCV's built-in
MJPEG backend. OpenCV's original FFmpeg readme and license are retained only as
upstream context; they do not identify a shipped FFmpeg library. The exporter
fails if an FFmpeg plug-in is found in the staged release. Adding that plug-in
later requires a separate review of corresponding-source distribution, not just
copying an LGPL text.

This software is based in part on the work of the Independent JPEG Group.

## Source receipts

Most texts are unmodified files from a pinned upstream commit. Intel's EULA and
third-party-programs file were extracted unchanged from the exact OpenCV IPP
archive; the archive MD5 was checked against OpenCV's pinned build recipe and its
SHA-256 is recorded. The Visual C++ agreement is a text extraction of every body
paragraph of Microsoft's original Word document; the original document hash and
extraction method are recorded.
GIFLIB's license was extracted unchanged from its official SourceForge release
archive after verifying SHA-512 against the pinned vcpkg package recipe. The curl
conversion notice is the leading copyright/license text selected by that same
vcpkg baseline's port recipe.

Win2D 1.4.0 does not embed a license file. Its original NuGet license URL is
retained in the generated inventory, but that legacy URL currently redirects to
unrelated Windows web documentation. The fallback preserves the official Win2D
repository's MIT license at its pinned license-file revision rather than saving
that unrelated redirect as a license.
