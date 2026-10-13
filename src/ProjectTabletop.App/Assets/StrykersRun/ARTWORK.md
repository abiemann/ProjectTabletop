# Stryker's Run artwork

These nine PNGs are original artwork generated for Project Tabletop with the
built-in OpenAI image-generation tool. The four initial images were generated
on 2026-10-12; the Bulwark ally, campaign boss, boss walking, thrown barrel and explosion atlases were added on
2026-10-13. The character
atlases and falling wall were requested with a transparent background; the
road is an opaque material image. They are covered by the project's license.

The verbatim generation and edit prompts for the initial four images were not
retained. [PROMPTS.md](PROMPTS.md) records their clearly labeled reconstructed
specifications and the exact generation and edit prompts for the Bulwark and
campaign boss, boss walking, thrown barrel and explosion atlases. Repeating the prompts does not guarantee reproducing
these files.

## Bundled files

| File | PNG | Use |
| --- | --- | --- |
| `actors.png` | 1254 × 1254, RGBA | Four columns of animation poses in four rows: support soldier seen from behind, approaching runner, brute, and armored boss. |
| `hero.png` | 2172 × 724, RGBA | Four poses of Stryker, the distinct rear-facing navy commando with a crimson scarf. The hero remains separate from the support-soldier atlas. |
| `ally.png` | 2172 × 724, RGBA | Four rear-facing Bulwark poses: left stride, right stride, raised fists, and crouched slam. Teal stone skin and a navy/brass squad harness distinguish the allied guardian from approaching enemies. |
| `bosses.png` | 1536 × 1024, RGBA | Three columns and two rows of distinct campaign boss forms: barrel thrower, commander, lightning zombie, ordinary skin-walker, fungal skin-walker, and the colossal zombie's separated legs. |
| `boss-walk.png` | 1254 × 1254, RGBA | Four rows of four walking poses derived from the first four bosses, for their entrance before combat. |
| `barrels.png` | 1536 × 1024, RGBA | Two columns and two rows of detailed rolling red steel drums, derived from the barrel held by the first campaign boss. |
| `explosion.png` | 1774 × 887, RGBA | Four columns and two rows: ignition, expanding fireball, burning soot and dissipating smoke. |
| `road.png` | 940 × 1672, RGB | Overhead urban asphalt, cracks, lane markings and sidewalk detail. A native shader projects the texture into the road's receding perspective and scrolls it. |
| `game-over-wall.png` | 1434 × 1097, RGBA | Irregular battered concrete-and-brick slab with exposed metal and large GAME OVER graffiti; no rectangular backdrop. |

Character and barrel animation cell boundaries are measured once from alpha coverage when the
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
| `bosses.png` | `08eb2ec7c8199cc27f153682740075141beed0d8e7d828145b9d24fbecb6db0a` |
| `barrels.png` | `4dc58b7631323345c1e2ec3d39ea8827ec8221ccacfe08d16387460334381ed7` |
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

## Campaign boss atlas

`bosses.png` contains six original painted forms, all facing the approaching
player from the same raised camera angle as the existing enemies. Its equal
512 × 512 cells run left to right, then top to bottom: a scorched industrial
barrel thrower; a long-coated undead commander; an electrical worker mutated
by lightning; an ordinary burgundy-shirted skin-walker; that creature's
expanded fungal form; and a giant zombie's two separated lower legs. The
last cell intentionally contains no torso or head. The renderer can animate
each leg independently through the transparent center gap.

The atlas used one built-in generation and two built-in edits. After those
calls, the user explicitly approved a scripted cleanup on 2026-10-13: all
30,560 alpha-1 pixels were set to alpha 0. Comparison against the selected
third output confirms every RGB value and every alpha value 2–255 is
unchanged, with identical dimensions, positions and silhouettes. No
checkerboard, matte, backdrop or cast shadow was added.

The saved RGBA PNG contains 1,572,864 pixels: 1,040,025 have alpha 0 and none
have alpha 1. All outermost rows and columns, all four 32 × 32 corner squares,
the 16-pixel strips centered on both column boundaries and the row boundary,
and a 32-pixel strip through the giant legs' center gap have alpha 0.
Diagnostic compositing against solid black and white confirmed clean
silhouettes without a broad colored backdrop. Colored RGB values underneath
fully transparent pixels do not contribute to the displayed sprites.

For integration, each form is cropped independently within its own cell;
these are different characters, not an animation row that shares a crop.
The following bounds use alpha greater than 8 and exclusive right/bottom
coordinates, relative to the top-left of each 512 × 512 cell:

| Form | Left | Top | Right | Bottom |
| --- | ---: | ---: | ---: | ---: |
| Barrel thrower | 76 | 25 | 475 | 491 |
| Commander | 123 | 51 | 367 | 496 |
| Lightning zombie | 17 | 56 | 466 | 496 |
| Ordinary skin-walker | 82 | 24 | 443 | 463 |
| Fungal skin-walker | 63 | 20 | 433 | 463 |
| Giant legs, paired | 14 | 32 | 481 | 448 |

Within the complete atlas, the left leg spans x=1038–1235, y=546–959 and the
right leg x=1297–1504, y=544–958 at that threshold. These measurements describe
the image only; native scene validation is reported separately from pixel
verification. Existing road and character artwork was not altered.

Final campaign validation used the cleaned atlas in the Debug x64 build. An
isolated native fixture, rerun after the barrel update, passed 28 draw frames
and saved 22 inspection images
covering the six boss forms, attack warnings and impacts, changing health,
skin shedding, level clearance and victory. Pixel checks confirm distinct
forms and preserve the black input-strip region; visual review checked
connected giant-leg geometry through its foreground stomp. All 290 headless
tests passed, including ten steering-only boss victories and victory/retry
controls. No app window or camera was opened for these checks. These native
fixtures supplement the earlier defender verification described above.

## Thrown barrel atlas

`barrels.png` replaces the procedural rectangle and band outlines used for
Barrel Warden projectiles. Four painted red steel drums retain chipped enamel,
scorched metal, dents, raised rims and curved highlights matching his held
barrel. The built-in tool derived the atlas from `bosses.png`, then performed
one transparency-cleanup edit. The held-barrel boss artwork was not modified.

The final image uses the same alpha-only cleanup previously approved by the
user: all 7,675 alpha-1 pixels were set to alpha 0. Every RGB value and all
alpha values 2–255 are unchanged from the second tool output. Pixel
comparison confirms RGBA format, 987,276 fully transparent pixels, no alpha-1
pixels, and alpha 0 throughout the outermost edges, 32 × 32 corner squares
and 32-pixel-wide central gutters. All remaining nontransparent pixels lie
within eight pixels of meaningful barrel coverage; there is no broad backdrop.

The renderer caches the atlas and its four source rectangles per device.
A common scale preserves frame aspect ratios and avoids resizing each pose.
The drum briefly tilts in flight, then cycles rolling poses at six frames per
second, with native fire behind it and small animated embers in front. The
existing throw path, collision rules and three-second fuse are unchanged.
The explosion artwork was replaced in the follow-up described below.
The larger 72-unit body height improves detail readability on the
projected field. Road geometry and the input-strip boundary are unchanged.

The barrel update passed the Debug x64 build with only the existing NU1900
vulnerability-feed warning. The isolated native fixture passed all 28 rendered
frames and saved 22 images, including the new airborne-barrel inspection.
Visual review checked the foreground drum and its airborne appearance against
the unchanged held barrel. All checks kept the app and camera unopened.

## Barrel explosion animation

`explosion.png` replaces the overlapping soft glow circles with eight detailed
fire-and-smoke phases. The built-in tool generated the sequence, then edited
its spacing and transparency. The previously approved alpha-only cleanup
removed 10,879 residual alpha-1 pixels after the third output. Every RGB value
and every other alpha value is unchanged. The final RGBA PNG has 1,002,690
fully transparent pixels, no alpha-1 pixels, and alpha 0 along every outer and
cell edge. All remaining nontransparent pixels are within eight pixels of
meaningful flame or smoke coverage; no background film or checkerboard is used.

SHA-256: `e96a35110cefd3b04bfc60ea2625668112cd2d36563c8113b78911710f643773`.

The renderer caches eight full grid cells to preserve scale between phases,
cross-fades neighboring frames at a common world anchor, and lets the final
smoke dissipate. Bounded sparks and rotating fragments sampled from the painted
barrel accompany the blast. The former orange damage-radius outline is removed.
The effect still uses the existing 0.45-second hazard clock and pauses with
gameplay. It is clipped by the existing playfield boundary above the input strip.
Damage, fuse timing and road projection are unchanged.

The isolated Debug x64 build passed with only the existing NU1900 feed warning.
The camera-free native campaign fixture passed 37 draws and saved 31 images,
including ignition, expanding fire, cooling smoke, both foreground edges and
exact disappearance at expiry. Pixel assertions verify distinct visible phases
and an untouched black input-strip region. Visual inspection checked the flame
and smoke detail at game scale. The running app and webcam were not restarted.

## Boss walking entrance

`boss-walk.png` derives sixteen walking poses from the existing boss designs.
Rows contain the barrel carrier, commander, lightning zombie and ordinary
skin-walker. Three built-in tool calls created the poses and refined spacing
and transparency. The final alpha-only cleanup removed 99,271 alpha-1 pixels
and 308 isolated background pixels (289 at alpha 2, 19 at alpha 3) more than
eight pixels from meaningful coverage. Every RGB value and all other alpha
values remain unchanged. The final RGBA PNG contains 1,212,231 alpha-zero
pixels, with fully transparent outer and cell edges and no checkerboard.

SHA-256: `c653fae8b4784257695572d06768b6bff660eee93426dc46bac243929d865238`.

Walking rows use the existing shared atlas-bound measurement and device cache.
The barrel carrier alternates its forward strides through a neutral passing
pose. A short blend settles each walker into its original combat artwork.
The giant uses the existing separated-leg artwork, with alternating lifted
steps, perspective growth and grounded shadows. All bosses enter from behind
the HUD boundary over three active seconds. Their movement pauses with input;
attacks and damage wait for arrival. No road artwork or geometry was changed.

The final Debug x64 build passed with the existing NU1900 feed warning.
The camera-free native campaign fixture passed 77 draws and saved 51 images,
including four entrance positions for each boss. Pixel checks verify downward
movement, no initial body below the HUD, stationary road pixels and an untouched
input strip. Visual review checked the first boss's shorter HUD boundary and
the giant's alternating foot placement. The complete headless Interaction
verifier passed, including all five entrance regressions and ten steering-only
boss victories. All 300 tests passed through the xUnit in-process runner.
The running app and webcam were not restarted.
