# Stryker's Run artwork prompts and specifications

These are reconstructed specifications for the original assets generated on
2026-10-12. **The verbatim image-generation tool prompts were not retained.**
This document is a reproducible design brief, not an exact generation log.
The final selected dimensions and SHA-256 hashes are recorded in
[ARTWORK.md](ARTWORK.md).

The Bulwark and campaign boss sections record the exact built-in generation
and edit prompts used for their new atlases on 2026-10-13.

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

## Campaign bosses: exact prompts, 2026-10-13

All three calls used the built-in image-generation tool with
`transparent_background: true`. The first generated a new image without source
images; the second edited that result, and the third edited the second result.
The atlas contains six forms in a three-column, two-row grid: barrel thrower,
commander, lightning zombie, ordinary skin-walker, transformed fungal
skin-walker, and the colossal boss's separated lower legs.

After the third output, the user approved a scripted alpha-only cleanup.
All 30,560 residual alpha-1 pixels were set to alpha 0. Every RGB value and
all alpha values 2–255 were preserved, along with the dimensions and poses.
The verified measurements and final checksum are in [ARTWORK.md](ARTWORK.md).

### Initial generation

```text
Use case: stylized-concept.
Asset type: original production-quality boss character sprite atlas for Stryker's Run, a mature arcade road shooter. One atlas image, exactly THREE COLUMNS and TWO ROWS of equal-size cells, with exactly one isolated boss form in each cell. Premium hand-painted arcade illustration with bold natural silhouettes, detailed textured cloth, metal and organic anatomy, strong modeled volume, dark outlines, crisp readable highlights and mature proportions. Elevated frontal camera looking downward about 25 degrees: every enemy faces the viewer and approaches down-screen toward the player. No side views, no low-poly, no cute cartoon. The top five figures are full-body and must fit wholly in their own cells. Generous empty transparent gutters at least 40 pixels around every cell; nothing overlaps cell boundaries. Consistent perspective across all six cells. Large landscape 3:2 image, about 1536 by 1024 or larger.
Top-left cell: a very broad, hunched, ash-gray zombie barrel thrower in scorched mustard industrial work overalls and heavy boots, bald burned-looking head without gore, powerful arms gripping a dented rusty fuel barrel against one shoulder, small natural orange tongues of flame at the barrel opening. Aggressive heavy stance.
Top-middle cell: a lean commanding undead officer in a tattered dark olive long coat, worn peaked cap, chest radio and harness, one gloved arm pointing forward to order a charge. Distinct cold pale face and narrow upright silhouette, full body and boots.
Top-right cell: a tall gaunt lightning zombie wearing a torn slate-blue electrical-worker suit, thick electrical insulators and coiled cables across shoulders and forearms, luminous blue-white fissures on skin and a few small electrical arcs close to its hands. Arms slightly spread preparing to channel lightning. Full body and heavy work boots, clear narrow silhouette.
Bottom-left cell: the first form of a skin-walker, outwardly an ordinary emaciated gray zombie in a ragged faded burgundy shirt and brown trousers. Forward hunch, creepy long arms, mouth open with a small visible purple tongue coiled near chin (the game draws the long tongue attack separately). Full body. It must look different from the other four upper-body bosses.
Bottom-middle cell: the transformed fungal form of that same skin-walker. Same base pose and remnants of burgundy shirt/brown trousers, but expanded asymmetrical mushroom shelves and pale orange fungi sprout over head, shoulders and arms, mottled muted green skin with olive spore sacs. Bloodless transformation, no wounds or exposed organs. Full body. Clearly different silhouette from its ordinary form; no surrounding gas cloud because the game animates it.
Bottom-right cell: ONLY a pair of enormous zombie lower legs, from slightly above knees down to giant bare clawed feet. No torso, hips, head or upper body. Gray-brown rough hide, heavy natural thick ankles, tattered dark trouser remnants at the top, amber fissures in stone-like toenails, feet pointing toward the camera. Two legs separated by a wide fully transparent gap so the app can animate them independently. Include full foot silhouettes. The paired legs fill their own cell at large scale and nothing else enters it.
Background and cutouts: genuine RGBA PNG transparency with alpha exactly ZERO in every empty exterior area, all gutters, all four corners, image border and enclosed gaps between limbs. Smooth antialiasing only at immediate silhouette edges. No alpha-1 haze or residual low-opacity background. Never render a checkerboard, gray grid, matte, colored backdrop, ground, shadow, broad glow, frame, border, text, labels, logos, UI, numbers or decorative plate. The game supplies shadows and animated attack effects. Original character designs only.
```

### Atlas spacing and background edit

```text
Use case: precise-object-edit.
Image 1 is the edit target: six original painted boss forms in a 3-column by 2-row atlas. Keep these exact character designs, order, colors, costumes, body poses and elevated frontal camera. This is a sprite atlas technical cleanup, not a redesign.
Change spacing: uniformly reduce each of the six cell subjects to about 80% of its current size and center it within its equal 512x512 cell on the same 1536x1024 canvas. Preserve full barrel flame at top-left, full fungal fingers, full feet, and separate giant legs pair. Every subject including flame and electrical sparks must have a generous transparent margin of at least 35 pixels from its own cell boundary. No overlap between cells. Keep giant legs pair in bottom-right with a generous fully transparent vertical gap between legs.
Change transparency: remove ALL background, backdrop illumination and low-alpha haze. The source RGBA contains many faint background alpha=1 pixels; these must be alpha=0. All empty areas, all outer borders, corners, gutters and enclosed gaps must have exactly alpha=0. The entire canvas except the actual character silhouettes must be truly transparent. Keep clean antialiased immediate silhouette edges. Remove broad orange or blue glow patches surrounding the sprites; keep only tightly shaped flame tongues and narrow close electrical arcs. NO residual alpha-1 film anywhere. No checkerboard, gray grid, floor, cast shadow, matte or replacement background. RGBA transparent PNG required. Preserve the six paintings and their physical textures.
```

### Transparent-background cleanup edit

```text
Use case: background-extraction.
Image 1 is the edit target, a six-boss sprite atlas with exactly three columns and two rows, 1536x1024 RGBA. Preserve every character pixel color, silhouette, pose, position, scale and atlas spacing exactly. Do not regenerate, resize, move or redesign anything. Remove only accidental transparency residue.
The actual RGBA source contains 50,821 nearly invisible pixels whose alpha value is 1, floating across otherwise empty background. Set every alpha=1 pixel to alpha=0. Preserve all meaningful character pixels and alpha values 2 through 255 exactly. Corners, outside edges, empty gutters and the gap between the giant legs must be alpha ZERO. No faint haze. Output the same RGBA PNG with a real completely transparent background, not a checkerboard or a painted matte. This is a strictly technical alpha cleanup, absolutely no new artwork, shadows, backdrop, text or other changes.
```

## Thrown barrel atlas: exact prompts, 2026-10-13

The built-in image-generation tool used `transparent_background: true`.
The first call derived four rolling frames from the barrel in `bosses.png`;
the second edited that result to clean its exterior alpha. The saved atlas then
received the previously approved alpha-only cleanup: 7,675 alpha-1 pixels
became alpha 0, preserving every RGB value and every other alpha value.
[ARTWORK.md](ARTWORK.md) records the verified final pixels and checksum.

### Barrel sprite derivation

```text
Use case: precise-object-edit
Asset type: RGBA game projectile sprite atlas, four animation frames in a 2 by 2 grid.
Input image: the supplied boss atlas is the edit source. Isolate and derive ONLY the scorched red steel oil drum held by the upper-left boss into a dedicated barrel sprite sheet. Remove all characters and every other object.
Primary request: Four exceptionally detailed arcade-quality views of that same red industrial barrel rolling on its side toward the viewer, at rotation steps 0, 90, 180 and 270 degrees around its cylindrical long axis. These are four animation frames of one object, not four different barrels.
Style: match the mature high-quality painted arcade artwork in the source: rich red enamel, chipped paint, scorched steel, dark soot, dents, oxidized metal hoops, raised circular rims, bright curved metal highlights and deep three-dimensional shading. This must look like the boss's beautiful barrel, not a simple outline or flat icon.
Camera: fixed elevated three-quarter view consistent with a portrait road shooter. The barrel lies on its curved side, its long cylinder axis across the screen at a slight diagonal from lower-left to upper-right. Show a convincing elliptical circular end and the curved red cylinder. Hold the silhouette, camera, size and light direction consistent across frames; rotate dents and scratches around the cylinder to convey rolling.
Composition: exactly four separate barrels in equal square cells, top-left, top-right, bottom-left, bottom-right. Center each frame at the same local position and scale. Wide perfectly transparent margins and gutters, at least 32 pixels clear from every cell boundary. Entire barrel silhouettes visible, no clipping.
Lighting: strong warm metal highlights and ember-orange light reflected along scorched rims. No visible flames or smoke; the app adds animated fire separately. No cast floor shadow.
Background: actual fully transparent RGBA background. Every exterior background pixel and every gutter must have alpha 0. No checkerboard, background color, diffuse glow, haze, matte, vignette, or broad shadow, including barely visible residue. Opaque detailed barrel body and clean antialiased edges only.
Text: none. No labels, numerals, logos, watermark, ground, characters, sparks or extra props.
```

### Transparent-background cleanup

```text
Use case: background-extraction.
Edit target: this four-frame red steel barrel sprite atlas. Preserve the exact barrel artwork and four existing views, every material color, size, position, and the 1536x1024 canvas. Do not redraw or redesign the barrels.
Perform ONLY a precise transparent-background cleanup. The PNG is RGBA but has 19,657 stray alpha=1 background pixels and some alpha=2 residue in the otherwise empty center gutter. Every pixel of empty background, including all outer edges, corners, both wide center gutters, and any colored glow or haze outside the immediate barrel silhouettes, must become alpha=0. Keep clean antialiasing only at the immediate silhouette edges. No diffuse glow, backdrop, shadow, checkerboard, matte or new pixels. Preserve the richly painted chipped red metal and all four independent cylindrical drum silhouettes. Output a genuinely transparent RGBA PNG with the same dimensions and frame arrangement.
```

## Barrel explosion atlas: exact prompts, 2026-10-13

The built-in image-generation tool created `explosion.png` with
`transparent_background: true`, followed by spacing and transparency edits.
The same previously approved alpha-only cleanup used for the other game atlases
removed residual alpha-1 pixels. Pixel verification is recorded in
[ARTWORK.md](ARTWORK.md).

### Initial generation

```text
Use case: stylized-concept.
Create an original production-quality RGBA explosion animation sprite atlas for Stryker's Run, a mature arcade road shooter viewed from above and behind its hero. This is game VFX artwork, not a screenshot.
Exactly EIGHT chronological frames in a regular FOUR-COLUMN TWO-ROW grid, read left to right then next row. Equal-size cells on a landscape canvas. Every explosion stays fully inside its own cell with generous completely empty transparent gutters and margins. Consistent fixed camera, common detonation center, common scale, no shifting framing. Camera elevated about 35 degrees looking down at a fuel barrel explosion, with visible volume rising above its ground origin.
Sequence: (1) tight jagged white-gold ignition with compact orange flame lobes; (2) fast growing hot fireball; (3) full powerful bright orange fireball with a cream-hot broken core and complex billowing lobes; (4) peak turbulent blast with curling orange flame and rich black charcoal smoke; (5) expanding darker soot cloud with flame licking through folds; (6) cooling dark textured smoke with deep red embers; (7) separated dissipating gray-charcoal smoke wisps with a few embers; (8) thin broken translucent wisps almost dissipated.
Quality and style: premium richly detailed hand-painted arcade VFX with convincing 3D volume, intricate turbulent flame sheets, sharp irregular flame tongues, layered natural cauliflower smoke folds and internally illuminated soot. Clear readable organic silhouettes at small game scale, restrained bloom around real hot contours. Not cartoon outlines, not pixel art, not flat vectors, not low-poly, not a blurry orange blob. Mature cinematic fire simulation quality translated into crisp arcade illustration. No uniform radial glow, no circular or oval outline, no damage radius, no ground shockwave ring, no separate radial starburst, no explosive barrel object, no scene, floor, characters, UI, text, numbers, borders or labels.
Background is genuine RGBA alpha transparency. All empty pixels, corners, outer borders and wide gutters must be alpha exactly ZERO, not alpha 1 haze. Clean meaningful antialiased translucent smoke and flame edges only. No baked checkerboard, backdrop, matte, background haze or rectangular glow. Never clip smoke or sparks at a cell edge. Each cell contains just its own one explosion frame.
```

### Spacing and transparency edit

```text
Use case: precise-object-edit.
Edit target: the supplied eight-frame fire-and-smoke explosion sprite atlas. Preserve these richly detailed explosions, their eight stages and order, colors, materials, rendering style and transparent translucent smoke. Only fix atlas spacing and background transparency.
Output a regular FOUR-column TWO-row atlas, 1792 by 896 pixels, eight equal 448-square cells. Uniformly reduce every subject to 78 percent of its current size; do NOT enlarge the first small ignition. Keep common scale across all eight frames and align all explosion ground origins at local x=224,y=360 in every cell. Keep every flame, smoke fold, ember and translucent edge at least 35 pixels from every cell border. Each frame must fit completely inside its own cell with no clipping or cross-cell glow.
The actual source RGBA contains unwanted low-alpha background film (130010 alpha-1 pixels and alpha-2 residue). Remove all empty-background film and any faint diffuse halo outside the actual flames/smoke. Every empty exterior pixel, corner, outer border, and cell gutter MUST be alpha exactly 0. Preserve meaningful translucent smoke edges and actual flame detail only. Genuine RGBA transparency, no checkerboard, matte, backdrop, ring, floor, scene, shadow, text, caption, label, UI or other new artwork.
```

### Transparency cleanup edit

```text
Use case: background-extraction.
Edit target: the supplied eight-frame explosion atlas. Preserve the exact 1774x887 dimensions, all eight paintings, their positions and scales, all colors, shapes, flame detail and meaningful translucent smoke. Do not redraw or move artwork.
Technical transparency cleanup ONLY: the PNG has 22812 residual alpha=1 pixels in background, including cell gutters. Set ALL alpha=1 pixels to alpha=0. Also remove isolated empty-background residue; all empty exterior background and gutters must be completely alpha=0. Preserve meaningful smoke pixels and clean immediate antialiased edges. The output must be RGBA with real alpha-zero empty background, no checkerboard, no matte, no backdrop, no new text or imagery. Keep the eight-cell layout exactly.
```


## Boss walking atlas: exact prompts, 2026-10-13

The built-in image-generation tool used `transparent_background: true` for
all three calls. The first derived walking poses from `bosses.png`; subsequent
calls edited the generated atlas. Pixel cleanup and verification are recorded
in [ARTWORK.md](ARTWORK.md).

### Walking poses

```text
Use case: precise-object-edit.
Input image is a character-design reference: the existing boss atlas for Stryker's Run. Create a NEW walking animation atlas derived faithfully from FOUR of these characters. Retain their exact identities, clothing, anatomy, materials, palettes, richly painted mature arcade style and elevated frontal camera looking down about 25 degrees.
Output exactly SIXTEEN full-body sprites in FOUR COLUMNS and FOUR ROWS on a square canvas, about 2048 by 2048 pixels. Equal-size cells. Each row contains FOUR chronological WALKING poses of one identical character moving FORWARD toward the viewer/down-screen, not attacking:
Row1: the large gray zombie in mustard industrial overalls, carrying his detailed burning red barrel against his shoulder. Keep that barrel in every frame.
Row2: the lean commanding undead officer in long olive coat, peaked cap, radio and harness. Walking forward with a purposeful stride, arms close enough to fit his cell.
Row3: the gray lightning zombie in torn blue electrical-worker suit with coils and insulators. Walking forward with subdued close blue electric highlights.
Row4: the ordinary emaciated skin-walker in ragged burgundy shirt and brown trousers with dangling hands. NO fungal form.
Columns for each row: (1) left foot strides forward, right foot behind; (2) passing pose, knees and hips between strides; (3) right foot strides forward, left foot behind; (4) opposite passing pose. Opposing arm sway where hands are free, coat tails and cloth respond to steps. Make the poses unmistakably different and anatomically coherent; actual leg and knee motion, not four copies shifted side-to-side.
All figures face directly toward the player, no side or rear views. Keep common scale, grounded baseline, head height and lighting within each row. Every sprite FULLY contained inside its own equal square cell with at least45pixels empty margin including flame tips and outstretched hands. Nothing overlaps cell boundaries; never clip feet, hands, flames or head. Do not include the fungal zombie or giant legs from the reference.
No scenery, road, floor, cast shadow, smoke, broad glow, aura, vignette, decorative backdrop, plate, UI, text, labels or numbers. True RGBA transparent background, every exterior background pixel and all gutters alpha exactly0, never alpha1 residue. Preserve smooth immediate antialiased edges and natural creature silhouettes. No checkerboard or matte. This is a production sprite atlas, not a montage or screenshot.
```

### Pose and spacing edit

```text
Use case: precise-object-edit.
Edit target: the supplied sixteen walking-boss sprite atlas, exactly four columns and four rows. Keep the same four character identities, same clothing, carried red barrel, colors, style, camera, row order and exactly16sprites.
Fix atlas spacing: uniformly shrink all16figures to72percent of their current size within their existing equal square cells. Center each body inside its cell and align the soles at85percent cellheight. Leave at least35pixels genuinely empty transparent padding on all four sides of each cell. Restore any clipped tips of barrel flames. Keep every lightning arc tightly against the lightning zombie's body, remove broad blue glow and bridges connecting adjacent sprites; every sprite must be wholly isolated.
Fix walk sequence for each row: column1 LEFT foot forward and RIGHT foot behind, column2 legs passing, column3 RIGHT foot forward and LEFT foot behind, column4 opposite passing pose. Column3 must visibly be the opposite stride from column1; retain asymmetric clothing, barrel shoulder and character anatomy without mirroring the entire character. Distinct leg movement, torso weight shift and free-arm swing. Preserve common body scale across the four poses.
True RGBA PNG with alpha exactly0 everywhere outside the actual silhouettes, including all cell gutters and every outer border. No alpha1 background film, no checkerboard, no matte, no ground shadow, no backdrop, no text, no other art. Do not add extra rows or columns.
```

### Spacing and transparency cleanup

```text
Use case: precise-object-edit.
Edit this16sprite boss walking atlas as a technical spacing and alpha cleanup only. Preserve the exact four characters, all16existing poses, row and column order, colors, textures, and1254x1254 canvas.
Make EACH sprite substantially smaller inside its own existing equal cell: reduce to75percent of its CURRENT size, keep common scale within each row, place its feet at82percent of its cellheight. There must be at least35pixels of EMPTY transparent space on every side of each sprite INCLUDING soles, flame tips, claws and lightning. No character may touch a row boundary. The commander boots currently nearly touch the next row; move them well clear. Keep all4rows and4columns.
Remove all unwanted alpha1 film and isolated alpha2 residue from emptybackground, borders and gutters. Actual source has57456alpha1pixels. Every empty exterior pixel must have alpha exactly0, preserve clean immediate antialiased silhouette edges. Keep lightning as thin close arcs only, no broad glows. No checkerboard, background, matte, shadow, floor, text or new imagery.
```

## Barrel Warden throw and travelling fire: exact prompts, 2026-10-15

Both sheets were generated with the built-in image-generation tool and 	ransparent_background: true. The Warden references were osses.png and oss-walk.png; the flame reference was arrels.png. Each sheet had two subsequent edits. The user explicitly authorized alpha-1-only cleanup after these attempts; see [ARTWORK.md](ARTWORK.md).

### Throw poses

```text
Use case: stylized-concept. Asset type: production-ready transparent animation sprite atlas for an arcade game.
Input image 1 (bosses.png) is the identity and high-quality painting reference: use ONLY the top-left Barrel Warden character. Input image 2 (boss-walk.png) is the reference for slightly elevated front camera and identity.
Create ONE new sprite sheet, exactly eight full-body images of this same massive bald gray zombie in mustard-yellow industrial bib overalls and heavy scuffed boots, carrying and throwing his chipped red steel burning drum. Match his face, bulky exposed scarred arms, costume, barrel shape, textures, and rich hand-painted arcade lighting. Keep the hard-edged high detail and mature illustrated look, not smooth plastic and not primitive.
Layout: 2048×1024 PNG, 4 columns × 2 rows of 512×512 equal cells. Read left to right across top then bottom. Every pose shares the same body scale, center x=256 within its cell, feet on y=455 baseline. Keep at least 35 fully transparent pixels between all art and every cell edge, including fingertips, fire and boots. Full figure with no clipping. No contact shadow.
The animation is an articulated two-handed throw toward the player, who views the boss from a slightly elevated frontal camera:
Frame 0: ready stance, burning barrel on his right shoulder (viewer left), right hand supports it, other arm low.
Frame 1: anticipation, both hands grip barrel, elbows bend, shoulders twist back, torso gathers force.
Frame 2: hoist barrel up and behind the head, both arms bent raised, chest stretches, knees flex.
Frame 3: forceful forward swing, barrel still held by both hands forward above chest, shoulders and elbows visibly rotate.
Frame 4: exact release, both hands now EMPTY with fingers spread, arms reaching forward, barrel is ABSENT from this cell, NO detached projectile.
Frame 5: follow-through, EMPTY hands and arms swing down, torso leans forward, NO barrel.
Frame 6: recovery, empty arms return toward torso, torso straightens, NO barrel.
Frame 7: return to ready with a NEW red burning barrel lifted back to the same shoulder position as frame 0.
Make shoulder and elbow motion anatomically connected and genuinely different for each keyframe. Maintain recognizable same individual, costume and proportions. The player separately renders the flying projectile, so do not paint any airborne barrel in frames 4–6.
Transparency requirements: genuinely transparent RGBA background. Every exterior pixel and all grid gutters must have alpha exactly zero, not almost-transparent residue. Clean antialiased silhouette only. No backdrops, no ground plane, no shadow, no matte, no opaque checkerboard, no outlines of cells, no text, no labels, no broad colored glow. Fire attached to carried barrel only, sharply shaped yellow/orange tongues entirely within cell.
```

### Throw pose spacing

```text
Edit ONLY the framing, spacing and transparent background of this existing eight-pose Barrel Warden sprite atlas. Preserve the exact art identity, costume, barrel details, textures and pose sequence.
Output must have 4 columns by 2 rows in equal cells, 2048 pixels wide and 1024 tall if possible. Each cell MUST have at least 50 fully transparent pixels on all four edges. Shrink every entire character and its flames to fit safely. All eight feet must align to a common cell-local baseline at 85% of cell height, and body scale must be identical across frames. Complete the clipped orange flame above the overhead barrel in top row third cell. Never clip a boot, hand, flame, or any part.
Keep poses exactly as existing: shoulder ready; grip windup; overhead hoist; forward swing still holding barrel; release empty hands; follow-through empty hands; empty recovery; new barrel ready. No detached flying barrels.
Genuinely transparent RGBA: every exterior, margin, and gutter pixel alpha exactly 0, not alpha1, with clean antialiased silhouettes only. No broad glow, no shadows, no grey matte, no checkerboard, no text, no grid. This is a strict padding/placement correction, not a new design.
```

### Throw pose transparency

```text
Production correction to this eight-pose character animation sheet. Keep the eight character drawings, identities, sequence and rendering quality. The current figures are TOO LARGE for the cells and the top flame nearly touches canvas edge. Reduce EVERY drawing to 65% of its current size, including flames. Center each drawing in its own equal cell, align lowest boot of every pose at 80% cell height. Maintain 4 equal columns and 2 equal rows. Leave broad EMPTY TRANSPARENT padding, at least 80 pixels on EVERY cell edge. Do not try to fill the available canvas with art. The sheet must look deliberately spacious. The overhead barrel flame in top-row column 3 must be short and fully inside its cell.
Most important technical correction: absolutely all empty background pixels must have alpha=0. Remove the approximately 34,444 spurious background pixels with alpha1. Corners, outermost 60-pixel border, and internal row/column gutters must be entirely RGBA alpha0. Keep smooth antialiasing directly on silhouette edges only. No semi-transparent noise, no fog, no shadows, no matte, no colored halo, no checkerboard. Keep the original eight highly detailed arcade character poses. Final sheet 4 columns×2 rows.
```

### Flame sequence

```text
Use case: stylized-concept. Asset type: production animated flame sprite atlas for the burning red barrel in an elevated-camera arcade road shooter. Input image is a STYLE and material reference ONLY: keep its premium realistic hand-painted arcade lighting, but draw NO barrels and NO solid objects in the output. Generate exactly EIGHT distinct consecutive animation frames, arranged in an exact regular grid of FOUR columns by TWO rows. Landscape canvas, equal square cells if possible. Each frame is a small fierce turbulent orange/yellow fire plume rising from a broad curved base at the BOTTOM CENTER, sized to engulf a rolling steel drum from behind. Detailed physically believable twisting flame sheets, forked licking tongues, yellow-white hot cores transitioning through rich orange to sparse dark red edges; natural negative gaps between tongues, tiny sparse embers, just a trace of curling soot at tips. Attractive high-end arcade VFX, sharp readable complex flame silhouettes, convincing three-dimensional folds, no flat teardrops, no soft blurred oval glow. All eight frames show an evolving continuous LOOP: flame tongues stretch, curl, split and reform, changing asymmetrically while the base stays planted at exactly the same coordinate; frame8 flows naturally into frame1. Keep consistent overall scale and comparable energy across all frames. Every frame has its entire flame, soot and sparks wholly INSIDE its own cell with at least 35px genuinely empty margin on each edge; isolated frames, no shared glow or bridges. IMPORTANT: real transparent RGBA PNG. Every exterior background pixel, cell gutter and border MUST be alpha exactly ZERO, not alpha1. No checkerboard in image pixels, no black/grey/colored backdrop, no ambient ground shadow, no matte, no rectangular haze, no vignette, no text, labels, numbers, UI or watermark. Natural antialiased flame edges only. Fire remains bright and richly textured on a dark asphalt game background.
```

### Flame spacing and transparency

```text
Use case: background-extraction. Edit target: the provided eight-frame flame atlas. Keep exactly these eight rich detailed orange-yellow flames and their evolving shapes in a regular four-column two-row grid, keep each source pixel of visible fire as closely as possible. Remove ALL the orange-brown background glow, matte and backdrop. No background gradient or haze anywhere. Output real RGBA transparency, every exterior pixel and every gutter pixel alpha exactly 0, including any nearly invisible alpha1 residue. Flame edges should be naturally antialiased, genuine semitransparent flame only, with empty transparent negative gaps. Do NOT paint a checkerboard. Keep full plume and sparks inside each cell; shrink within cells slightly if needed so all four cell edges have at least 32 pixels fully transparent padding. Maintain the same 1536x1024 canvas, 4 by 2 frame layout, common centered base and consistent scale. No text, labels, frame borders or new objects.
```

### Flame transparency cleanup

```text
Use case: background-extraction. Production sprite alpha cleanup only. Input is an eight-frame fire atlas: keep all eight fire shapes, colors, texture, grid arrangement and scale unchanged. Eliminate the entire brown-orange backdrop and diffuse glow; deliver isolated flame tongues and sparse sparks on truly empty RGBA background. Background alpha must equal ZERO everywhere outside actual flame/sparks, including alpha1 residue. No broad semitransparent atmosphere or rectangular fog anywhere. Retain fine antialiased edges only immediately along real fire contours. Keep transparent holes between tongues. Four columns, two rows, 1536x1024, each flame entirely within its cell. No checkerboard, labels, borders, shadows or matte.
```

## Storm clouds, electric spiders and persistent craters (2026-10-15)

Generated with the built-in `image_gen` tool and `transparent_background: true`. Cloud style reference: `bosses.png`; spider style references: `bosses.png` and `actors.png`; crater material reference: `road.png`. Each second and third call edited the preceding result. The user explicitly approved background-only script cleanup after all three attempts retained faint residue. Exact prompts follow.

### Storm cloud, attempt 1

```text
Use case: stylized-concept. Asset type: premium animated storm cloud sprite atlas for Stryker's Run, a gritty elevated-camera arcade road shooter. Reference image supplies STYLE and electric-blue storm lighting only, specifically the electrical zombie in the top-right cell. Draw NO characters. Generate exactly EIGHT consecutive animation frames of one compact floating thundercloud in a precise FOUR-column TWO-row grid. Each frame shows the same low broad three-dimensional storm cloud with an irregular cauliflower billowing silhouette, beautifully detailed charcoal/navy smoke folds, cold silver rim light and intense cyan-white lightning glowing from deep cracks inside. Different lobes roll and reform across the 8 frames in a seamless loop, internal lightning pulses travel through the underside; natural curling transparent wisps at sides. Show the top and the dark underside from an elevated frontal viewpoint, cloud floats above the road toward the camera/downscreen. Cloud width about1.8timesheight, common center and scale in everycell. Rich painterly realistic arcade VFX, mature dramatic threatening storm, not cartoon weather icon, not flat oval, not blurredblob. Only internal sheet-lightning and a few short downward roots close to underside; long downward rain bolts will be added by the game. No ground, ground shadow, road, sky, background, horizon, rain streaks across canvas, characters, text, labels or frameborder. Each cloud wholly inside its equal cell with at least40pixels truly emptyspace aroundallwisps; no bridges between cells, no fogsheet or broad rectangular opacity fade. Landscape atlas preferably2048x1024 with4x2equal512cells. Real RGBA PNG; EVERY exterior background, gutter and border pixel must be alpha exactlyZERO, notalpha1; transparent holes betweenwisps; preserve only meaningful antialiasing at cloudcontours. No checkerboard, matte, backdrop, vignette, aura or baked background gradient. Keep the cloud visually readable over dark asphalt using intricate luminous innerlight and rimlight, not a rectangle of glow.
```

### Storm cloud, attempt 2

```text
Use case: precise-object-edit. Edit target is the supplied eight storm-cloud frames, 1774x887 RGBA, four columns and two rows. Preserve all eight detailed cloud paintings and their distinct billowing/lightning phases, dark blue charcoal texture and cyan-white inner lightning. Fix spacing and background only: uniformly SHRINK each cloud to 70 percent of its present width and height INSIDE its current cell, center every cloud at the same cell-local coordinate (50% width,52% height). Keep every wisp and lightning root at least45pixels away from every cell edge; currently clouds touch the sides and cross cell boundaries. No clipping. Keep complete natural silhouettes; do not trim them with a rectangle. Remove the blue speckles/glow film outside true cloud contours. Every empty exterior, corner and gutter must be alpha EXACTLY0, not1or2residue. Smooth antialiasing along real contours and real thin cloudwisps only. No background gradient, coloredmatte, checkerboard, text,labels,shadow,scene,newobjects. Exact4x2layout and1774x887canvas.
```

### Storm cloud, attempt 3

```text
Use case: background-extraction. Edit only technical transparency and frame anchoring in this eight-frame storm cloud atlas. Preserve exactly1774x887canvas,4columns2rows, all8cloudshapes, theirintricatedetails, colors and internal lightning. Empty background still has35240pixels atalpha1. Set everybackgroundpixeloutside actualcloudwisps toalphaexactly0, especiallyouterborders,gutters,corners. No faint blue film or isolated nearlyinvisible speckles. Keep actual softly translucent cloudedges smooth and natural. Align all8 cloudbodycenters at the same celllocalpoint x=222,y=244; secondrowcurrentlya little higher, shift thoseclouds downward20pixels WITHOUTchangingcloudsize. Everycloudandwisp mustretain at least40pixelsclearspacefromeverycelledge. Do not addglow, fog, scene, checkerboard, labels, borders, shadows or any newart. GenuineRGBAtransparentPNG.
```

### Electric spider, attempt 1

```text
Use case: stylized-concept. Asset type: production WALKING animation sprite atlas for an electric undead spider enemy in a premium arcade road shooter. Input images are STYLE references only: image1 bosses and image2 actors establish realistic hand-painted arcade lighting, rich textured surfaces, elevated camera. Create one original terrifying eight-legged spider with black/charcoal organic chitin, cracked armored abdomen containing brilliant blue-white electric energy, pale cyan eye cluster, copperlike cable details, and small electric arcs between its legs. No human features. Its low slung heavy body looks physical, mature, fierce, richly detailed, not cute or cartoony. EXACTLY eight successive poses of the SAME spider, in an exact equal FOUR-COLUMN by TWO-ROW atlas, canvas 1536x1024, cells384x512. Camera elevated in front, seeing tops of body and legs, spider facing toward camera/downscreen. Every frame has the body center at EXACTLY relative cell(192,250), matching angle, size and lighting. Walking cycle in place: alternate four legs lifting/reaching while other four push, joint angles visibly change across frames1-8, slight natural body bob but no turning; frame8 loops back to1. Keep all legs and arcs inside each cell with at least40px genuinely empty padding on all sides; target complete silhouette roughly290px wide and300px tall, baseline aroundy410. Detailed sharp painted highlights, crisp dark contours, convincing leg anatomy, eight legs each with several joints, blue electricity flickering differently per pose but never swallowing the physical body. No floor shadow or ground, no scenery, no text/numbers/grid/labels/watermark, no human, no broad glow haze. Real RGBA transparency: ALL exterior background, inter-leg holes, gutters and borders must be alpha exactly0, NOT1. Do not paint a checkerboard. Preserve clean antialiased silhouette edges. Draw only eight isolated spiders on completely transparent background.
```

### Electric spider, attempt 2

```text
Use case: precise-object-edit/background-extraction. Edit this existing eight-frame electric spider atlas. Preserve this excellent spider design, rich surface detail, colors, eight walking poses and 4 columns by 2 rows arrangement. Keep output canvas EXACTLY1536x1024 and equal cells384x512. Correct ONLY sprite registration/padding and transparent background. Keep each spider's head center at local(x192,y270), abdomen center at local(x192,y175), same body size in all cells. Place walking toes within y420. ALL eight spiders and every arc must fit wholly inside x40..344 and y60..452 of their own cell. Do not crop legs. MOST IMPORTANT: remove the entire surrounding gray/blue/black glow haze and near-invisible alpha film. Every pixel outside spiders and their thin connected blue electric arcs must be alpha EXACTLY0. All gutters, canvas border, cell corners and enclosed gaps between legs must be completely transparent alpha0, never alpha1/2/3. No shadow, no soft background glow. Preserve crisp subtle antialiasing ONLY at actual contour edges. Do not change the detailed black chitin/copper joint design or bright crackled abdomen, and do not paint a checkerboard. Actual RGBA PNG. No scene,text,labels,numbers.
```

### Electric spider, attempt 3

```text
Use case: background-extraction. Extract these eight existing electric spider sprites onto real alpha transparency. DO NOT REDRAW OR CHANGE THE SPIDERS. Keep exact1536x1024 image and exact4columns2rows cell grid. Remove the full diffuse gray/orange/blue haze and ALL near-invisible backdrop film from every cell, every gutter and every hole between legs. Treat the sprites as hard cutouts with delicate antialiasing only immediately adjacent to the solid contour and narrow attached lightning arcs. The backdrop must be absent, alpha exactlyZERO everywhere outside those silhouettes. No alpha1..10 blanket or low-opacity ambient haze. Do not add new halo, shadows, ground, glow, grid, checkerboard or replacement background. Keep the black armored bodies, copper joints and blue-white narrow electricity unchanged with their crisp painted detail. Result must be RGBA with transparent pixels carrying alpha0. Leave pose order, cell placement, body size and every joint bend unchanged.
```

### Charged crater, attempt 1

```text
Use case: stylized-concept. Asset type: four premium electrical blast crater ground decals for an elevated-camera arcade road shooter. Reference image provides ONLY the dark weathered cracked asphalt material and mature realistic painted game-art style; do not reproduce the whole road. Create exactly FOUR small DIFFERENT irregular craters in an exact TWO-column TWO-row atlas on a square canvas. Each is a shallow blasted depression in charcoal asphalt, with jagged upturned broken pavement, fine dark debris close to the edge, scorched center, metallic fragments, and vivid blue-cyan electrical fissures glowing inside the cracked rim. A few tiny frozen white-blue sparks tightly around the rim, no big lightning bolts. Different natural asymmetrical silhouettes and crack patterns, same overall size and viewpoint. Elevated overhead/front perspective, shallow elliptical footprint seen at35degreesdownward; strongconvincingdepth with darkerrecess andlitedge, not an outline ring. User wants permanent small electrified craters left behind exploded electric spiders. No spiderbody, no characters, no groundplane outside actualcrateredge, nooutershadow, nosmoke (smokeanimatedseparately), noflames/lava/red/orange, nocirculardamageindicator,no perfectcircle. Each entiredecal includingchips fits itsowncell withatleast60pixelsfullyemptytransparentpaddingallsides. GenuineRGBA PNG, ALLexteriorbackground andgutters alphaexactly0, notalpha1 haze, no paintedcheckerboard, matte, rectangle, vignette or broadgradient. Crisp rich hand-painted detail readableatsmallgamescale, restrainedglow confined torealcracks. No captions,borders,labels orwatermarks.
```

### Charged crater, attempt 2

```text
Use case: background-extraction. Keep the four detailed electrified asphalt crater decals unchanged in this1254x1254two-column two-row atlas. Preserve brokenrock/chippedasphalt detail, fragments, colors, textures, darkinteriors andtightblueelectricalcracks. Remove ALL emptybackgroundresidue: sourcehas62137alpha1pixels, bordershavealpha1. Everypixeloutside realcrater/debris mustbealphaEXACTLY0, preserving immediateantialiasededges. No haze, matte,groundpatch,checkerboard orrectangle. Uniformlyreduceeachdecalto85%currentsizeinitscell, centerit,soeverychipandarciswellinsideitsowncellwithatleast50pixeltransparentpadding. Do notaddsmoke/flames/largebolts/shadows/textornewobjects. RealRGBAtransparentPNG.
```

### Charged crater, attempt 3

```text
Use case: background-extraction. Technical transparency cleanup only. Preserve this1254x1254four-crateratlas exactly: identical positions, scale, crackshapes, colors, pixels of meaningful pavementandlightning,2x2layout. Set every nearlyinvisible backgroundresidue pixel outside the actual crater/debris/electricalcontours to alphaexactlyZERO. Especially remove alpha1film in corners/borders/gutters andisolatedlowalpha speckles. Keep smooth antialiasing immediatelyalong the irregularrealartwork. No matte, backgroundgradient, rectangularfade, checkerboard or newimagery. Real RGBA PNG with truly empty background.
```

## Independent hero scarf (2026-10-16)

The built-in `image_gen` tool generated these two four-frame atlases with
`transparent_background: true`. The first body call edited `hero.png`;
the first scarf call used that original hero as its style reference. Each
second and third call edited its preceding result. The final body is
2172 × 724 and the scarf is 2048 × 768; requested dimensions and anchor
coordinates in the prompts are design constraints, not measurements of the
returned artwork. Measured registration appears in [ARTWORK.md](ARTWORK.md#independent-hero-scarf-2026-10-16).

After three attempts per atlas retained faint background residue, the user
explicitly approved setting only alpha-1 pixels to alpha 0. The cleanup
preserved every RGB value and all other alpha values. The six tool prompts
below are verbatim.

### Hero body, attempt 1

```text
Use case: precise-object-edit.
Asset type: four-frame game character animation atlas, PNG with real alpha transparency.
Input image 1 is the exact edit target: the four rear-view walking poses of Stryker.
Change only one thing: remove the long loose flowing red scarf tail from all four poses, reconstructing only the small shoulder/back regions previously hidden underneath the tail. KEEP the close-fitting red scarf wrapped around his neck, including its small knot. The separate long tail will be attached by the game renderer later.
Preserve the exact hero identity, rear view, four existing walking poses, navy clothing, brass hardware, dark green tactical backpack, rifle aimed upscreen, hair, anatomy, lighting, painted mature arcade rendering, figure scale and original frame centers. Keep the same 2172x724 wide composition with four equal columns, one figure per column, no visible grid. Do not move or crop the figures. Do not create additional figures or accessories.
Output actual transparent RGBA PNG. All exterior background pixels, including the spaces between characters, must have alpha exactly zero. No checkerboard pattern, backdrop, matte, floor, exterior shadow, muzzle flash, text or border. Preserve crisp antialiased character edges.
```

### Hero body, attempt 2

```text
Use case: background-extraction. Image 1 is the exact edit target. The character art is already correct: do not change any poses, body detail, collar, positions, colors, size or composition. Change only the alpha channel of empty background. The current PNG has 13,285 near-invisible alpha-1 pixels in the exterior and even border rows. Set every empty exterior/background pixel to alpha exactly0, including all canvas boundaries and inter-character gutters. Preserve every actual character pixel and smooth antialiased silhouette edges. Return the same four figures, same2172x724RGBA PNG, with genuine transparent exterior; no checkerboard, matte, halo, backdrop, floor, shadow or new artwork.
```

### Hero body, attempt 3

```text
Use case: background-extraction. This is a technical transparency correction only. Keep the supplied four-frame character sheet unchanged: same poses, painted details, colors, full dimensions2171x724and exact positions. Do not redraw the art. The PNG still has10,910pixels at alpha1, including149isolated background specks more than8pixels from the subject. Set ALL alpha1 pixels to fully transparent alpha0. Leave every RGBvalue and every alpha value2..255unchanged. Preserve trueRGBA, crisp antialiasing, transparent canvas/gutters. No replacement background, no checkerboard or halo.
```

### Scarf tail, attempt 1

```text
Use case: stylized-concept.
Asset type: four-frame crimson scarf-tail animation atlas for a high quality painted arcade game, actual transparent RGBA PNG.
Input image 1 is a style reference only: Stryker's original scarf. Create ONLY the loose red scarf tail as a separate compositing asset, no character, neck wrap, knot, body, arms, weapon or props.
Canvas exactly 2048x768, four equal 512x768 columns in one horizontal row. In each cell place the identical cloth root at local coordinate (450,280); root is a narrow folded fabric end approximately 36 pixels high, intended to tuck under a character's collar. The entire tail extends LEFT of this root to local x=65..90. Every frame uses the exact same scale and root position. Keep substantial genuinely transparent space around each tail. No tail crosses a cell boundary.
Show four successive flutter phases: a broad shallow wave, crest curling upward at the outer tip, a soft S-shaped ripple, and a low flowing wave. Keep all tails trailing toward screen-left, never changing side. Cloth length approximately375pixels, vertical envelope roughly180pixels. Long tattered outer edge but intact connected fabric. Folded deep crimson woven cloth with strong vermilion ridge highlights, deep burgundy fold shadows, fine cloth texture, hand-painted mature premium arcade sprite detail matching the reference. Readable strong silhouette at small game scale. Rear-view perspective matches a soldier seen from above and behind, running upscreen.
Actual alpha transparency: all intended background pixels must have alpha exactly0. No checkerboard, matte, gradient, floor, outside shadow, background, text, labels, borders or visible cell guides. Do not paint translucent background residue. Only antialiasing attached to the cloth edges may use intermediate alpha.
```

### Scarf tail, attempt 2

```text
Use case: background-extraction. Image1 is the exact edit target. Preserve the four crimson cloth tails, their fine painted detail, shapes, same common root positions and 4-column composition. Remove the ENTIRE glowing red haze surrounding them. Cloth does not emit light. The tails must be clean cutouts on genuine alpha transparency, with no glow, shadow or halo of any kind outside the actual fabric. Every exterior pixel outside the actual fabric silhouette must be alpha0, including inter-frame gaps and all canvas edges. Preserve crisp antialiased cloth edges. Same 2048x768 canvas, four equal columns. No checkerboard, no matte, no background color, no text. Only the actual red fabric is visible. Do not add cloth or change its length.
```

### Scarf tail, attempt 3

```text
Use case: background-extraction. This is a technical transparency correction only. Keep the supplied four-frame scarf sheet unchanged: same painted cloth details, colors, exact positions, common roots, and full2048x768dimensions. Do not redraw it. The PNG still has7,912pixels at alpha1, including221isolated background specks more than8pixels from the fabric. Set ALL alpha1 pixels to fully transparent alpha0. Leave every RGBvalue and every alpha value2..255unchanged. Preserve trueRGBA, crisp antialiasing and transparent canvas/gutters. No replacement background, no checkerboard, glow or halo. The actual cloth is the only visible artwork.
```

## Devastated-city parallax backdrop (2026-10-16)

Built-in `image_gen`; one original generation and one edge-region edit. The
selected opaque 948 × 1659 PNG is `city-ruins.png`. The edit replaces boundary
building cuts with rubble; it does not establish pixel-perfect seamless tiling.
Native inspection of the repeated join is recorded in ARTWORK.md.

### Original generation

```text
Use case: stylized-concept. Asset type: polished opaque vertical environment texture for a portrait arcade survival shooter, a slow parallax backdrop BEHIND an existing road. Create a high-quality, richly painted devastated modern city seen from a steep elevated camera looking down and forward, matching a mature hand-painted arcade action game with crisp tactile detail and realistic proportions. Tall damaged concrete and brick buildings, roof slabs broken open, exposed floors, twisted structural steel, collapsed stairwells, shattered windows, rubble and sparse small orange fires. Deep blue-grey shadows, desaturated warm concrete, subtle rust and amber firelight. Distinct readable broken silhouettes and architecture, not flat rectangles with dotted window lights. Composition: 1024 by 1792 portrait or similar tall aspect. Buildings and destruction concentrated in both left and right thirds, with a darker unobtrusive open corridor through the middle that will be covered by the separate road. Full image is an aerial urban environment; NO sky, NO horizon, NO people, NO text, NO UI, NO road lane markings. Do not paint the playable road. Maintain similar scale and lighting all the way from top to bottom so the texture scrolls vertically. Crucial: seamless vertical tile, top and bottom image edges join naturally into continuous ruined city blocks with no seam, no dark border, no vignette, no gradient fade, no frame. All pixels opaque, not a cutout. Detailed dimensional rooftops and wall faces visible from above, long irregular collapsed edges, a convincing ruined city rather than a dark empty background.
```

### Top/bottom edge edit

```text
Edit this game environment texture for seamless vertical scrolling. Keep its mature painted arcade style, palette, buildings, fire, all central 70% of artwork and vertical aerial perspective. Correct ONLY the TOP and BOTTOM edge regions so they join as one continuous texture when identical copies are stacked vertically. Both edges should cross the SAME low-detail ruined cross-street area with irregular asphalt rubble and scattered broken concrete, not through recognizable tall building facades or rooftops. Match the exact top and bottom pixel colours and features for true vertical tiling. Preserve detailed ruined architecture away from those boundary regions. Do not fade to black, add borders, blur a rectangular strip, or mirror any buildings. No skyline, no text, no UI, all pixels opaque. Keep overall tall aspect and central unobtrusive corridor for the existing road to cover.
```
