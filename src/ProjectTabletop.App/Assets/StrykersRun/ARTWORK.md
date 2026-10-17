# Stryker's Run artwork

These PNGs are original artwork generated for Project Tabletop with the
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
| `actors.png` | 1254 Ã— 1254, RGBA | Four columns of animation poses in four rows: support soldier seen from behind, approaching runner, brute, and armored boss. |
| `hero.png` | 2172 Ã— 724, RGBA | Original four-pose Stryker atlas, retained as an artwork reference. Runtime rendering uses the independent body and scarf below. |
| `hero-body.png` | 2172 Ã— 724, RGBA | Four rear-facing walking poses retaining Stryker's navy uniform, brass hardware, rifle and red neck wrap, with the loose scarf tail removed. |
| `hero-scarf.png` | 2048 Ã— 768, RGBA | Four left-trailing crimson cloth poses, independently attached at the neck and reflected when lateral travel changes direction. |
| `ally.png` | 2172 Ã— 724, RGBA | Four rear-facing Bulwark poses: left stride, right stride, raised fists, and crouched slam. Teal stone skin and a navy/brass squad harness distinguish the allied guardian from approaching enemies. |
| `bosses.png` | 1536 Ã— 1024, RGBA | Three columns and two rows of distinct campaign boss forms: barrel thrower, commander, lightning zombie, ordinary skin-walker, fungal skin-walker, and the colossal zombie's separated legs. |
| `boss-walk.png` | 1254 Ã— 1254, RGBA | Four rows of four walking poses derived from the first four bosses, for their entrance before combat. |
| `barrels.png` | 1536 Ã— 1024, RGBA | Two columns and two rows of detailed rolling red steel drums, derived from the barrel held by the first campaign boss. |
| `explosion.png` | 1774 Ã— 887, RGBA | Four columns and two rows: ignition, expanding fireball, burning soot and dissipating smoke. |
| `road.png` | 940 Ã— 1672, RGB | Overhead urban asphalt, cracks, lane markings and sidewalk detail. A native shader projects the texture into the road's receding perspective and scrolls it. |
| `game-over-wall.png` | 1434 Ã— 1097, RGBA | Irregular battered concrete-and-brick slab with exposed metal and large GAME OVER graffiti; no rectangular backdrop. |

Character and barrel animation cell boundaries are measured once from alpha coverage when the
device-owned images load. The generated atlases do not have perfectly uniform
cell dimensions. Each animation row shares a vertical crop to preserve scale
through its poses; the renderer preserves every sprite's aspect ratio. This
analysis selects source rectangles and does not resample the PNGs. Stryker is
larger than the surrounding troops and stays at the center front of their
formation. The separate hero image was edited with the image-generation tool
to remove its baked muzzle flash; firing flashes are runtime effects.

The three initial cutout files received one final alpha cleanup: pixels with alpha 1
were set to alpha 0, with alpha values 2â€“255 preserved. No checkerboard, matte,
exterior shadow or painted replacement background was added. Actual PNG
inspection after cleanup confirmed RGBA format, alpha 0 at all four corners,
and zero remaining alpha-1 pixels in each cutout. The RGB road is intentionally
opaque. The measurements and hashes below describe the checked-in files, not
the image viewer's transparency display.

The new `ally.png` used one original generation followed by a spacing edit and
a background-cleanup edit in the built-in tool. On 2026-10-13, the user approved
a final scripted alpha cleanup of that third output: all 14,243 alpha-1 pixels
were set to alpha 0. Pixel comparison confirms that every RGB value and all
alpha values 2â€“255 are unchanged, as are the dimensions and poses. No
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
512 Ã— 512 cells run left to right, then top to bottom: a scorched industrial
barrel thrower; a long-coated undead commander; an electrical worker mutated
by lightning; an ordinary burgundy-shirted skin-walker; that creature's
expanded fungal form; and a giant zombie's two separated lower legs. The
last cell intentionally contains no torso or head. The renderer can animate
each leg independently through the transparent center gap.

The atlas used one built-in generation and two built-in edits. After those
calls, the user explicitly approved a scripted cleanup on 2026-10-13: all
30,560 alpha-1 pixels were set to alpha 0. Comparison against the selected
third output confirms every RGB value and every alpha value 2â€“255 is
unchanged, with identical dimensions, positions and silhouettes. No
checkerboard, matte, backdrop or cast shadow was added.

The saved RGBA PNG contains 1,572,864 pixels: 1,040,025 have alpha 0 and none
have alpha 1. All outermost rows and columns, all four 32 Ã— 32 corner squares,
the 16-pixel strips centered on both column boundaries and the row boundary,
and a 32-pixel strip through the giant legs' center gap have alpha 0.
Diagnostic compositing against solid black and white confirmed clean
silhouettes without a broad colored backdrop. Colored RGB values underneath
fully transparent pixels do not contribute to the displayed sprites.

For integration, each form is cropped independently within its own cell;
these are different characters, not an animation row that shares a crop.
The following bounds use alpha greater than 8 and exclusive right/bottom
coordinates, relative to the top-left of each 512 Ã— 512 cell:

| Form | Left | Top | Right | Bottom |
| --- | ---: | ---: | ---: | ---: |
| Barrel thrower | 76 | 25 | 475 | 491 |
| Commander | 123 | 51 | 367 | 496 |
| Lightning zombie | 17 | 56 | 466 | 496 |
| Ordinary skin-walker | 82 | 24 | 443 | 463 |
| Fungal skin-walker | 63 | 20 | 433 | 463 |
| Giant legs, paired | 14 | 32 | 481 | 448 |

Within the complete atlas, the left leg spans x=1038â€“1235, y=546â€“959 and the
right leg x=1297â€“1504, y=544â€“958 at that threshold. These measurements describe
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
alpha values 2â€“255 are unchanged from the second tool output. Pixel
comparison confirms RGBA format, 987,276 fully transparent pixels, no alpha-1
pixels, and alpha 0 throughout the outermost edges, 32 Ã— 32 corner squares
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

## Articulated barrel throw and travelling flames

`boss-throw.png` adds eight painted Barrel Warden poses derived from the
existing boss and walking artwork. The sequence grips, hoists and swings the
barrel, releases with empty hands, follows through and returns to carrying
another barrel. It retains the bald gray zombie, mustard workwear, boots and
detailed red steel drum. The renderer measures each pose against a common
body scale and fixed boot anchor rather than shrinking raised arms to fit.
The last held barrel's center and height anchor the projectile at release.
The original boss and walking atlases remain unchanged.

`barrel-flames.png` provides eight turbulent fire frames derived from the
painted barrel's style. These replace the elongated glow sprites with forked
flame sheets, hot cores and irregular orange edges. Neighboring fire frames
blend at a fixed base behind the drum; smaller independently phased curls
overlap its rims, leaving the metal readable. Six bounded embers accompany
the flames. Cached textures and frame rectangles avoid per-frame image reads
or new managed collections. The flame and throw clocks pause with gameplay.

Both sheets used three built-in image-generation calls with real transparency
requested. After the tool retained nearly invisible background residue, the
user explicitly authorized setting only alpha-1 pixels to zero. Exact pixel
comparison verified that every RGB value and every other alpha value was
preserved. Both final files are RGBA PNGs with fully transparent outer edges,
corners and cell gutters, no alpha-1 residue and no baked checkerboard.

| Asset | Dimensions / layout | Alpha-1 pixels cleared | SHA-256 |
| --- | --- | ---: | --- |
| `boss-throw.png` | 1774 Ã— 887; 4 columns Ã— 2 rows | 27,428 | `a81ac1877a2d88ad868ea2d14ece78089143aee409a6a1d2d33deeb1176a1d75` |
| `barrel-flames.png` | 1536 Ã— 1024; 4 columns Ã— 2 rows | 103,433 | `cea4e57c1fab6373aed2ef7fa6b8df649b40cc4a5d8d29ad1b1de7bba6b42833` |

The throwing sequence uses 0.55 active seconds before release and 0.45 after.
The existing first attack, repeat interval, locked lane, damage and three-second
fuse are unchanged. The short airborne path now starts at the held barrel.
Road projection, boss entrance, explosion artwork, HUD and dotted input strip
are preserved. Exact generation and edit prompts appear in [PROMPTS.md](PROMPTS.md).

The final Debug x64 build passed with zero warnings and errors. All four
focused Stryker's Run test groups passed, including tick-by-tick throw timing,
release-lane locking and windup/recovery pauses. The camera-free native fixture
passed 102 draws and saved 72 inspection images. Its checks cover distinct
throw poses, lane-independent release from the hands, evolving flames, paused
attack pixels, all five boss entrances and protected HUD/input regions. Visual
review matched the last held barrel to the released projectile and confirmed
compact fire at release grows over 0.25 seconds without covering the face.
A separate four-second native preview contains 120 frames at a 30 fps capture
cadence; this is an animation inspection, not a displayed-frame-rate measurement.
The running app and webcam were left untouched; live acceptance remains pending.

## Storm encounter artwork (2026-10-15)

Three original PNG atlases generated with the built-in image tool extend the existing painted arcade style:

- [storm-cloud.png](storm-cloud.png): eight billowing, internally lit cloud frames in a 4 by 2 grid.
- [electric-spider.png](electric-spider.png): eight articulated walking frames in a 4 by 2 grid. Measured eye anchors register the body across rows while preserving the moving legs.
- [storm-craters.png](storm-craters.png): four broken-asphalt variants in a 2 by 2 grid, with blue charge in the cracks. Each defeated spider keeps one variant; animated smoke reuses the late frames of `explosion.png` and bounded native arcs animate the rim.

All are real RGBA PNGs with verified alpha-zero canvas borders and grid dividers, without a baked checkerboard. After three tool attempts per asset, the user explicitly authorized background-only cleanup. Every RGB value and every alpha value outside the stated residue mask remain unchanged. Cloud/crater cleanup preserves a four-pixel buffer around alpha >= 16; spider cleanup preserves a four-pixel buffer around alpha >= 32. This retains delicate edge antialiasing while clearing detached faint background residue. [Exact generation and edit prompts](PROMPTS.md#storm-clouds-electric-spiders-and-persistent-craters-2026-10-15).

| Asset | Pixels | Cleared background pixels | Maximum cleared alpha | SHA-256 |
| --- | --- | --- | --- | --- |
| `storm-cloud.png` | 1774 x 887 | 47,646 | 3 | `477151340b2da70e398efe52444cc812c9b812b6f2919060e960698d640104bb` |
| `electric-spider.png` | 1536 x 1024 | 128,352 | 21 | `53ba18fbaa7a0b3dfc58a583e7e48264c2418d45632187951f972dc0426316fe` |
| `storm-craters.png` | 1254 x 1254 | 10,803 | 9 | `a757c9302a3da7651163b803d5ce2aa0cba5cda6e71631958662cd86d314a0b8` |

Textures and frame rectangles are cached per graphics device and disposed with the shared image set. Crater instances contain only position, identity, creation time and road distance; they persist through the battle/intermission, scroll with resumed travel and are culled after the full smoke footprint leaves the field. Restart and board exit clear them. Cloud, spider, return-lightning and crater animation use active simulation time and freeze on input loss.

Validation: clean Debug x64 build; all 356 headless tests; 191 camera-free native draws with 97 saved images. The final render includes more visible crater smoke without changing its geometry. Checks cover eight spider poses and HP, cloud lanes and rain, returned bolts and impacts, hidden boss hints and lower HUD padding, persistent/scrolling/paused craters, exact offscreen smoke bounds for four variants across 12 phases, and protected rail pixels. A separate nine-second scripted native animation uses 270 frames at a 30 fps capture cadence. These are offline visuals and simulation checks, not displayed performance or live tabletop acceptance.


## Independent hero scarf (2026-10-16)

`hero-body.png` derives four walking poses from the original `hero.png`.
The edit retains the rear-facing commando, navy uniform, brass hardware,
rifle, backpack and red neck wrap while removing the loose flowing tail.
`hero-scarf.png` supplies four painted crimson cloth poses trailing toward
screen-left. The separate attachment lets the scarf change sides without
mirroring Stryker's body or weapon. The original atlas remains an artwork
reference; runtime rendering and the menu thumbnail use the new assets.

Both atlases used three built-in image-generation calls. The user explicitly
approved the final scripted alpha-only cleanup: 11,522 body pixels and
8,497 scarf pixels changed from alpha 1 to alpha 0. Pixel comparison against
the selected tool outputs confirms every RGB value and every other alpha
value is unchanged. Dimensions, poses, colors and cloth-edge antialiasing
are preserved. [Exact generation and edit prompts](PROMPTS.md#independent-hero-scarf-2026-10-16).

| Asset | Layout | Fully transparent pixels | SHA-256 |
| --- | --- | ---: | --- |
| `hero-body.png` | Four 543 Ã— 724 cells in one row | 1,114,326 | `5ed46a86c97dc12cb184fdab06b47725770ca99f76a0dd7eea712cd882c9a987` |
| `hero-scarf.png` | Four 512 Ã— 768 cells in one row | 1,416,776 | `f9de16004ab6ae9940f97c46dc9cb812723d96c110500164021c042b18ade2f8` |

Independent inspection confirms RGBA format, no alpha-1 pixels, and alpha 0
throughout the outer canvas edges and 16-pixel-wide column gutters. Every
remaining nontransparent pixel lies within an eight-pixel square
neighborhood of alpha at least 32. No checkerboard, exterior shadow, matte
or visible rectangular background was added. Colored RGB values beneath
alpha 0, including red around the scarf, remain invisible.

The renderer retains its existing measured body crops and common vertical
scale. It uses full scarf cells and the following measured attachment points;
body coordinates are absolute within the atlas, while scarf coordinates are
local to each 512 Ã— 768 cell.

| Frame | Body neck (x, y) | Scarf root (x, y) |
| --- | --- | --- |
| 0 | (321, 173) | (473, 324) |
| 1 | (835, 172) | (469, 323) |
| 2 | (1319, 174) | (469, 323) |
| 3 | (1832, 173) | (468, 323) |

Cloth scale is 0.55 times the body's source-to-screen scale. Drawing the
cloth behind the body hides its attachment under the neck wrap. The renderer
reflects only the cloth, briefly turning it edge-on during a side change.
Forward travel retains the last side; lateral travel sends the tail opposite
the direction of movement. Textures, frame rectangles and attachment points
are cached, with no new image reads or managed collections per frame.

These dimensions, hashes, alpha checks and registration values are verified.
Final native checks passed for all four scarf poses on both sides and during
the edge-on transition, while confirming the hero body remains unmirrored.
Visual inspection of the completed scenes also passed. The user confirmed the
scarf direction and city scrolling on the restored tabletop session on
2026-10-16; shared build and campaign results follow below.

## Devastated-city parallax backdrop

`city-ruins.png` is an opaque 948 x 1659 RGB image generated with the built-in
image tool on 2026-10-16, followed by one tool edit to replace cut buildings at
the top/bottom boundaries with rubble. It shows ruined concrete and brick
buildings, exposed floors, broken roofs, debris and small fires. No procedural
window-grid buildings remain. Exact generation and edit prompts are in
[PROMPTS.md](PROMPTS.md#devastated-city-parallax-backdrop-2026-10-16).

SHA-256: `fa3396a98b8a90d605be2b1ead8054cc1bff5bac42e07c3e7ad95021954326a7`.
The selected image is opaque by design; no pixel cleanup was performed.

A cached GPU shader repeats the city vertically without flipping its buildings.
It uses 22% of the road texture travel, driven by the same interpolated distance,
so boss fights and lost-input pauses stop both layers together. The original
road image, road shader and projection calculations are unchanged. The new
background loads once per device, with no per-frame CPU texture copies.
The shader reduces only the city's RGB brightness by 35% so the road remains
the visual focus, preserving the artwork's alpha and all foreground brightness.
The city edit improves the join but is not pixel-perfect seamless. Final native
rendering and visual inspection covered the repeated boundary in the scene.
The dedicated scenery fixture passed the 22% travel check, exact texture-repeat
check, paused-layer check and scarf-side checks across all four walking poses.
The campaign fixture passed 191 rendered frames, including protected input-rail
pixels and the existing boss attacks and effects. All 452 offline tests passed,
and the app builds completed with zero warnings and errors. The user confirmed
the city scrolling and scarf direction on the calibrated tabletop with the
Facecam running at 1080p/60 on 2026-10-16.

The broader in-app verifier still reported an exact-pixel crater-culling
comparison failure. An isolated native check of all 48 cutoff variants/phases,
a repeated baseline and a beyond-cutoff frame passed all 50 comparisons with
zero changed pixels. The in-app failure is not reproduced or resolved; capture
its exact failing frame before changing that assertion. No crater tolerance
was relaxed.
