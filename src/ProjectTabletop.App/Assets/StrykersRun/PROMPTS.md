# Stryker's Run reconstructed artwork specifications

These are reconstructed specifications for the original assets generated on
2026-10-12. **The verbatim image-generation tool prompts were not retained.**
This document is a reproducible design brief, not an exact generation log.
The final selected dimensions and SHA-256 hashes are recorded in
[ARTWORK.md](ARTWORK.md).

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
