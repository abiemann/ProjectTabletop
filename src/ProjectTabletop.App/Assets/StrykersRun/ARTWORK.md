# Stryker's Run artwork

These five PNGs are original artwork generated for Project Tabletop with the
built-in OpenAI image-generation tool. The four initial images were generated
on 2026-10-12; the Bulwark ally atlas was added on 2026-10-13. The character
atlases and falling wall were requested with a transparent background; the
road is an opaque material image. They are covered by the project's license.

The verbatim generation and edit prompts for the initial four images were not
retained. [PROMPTS.md](PROMPTS.md) records their clearly labeled reconstructed
specifications and the exact generation and edit prompts for the new Bulwark
atlas. Repeating the prompts does not guarantee reproducing these files.

## Bundled files

| File | PNG | Use |
| --- | --- | --- |
| `actors.png` | 1254 × 1254, RGBA | Four columns of animation poses in four rows: support soldier seen from behind, approaching runner, brute, and armored boss. |
| `hero.png` | 2172 × 724, RGBA | Four poses of Stryker, the distinct rear-facing navy commando with a crimson scarf. The hero remains separate from the support-soldier atlas. |
| `ally.png` | 2172 × 724, RGBA | Four rear-facing Bulwark poses: left stride, right stride, raised fists, and crouched slam. Teal stone skin and a navy/brass squad harness distinguish the allied guardian from approaching enemies. |
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

The three initial cutout files received one final alpha cleanup: pixels with alpha 1
were set to alpha 0, with alpha values 2–255 preserved. No checkerboard, matte,
exterior shadow or painted replacement background was added. Actual PNG
inspection after cleanup confirmed RGBA format, alpha 0 at all four corners,
and zero remaining alpha-1 pixels in each cutout. The RGB road is intentionally
opaque. The measurements and hashes below describe the checked-in files, not
the image viewer's transparency display.

The new `ally.png` used one original generation followed by a spacing edit and
a background-cleanup edit in the built-in tool. On 2026-10-13, the user approved
a final scripted alpha cleanup of that third output: all 14,243 alpha-1 pixels
were set to alpha 0. Pixel comparison confirms that every RGB value and all
alpha values 2–255 are unchanged, as are the dimensions and poses. No
checkerboard, matte, exterior shadow or replacement background was added.

The saved RGBA PNG has 1,572,528 pixels: 1,091,347 have alpha 0 and none retain
alpha 1. All four corners, all outermost pixel rows/columns, and the 32-pixel
strips centered on each of the three cell boundaries have alpha 0. Independent
pixel verification confirmed these measurements. The renderer uses the existing
alpha-coverage cell analysis for the four poses; above alpha 8 the shared
vertical extent remains y=72 through 651, preserving the raised arms and common
foot baseline.

| File | SHA-256 |
| --- | --- |
| `actors.png` | `268135df79e27ce769137f8f665c270d5707988ea47724bf464b13e2cf54ad61` |
| `hero.png` | `58026b001cc939eff51892475da96103de92b809467d50925965a3cf1463f907` |
| `ally.png` | `605756268ada90c8294c25b9cae26529759605a4e78f04f4e5f86cc9266148a2` |
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

Asset dimensions, hashes and the stated alpha checks are verified. Before the
final alpha-only cleanup, native GPU verification of the defender update passed
119 rendered frames and produced
21 inspection images. Eight new images cover intact and damaged pods, full
and reduced defender HP, a ground slam, ally defeat with Stryker still alive,
and sealed/opened reinforcement caches. Existing portrait, controls, input
strip, shared exposure and game-over checks also passed. The complete offline
suite passed 289 tests. These checks left camera hardware unopened.

The road image, road shader, street drawing and projection calculations are
unchanged. Earlier live testing confirmed PLAY and steering, and the user
confirmed the transition and mark centering. Lift/resume remains tentative;
continuing camera stalls leave physical acceptance incomplete. File checks
and offscreen rendering do not establish real-camera reliability or a
displayed frame rate.
