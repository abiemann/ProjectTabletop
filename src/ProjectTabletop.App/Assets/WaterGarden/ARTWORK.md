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
perspective-rendered warm limestone walls with rounded corners and softened edges.
The same camera projects original fine-grained sand artwork onto the ground
beneath the basin, with rounded contact shading along its footprint.
A low layered wet-stone cascade stands at the far center of the pond. Its
irregular ledges are native three-dimensional geometry. A bounded particle
simulation moves water over the stone face; a reconstructed three-dimensional
density field provides the water surface for refraction and reflections.
Actual falling parcels drive the pond wave field where they land. The dark
slate material is original generated imagery,
with its prompt recorded below. It uses no pixels, plants or branding from the
supplied photograph. Refraction follows the viewing
ray through the water's full depth to the pebble bed or submerged wall. The original
rock cutout is placed inside the far-left and near-right corners, with the larger
foreground cluster emphasizing depth. Both clusters leave a visible water margin
between their silhouettes and the basin walls; their source pixels and provenance
are unchanged. Input uses
the same camera projection and rock silhouettes to keep ripples on visible water.

Ten tiny yellow rubber ducks use original analytic three-dimensional geometry,
including rounded bodies, wings, bills and eyes. GPU buoyancy samples the actual
water surface so the ducks bob and tilt. Local currents from measured cascade
landings carry them outward, and confirmed moving stick strokes push nearby ducks
aside; the water reflects the same moving geometry. **Duck+** adds one duck at a
time up to twenty, while **Reset** restores the original ten and clears their motion.
The ducks introduce no image asset or additional dependency.

## Selected output and alpha inspection

### Warm limestone basin material

`warm-limestone.png` is an original opaque material texture generated on
2026-10-06 with OpenAI's built-in image-generation tool. Its warm ivory and
honey-beige limestone mineral detail is mapped across the pond's thin rounded
walls and softened lips. The geometry and lighting
are native shaders; the supplied reference photograph's pixels are not used.
The approved menu thumbnail remains the earlier saved illustration.

Source: `exec-f609169a-518b-4646-b25d-6169384311bb.png`.
SHA-256: `bcd339092f0100a53e21fad7e11bf7bf183f9d453efdff3a537f3b4bbaf1406a`.

Generation prompt:

```text
Use case: photorealistic-natural. Asset type: seamless physically based base-color texture for a high-quality real-time 3D Japanese water garden's carved limestone basin walls and rounded rim. Create a perfectly flat orthographic square material scan covering the full frame edge to edge: beautiful warm ivory and pale honey-beige honed limestone, subtle cream travertine mineral clouds, delicate irregular pores, very restrained soft tan wisps and faint fossil flecks, natural quiet stone suitable for an elegant tranquil spa garden. Low contrast and harmonious warm color, real photographic mineral microdetail, not computer noise. Even diffuse shadow-free albedo illumination with no light gradient, no perspective, no raised objects, no bevels, no border, no scene, no water, no plants, no text, no watermark, no logos. Seamlessly tileable horizontally and vertically. Do not make grey concrete, salt-and-pepper granite, black speckles, checkerboard, big cracks, highly directional bands, glossy marble veins, or orange/yellow saturated sandstone. This is a material texture only, not a rendering of a basin. Opaque background, high resolution.
```

### Wet slate cascade material

`wet-slate.png` is an original opaque rock material generated on 2026-10-06
with OpenAI's built-in image-generation tool. Native geometry shapes its
uneven stacked ledges; shader lighting adds the wet finish and flowing water.
The supplied reference photograph's pixels are not included in the app.

Source: `exec-7cb57540-28ab-4151-bad3-b90bc5d09539.png`.
SHA-256: `d1b2d77689d50f553916842714086147fc3e056c031fd6b80289ae96a148c35f`.

Generation prompt:

```text
Use case: photorealistic-natural. Asset type: seamless square diffuse albedo material texture for native 3D game geometry. Create an original richly detailed dark wet layered slate and weathered basalt rock surface inspired by natural stacked garden-fountain stones: thin irregular horizontal mineral strata, charcoal graphite and deep olive-gray stone, restrained muted umber and blue-violet mineral undertones, tiny seams and worn rough edges in the material only, occasional subtle olive moss staining in crevices. It should feel like real damp water-darkened rock with natural visual variation and refined high-quality detail. Perfectly flat orthographic material scan filling the frame edge-to-edge, horizontally and vertically seamless, even diffuse light without shadows or specular highlights because the renderer adds shading and wet gloss. No individual rock object or pile, no waterfall or water, no background scene, no border, no text, no logos, no photo reproduction. Avoid uniform gray concrete, plastic, artificial glitter, strong regular stripes, big cracks, white speckles, or baked directional light. Opaque square PNG texture.
```

### Sand ground material

`sand-ground.png` is an original opaque, 1254 × 1254 RGB sand texture generated
on 2026-10-06 with OpenAI's built-in `image_gen` tool. The supplied sand
photograph guided its fine grain and muted beige colour; no reference pixels,
watermark or branding are included. The renderer maps the texture onto the
ground plane using the Water Garden camera so its apparent grain and direction
recede with the basin. The earlier Water Garden menu thumbnail remains unchanged.

Source: `exec-93464ed3-235c-4b51-ae5d-1ed2132ca452.png`.
SHA-256: `525f5eb810a409c343ef9f8e95403f60e6d517980c8e7efd11d2424f5f1ffe86`.

Generation prompt:

```text
Use case: photorealistic-natural
Asset type: original seamless ground-material texture for a perspective-mapped 3D water garden, to sit quietly behind a stone basin.
Primary request: ultra-fine natural beige beach sand like the user's reference, with tiny realistic individual grains and a few broad, shallow wind-swept tonal bands. Make it calmer and much less contrasty/coarse than the previous generated version; avoid obvious chunky gravel or orange saturation.
Composition/framing: square directly overhead orthographic material view, consistent grain size from edge to edge, no horizon, no baked perspective, no vignette. Opposite edges should transition smoothly for tiling.
Lighting/mood: soft warm daylight, subtle micro-shadows in the grain, restrained variation.
Color palette: muted pale tan, honey-beige and small cool-grey mineral flecks.
Constraints: sand only, no pebbles larger than grains, rocks, shells, footprints, objects, plants, water, controls, text, logos or watermark.
```

### Moss-rock cutout

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
