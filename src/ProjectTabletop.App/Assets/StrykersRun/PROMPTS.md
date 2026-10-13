# Stryker's Run artwork prompts and specifications

These are reconstructed specifications for the original assets generated on
2026-10-12. **The verbatim image-generation tool prompts were not retained.**
This document is a reproducible design brief, not an exact generation log.
The final selected dimensions and SHA-256 hashes are recorded in
[ARTWORK.md](ARTWORK.md).

The Bulwark section records the exact built-in generation and edit prompts
used for the new ally atlas on 2026-10-13.

## Support soldiers and monsters

Create original premium hand-painted arcade game artwork for Stryker's Run.
Use mature anatomy, detailed cloth and armor, strong readable silhouettes,
and a consistent raised camera angle suitable for an above-and-behind road
shooter. Arrange a transparent sprite atlas in four columns and four rows.
Every row depicts four clearly different running/action poses of one character
at consistent scale, without overlapping neighboring cells:

1. A support soldier facing away from the viewer, running and firing up the road.
2. An approaching runner monster facing toward the viewer.
3. A larger, heavily built brute with warm orange details, approaching the viewer.
4. A massive armored boss approaching the viewer.

Keep heads, weapons, arms and feet fully inside their cells. Leave clear
transparent gutters and enough space around natural silhouettes. Avoid text,
logos, user-interface elements, bounding boxes, ground planes and exterior
cast shadows. Produce genuine RGBA transparency with alpha-zero background,
including enclosed gaps; never paint a checkerboard or matte.

## Main protagonist

Create original premium hand-painted arcade animation artwork for Stryker,
the central protagonist of Stryker's Run. Show a distinctive commando from
above and behind, facing and aiming away up the road, with dark navy tactical
clothing, a visible crimson scarf and restrained amber equipment details.
Use a confident, recognizable silhouette that remains distinct from support
soldiers. Arrange four sequential running/firing body poses in one horizontal
row, maintaining the same camera, scale and overall design across frames.
Preserve full anatomy and equipment with clear gutters and transparent margins.
No captions, logos, floor, backdrop, exterior shadow or checkerboard. Deliver
real RGBA transparency.

The subsequent edit removed the baked muzzle flash from the selected hero
atlas while retaining the character, weapon, poses and transparent silhouette.
The app draws flashes only when the simulation fires.

## Road material

Create an original opaque portrait urban street texture for Stryker's Run.
Show the road from directly overhead so the renderer can apply its own
perspective. Use richly painted weathered dark asphalt, irregular cracks,
yellow center lines, worn white lane stripes and narrow sidewalk details
along the sides. Keep the travel corridor readable and uncluttered. Use
coherent arcade lighting and detailed material texture, without characters,
UI, words, logos or branding. The image fills the entire frame with no border
or transparency. Its surface will scroll and be mapped into a trapezoid.

## Falling Game Over wall

Create an original cutout asset for a dramatic Stryker's Run defeat sequence:
a battered concrete-and-brick slab with jagged broken edges, exposed metal,
cracks and detailed rough material. Paint large readable GAME OVER graffiti
across its face in a bold white-and-red treatment. Preserve an irregular
physical silhouette with transparent openings and a small transparent margin.
No surrounding rectangular plate, floor, background scene, exterior shadow,
UI controls or extra text. Deliver genuine RGBA transparency with alpha-zero
background and clean antialiased edges, never a checkerboard or matte.

The renderer supplies the fall, impact, debris and smoke, and protects the
stationary input strip below the playfield.

## Final file preparation

Generation and hero editing used the image-generation tool. After generation,
the three cutouts had residual alpha-1 background pixels normalized to alpha
0; all alpha values 2–255 were retained. This cleanup did not replace any
artwork with a drawn background. The renderer determines atlas source bounds
from alpha coverage at load time and scales sprites uniformly at runtime.

## Bulwark ally: exact prompts, 2026-10-13

All three calls used the built-in image-generation tool with
`transparent_background: true`. The first call used no source image. The
second edited the first result, and the third edited the second result.
After the third output, the user approved a scripted alpha-only cleanup that
set 14,243 residual alpha-1 pixels to alpha 0. Every RGB value and all other
alpha values were preserved. Verification and the final checksum are recorded
in [ARTWORK.md](ARTWORK.md).

### Initial generation

```text
Use case: stylized-concept.
Asset type: original production-ready transparent animation sprite atlas for Stryker's Run, an above-and-behind arcade road shooter.
Create Bulwark, a powerful allied monster guardian facing AWAY from the viewer, always looking and moving toward the top of the image. Camera is elevated directly behind the creature, showing its upper back, shoulders, crown of head, forearms, legs and feet, not its face. It is a hulking muscular organic stone-skinned guardian, desaturated teal hide and overlapping natural mineral plates, enormous hunched shoulders, heavy long gauntlets, thick legs, and a simple weathered navy squad harness with restrained brass hardware. Its back and shoulder outline should make it immediately recognizable as a protective ally. Mature premium hand-painted arcade illustration, richly textured physical materials, crisp natural silhouettes, carefully shaded anatomy, cinematic but readable highlights. No cute cartoon, no low-poly rendering.
Exactly FOUR full-body poses of THE SAME monster in ONE horizontal row, evenly spaced four-column atlas. Left to right: (1) left-foot forward heavy walking stride, (2) right-foot forward heavy walking stride, (3) both fists raised overhead ready to pound forward, (4) crouched forward with both giant fists lowered in a ground-strike follow-through. All four poses face away/up-screen. Maintain identical anatomy, armor, scale and elevated rear camera in all cells. Align feet to a common baseline. Each body stays fully contained inside its separate cell, with generous empty transparent gutters and margins, no overlap, no cut-off hands or feet. Frame 3 must leave room for raised hands. Wide 3:1 canvas.
Background: genuine RGBA transparency with alpha exactly 0 everywhere outside each creature, including gaps between limbs. Clean antialiased edges. Never draw, render or bake a checkerboard, matte, floor or backdrop. No cast ground shadows, no dust, no impact ring, no spark effects, no glow surrounding the character, no border, no captions, no writing, no logo, no UI. The app supplies all ground shadows and attack effects at runtime.
```

### Atlas spacing edit

```text
Use case: precise-object-edit.
Image 1 is the edit target: the four-pose Bulwark allied monster sprite atlas.
Keep the exact creature design, rich painted arcade style, desaturated teal stone hide, navy/brass harness, all four rear-facing poses in the same order, above-and-behind camera, anatomy, colors and lighting. Change only atlas spacing and alpha cleanliness.
Give each pose a generous clear transparent margin inside its own equal-width cell. Slightly reduce all four creatures uniformly to about 82% of their present scale, re-center each inside its quarter of the wide canvas, and align their feet on a consistent lower baseline. Preserve full raised arms and feet. Four columns, one row, no other artwork.
Crucially, deliver actual RGBA with alpha exactly ZERO throughout all exterior background, outer edges, four corners, generous vertical cell gutters, and gaps between limbs. The source contains faint nearly transparent alpha-1 residual outside the bodies; eliminate that residue. Keep clean antialiased edges only immediately at creature silhouettes. No checkerboard pixels, no painted backdrop, no new shadow, no floor, no matte, no text, no impact effect.
```

### Transparent-background cleanup edit

```text
Use case: background-extraction.
Image 1 is the edit target, a four-pose Bulwark sprite atlas. Make only a transparent-background cleanup. Preserve the exact artwork, all four existing rear-facing poses, every body silhouette, spacing, scale, 2172x724 canvas, materials, navy/brass harness, desaturated teal stone coloring, and lighting. Do not redraw or move the creatures.
The actual source RGBA has faint nearly invisible alpha=1 residue in otherwise empty areas. Remove all that residue: all pixels with alpha 1 must become alpha 0. Every empty background area, vertical gutter, corner, outside border, and enclosed gap must be fully transparent alpha 0. Preserve opaque body pixels and meaningful antialiased silhouette edges. This is a technical cutout cleanup, no additional artwork. No checkerboard, backdrop, floor, ground shadow, matte, text, effect, cropping, recoloring, or pose change. Return a clean RGBA PNG with real transparency.
```
