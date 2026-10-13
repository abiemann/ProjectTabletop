# Stryker's Run artwork

These four PNGs are original artwork generated for Project Tabletop on
2026-10-12 with the built-in OpenAI image-generation tool. The character atlases
and falling wall were requested with a transparent background; the road is an
opaque material image. They are covered by the project's license.

The verbatim generation and edit prompts were not retained. [PROMPTS.md](PROMPTS.md)
records a clearly labeled reconstructed specification set, not an exact tool
transcript. It describes the intended asset composition and subsequent hero
edit without claiming that reproducing those words will reproduce these files.

## Bundled files

| File | PNG | Use |
| --- | --- | --- |
| `actors.png` | 1254 × 1254, RGBA | Four columns of animation poses in four rows: support soldier seen from behind, approaching runner, brute, and armored boss. |
| `hero.png` | 2172 × 724, RGBA | Four poses of Stryker, the distinct rear-facing navy commando with a crimson scarf. The hero remains separate from the support-soldier atlas. |
| `road.png` | 940 × 1672, RGB | Overhead urban asphalt, cracks, lane markings and sidewalk detail. A native shader projects the texture into the road's receding perspective and scrolls it. |
| `game-over-wall.png` | 1434 × 1097, RGBA | Irregular battered concrete-and-brick slab with exposed metal and large GAME OVER graffiti; no rectangular backdrop. |

Animation cell boundaries are measured once from alpha coverage when the
device-owned images load. The generated atlases do not have perfectly uniform
cell dimensions. Each animation row shares a vertical crop to preserve scale
through its poses; the renderer preserves every sprite's aspect ratio. This
analysis selects source rectangles and does not resample the PNGs. Stryker is
larger than the surrounding troops and stays at the center front of their
formation. The separate hero image was edited with the image-generation tool
to remove its baked muzzle flash; firing flashes are runtime effects.

The three cutout files received one final alpha cleanup: pixels with alpha 1
were set to alpha 0, with alpha values 2–255 preserved. No checkerboard, matte,
exterior shadow or painted replacement background was added. Actual PNG
inspection after cleanup confirmed RGBA format, alpha 0 at all four corners,
and zero remaining alpha-1 pixels in each cutout. The RGB road is intentionally
opaque. The measurements and hashes below describe the checked-in files, not
the image viewer's transparency display.

| File | SHA-256 |
| --- | --- |
| `actors.png` | `268135df79e27ce769137f8f665c270d5707988ea47724bf464b13e2cf54ad61` |
| `hero.png` | `58026b001cc939eff51892475da96103de92b809467d50925965a3cf1463f907` |
| `road.png` | `6eafde660894d2e1042bfdeb52b452b4def08f1afb52eb09e5cf9343156026f8` |
| `game-over-wall.png` | `9dac98688c8e344f7126321c42e03e05208efe7b45b551104770ebaaa78caa71` |

## Native composition

The renderer uses one above-and-behind camera projection for the road,
characters, gates, bullets, shadows and hit effects. Near objects appear larger
and the road narrows toward the distant city. Character sprites draw in world
depth order. Original procedural buildings, window lights, fire, smoke, muzzle
flashes, bullet trails, bloodless hit sparks and wall debris are layered at
runtime; they are not additional downloaded artwork or baked into the atlases.
Small particle textures are generated once per graphics device and batched.

Initially, EXIT and PLAY are visible and the dot rail is hidden. PLAY fades
the stationary buttons out while the rail slides up from below the board over
0.45 seconds. The white marks are centered inside the black rail. On defeat,
the rail slides out during the first 0.45 seconds as the wall falls rapidly
into the foreground and settles with debris. EXIT and PLAY fade back in at
fixed positions from 2.7 to 3.15 seconds while the wall remains visible.
Scenery and effects stop above the physical input strip. The menu thumbnail
reuses these same PNGs in a cached native composition without running the game
simulation.

Asset dimensions, hashes and the stated alpha checks are verified. The last
completed native GPU verification passed 104 rendered frames and produced 13
inspection images. It includes the portrait facing, scene composition, wall
effect, protected input strip, shared button exposure protection and latest
stationary button fades, rail movement and centered marks. That verification
and the repeated camera safety and input-scheduling checks left camera hardware
unopened. Live testing confirmed PLAY and steering, and the user confirmed the
transition and centering look correct. Lift/resume remains tentative, and
continuing camera stalls leave physical acceptance incomplete. The PNGs have
not changed for these input and control updates. File checks and offscreen
rendering do not establish real-camera reliability or a displayed frame rate.
