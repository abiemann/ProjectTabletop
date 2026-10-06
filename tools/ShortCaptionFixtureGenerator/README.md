# Neutral short-caption fixtures

This offline Windows x64 generator produces the three checked-in
`short-gold-caption-*.png` Vision regression images. It renders only the production
Crown & Deed button plates on an original, neutral optical calibration scene.
The scene has no named properties, game branding, camera photographs or personal
data. It does not load the city illustration or run a game.

After building the Debug x64 application, regenerate with:

```powershell
./tools/ShortCaptionFixtureGenerator/Generate.ps1 -SkipAppBuild
```

Without `-SkipAppBuild`, the script builds the application first. It stages the
Win2D runtime under ignored `artifacts/short-caption-fixture-generator/runtime`
and runs a separate process without a window, camera, projector or control pipe.
It changes only the three fixture PNGs and their hash/geometry manifest;
application startup and ordinary builds never execute this tool.

The reference is 1000 × 1000. The camera views are rendered independently at
3840 × 2160 using the historical affine mapping retained by the regression.
Control bounds, short Start caption, two independent colour-reference bands and
four synthetic uniform red strips retain the regression's optical purpose.
Every saved PNG is decoded and compared byte for byte with its render target.
The manifest stores only relative filenames, hashes, dimensions and geometry.

Run the Vision `--hand-acquisition` suite after regeneration. The short-caption
test must still show less than 7% raw glyph damage, at least 7% measured control
and caption foreground, two fresh frames, cold/warmed arrival, stationary
persistence and cancellation after removal. Keep those requirements fixed if
fonts or graphics drivers change the exact measurement.
