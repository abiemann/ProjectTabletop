# Offline menu previews

All board-card images are checked-in assets in
`src/ProjectTabletop.App/Assets/MenuPreviews`. Application builds copy those PNGs;
startup never runs this generator, the Paint simulation, or another board renderer
to create a thumbnail.

Regenerate on Windows x64 with the project's .NET SDK and a graphics adapter that
supports Win2D floating-point fields:

```powershell
./tools/MenuPreviewGenerator/Generate.ps1
```

The script builds the Debug app and the offline CLI, stages its self-contained
native runtime under ignored `artifacts/menu-preview-generator/runtime`, and runs
it in a separate process. It does not start a WinUI window, camera, projector, or
control pipe. `-SkipAppBuild` reuses an already built app. The generator is separate
from the solution and is never needed to run the app or build a release.

Each image is 2304×512, representing a 720×160 logical frame. Artwork stays anchored
to the right; menus uniformly scale by height and crop from the left. This retains
round Earth/chips and the original focal positions across common board shapes.
Captions, diagonal glass falloff and physical rounded clips remain live menu
rendering; they are not baked into the artwork.

Recipes preserve the established board previews. The offline CLI calls the actual
production Blackjack, Crown & Deed, Roulette, Globe and Paint rendering helpers
through reflection. It loads only the menu dragon asset for Dragon Slots. Paint
uses the existing 96-pixel-high simulation, seeded drops and settling sequence.
Roulette's complete bowl and spindle fill the thumbnail's height, without
foreground chips. Its composition stays anchored right, so
changing the board aspect crops the quiet left background instead of resizing
the wheel. Roulette's before images retain the previous rim-only framing.
The Globe uses the fixed captured opening pose and NASA imagery already credited
in `THIRD_PARTY_NOTICES.md`.

The generated `manifest.json` records recipe version, source hashes, asset hashes,
dimensions and decoded PNG alpha counts. Dragon Slots must be an RGBA PNG with
transparent exterior pixels and antialiased edges; a matte or checkerboard fails
generation. A second manifest and equal-size before/after images are written under
`artifacts/menu-preview-generator/comparisons` for 16:9 and 1.4:1 board aspects.
Small GPU and resampling differences are expected, especially when Paint's fixed
wide field is cropped instead of being simulated separately for every board aspect.

After changing artwork, theme colours, board geometry, shaders or these recipes,
regenerate, inspect the comparisons, and include updated PNGs plus their manifest
with the source change. Native GPU rendering is deterministic on a given graphics
stack, but bit-identical files across different drivers are not required.
