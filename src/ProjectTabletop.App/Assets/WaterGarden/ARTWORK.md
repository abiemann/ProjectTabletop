# Water Garden artwork

`moss-rocks.png` is original AI-generated artwork made for Project Tabletop on
2026-10-06 with the built-in OpenAI `image_gen` tool and
`transparent_background: true`. It depicts three irregular slate-gray stones,
restrained olive-green moss and two sparse grass tufts, lit from the upper left.
It is an overhead cutout for the garden's corner decoration.

The reference video was viewed for the requested garden mood. Its frames were
not supplied to the generator, copied into this image or included in the app.
The asset contains no reference branding or downloaded photographic textures.
Original generated files remain in Codex's generated-images folder. No
programmatic image editing has been applied.

## Scene composition

The garden uses a fixed close oblique camera, with larger procedural pebbles and
perspective-rendered slate walls and raised coping. Refraction follows the viewing
ray through the water's full depth to the pebble bed or submerged wall. The original
rock cutout is placed inside the far-left and near-right corners, with the larger
foreground cluster emphasizing depth. Both clusters leave a visible water margin
between their silhouettes and the basin walls; their source pixels and provenance
are unchanged. Input uses
the same camera projection and rock silhouettes to keep ripples on visible water.

Five tiny yellow rubber ducks use original analytic three-dimensional geometry,
including rounded bodies, wings, bills and eyes. GPU buoyancy samples the actual
water surface so the ducks bob, tilt and drift; the water reflects the same moving
geometry. **Calm Water** restores their starting positions and clears their motion.
The ducks introduce no image asset or additional dependency.

## Selected output and alpha inspection

- Source: `exec-dde3e9bc-6212-4d38-b939-fba02210262b.png`.
- PNG dimensions: 1254 × 1254; Pillow mode: `RGBA`.
- SHA-256: `85c5f3209c14d5c384ddbf47c09a867a3b9e90a6c01c40f954d932a76c67fba0`.
- Alpha range: 0–255; 863,529 pixels are fully transparent. Most solid rock
  pixels have alpha 253–254, as supplied by the generator.
- Every perimeter pixel has alpha 0. The four corners, four edge midpoints and
  exterior/open-gap samples `(700,1030)`, `(640,990)`, `(1050,350)` and
  `(440,1010)` are exactly `(0,0,0,0)`.
- Enclosed spaces between fine grass/edge detail at `(935,388)`, `(944,399)`
  and `(939,351)` have alpha 0. Visual review found no painted checkerboard,
  backdrop or exterior cast shadow.

A connected-component check found 49 detached components totaling 61 pixels
with alpha 1, including `(387,1211)`. These nearly invisible generator remnants
remain in the selected internal texture, so it does not meet a strict
every-exterior-pixel-is-zero requirement. A built-in cleanup attempt still
returned alpha-1 remnants and softened the mineral detail; it was not selected.

## Initial generation prompt

```text
Use case: photorealistic-natural.
Asset type: one original transparent PNG game-environment cutout for a refined Japanese shallow-water garden.
Primary request: a compact cluster of exactly THREE naturally irregular river rocks: one larger weathered slate-gray stone and two smaller gray companion stones. Restrained soft green moss grows in uneven patches on the rock tops and crevices, leaving plenty of realistic stone visible; a few delicate fine grass blades emerge from two tiny crevices. High-quality realistic 3D/photoscan appearance with detailed mineral grain, subtle worn facets and convincing moss fibers.
Composition: true orthographic overhead view, camera exactly perpendicular to the tops, with no horizon, no perspective tilt and no visible vertical side view. Compact asymmetrical triangular grouping, naturally touching or partially overlapping, all stones and every grass blade fully inside the image with a generous transparent margin. Small natural gaps between stones and grass blades must remain genuinely transparent.
Lighting: soft natural illumination from the upper left, subdued highlights, self-shading on the rock surfaces only. Elegant natural gray/slate and muted moss green, not bright cartoon green.
Background: actual transparent alpha, no ground, no gravel bed, no water, no backdrop, no border, no text, no logo. No cast shadow, glow, haze, matte or painted ambient shadow outside the physical rock/moss/grass silhouettes. Never draw or bake a checkerboard transparency pattern into the pixels. Clean anti-aliased edges with alpha zero throughout the intended exterior and gaps.
Output: a single square, high-detail RGBA PNG asset, original artwork rather than a reproduction of any reference scene.
```

The initial output contained four stones. This focused edit created the selected
three-stone arrangement while retaining its natural texture.

## Selected edit prompt

```text
Edit this asset only: remove the small separate foreground rock at the bottom of the image completely, leaving actual transparent alpha where it was, so the final cluster contains exactly THREE stones: the large irregular rock on the left, the rock behind it toward the top, and the rock on the right. Preserve the remaining three rocks' shapes, placement, photoreal mineral and moss detail, subtle upper-left light and overhead viewpoint. Preserve the two sparse grass tufts where possible, but remove detached grass, soil, shadow or debris that belonged only to the removed bottom stone. Keep every gap genuinely transparent, no exterior cast shadow or backdrop, and never bake a checkerboard into the pixels. This must remain one original high-quality RGBA PNG rock cluster cutout, fully framed on actual transparency.
```

## Unselected cleanup prompt

```text
Use case: background-extraction. Clean only the transparency of this exact three-stone cutout. Preserve the rock shapes, mineral grain, restrained moss, grass blades, placement, size, overhead view and lighting exactly. The empty exterior and gaps must be pure alpha 0 with no isolated alpha-1 dust or detached remnants anywhere, including far below the rocks. Keep clean natural antialiased silhouette edges, but no shadow, haze or painted checkerboard. Solid rock and moss interior should be opaque. Return the same beautiful cutout as one genuinely transparent RGBA PNG.
```
