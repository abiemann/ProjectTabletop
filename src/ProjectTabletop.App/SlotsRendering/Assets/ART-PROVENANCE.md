# Dragon Slots artwork provenance

These original assets were created for Dragon Slots with the built-in `image_gen.imagegen` tool on 2026-09-30. They are project assets, not remote runtime dependencies. No CLI fallback or external API key was used.

## Assets and layout

- `slot-symbols.png`: 1448 x 1086, RGBA PNG, approximately 2.06 MB. Twelve icons arranged in a nominal 4-column x 3-row grid of 362-pixel cells. Use the safe crop rectangles below: the generated dagger and potion slightly cross nominal cell boundaries.
- `dragon-sanctum.png`: 1536 x 1024, RGB PNG, approximately 2.40 MB. Full-bleed opaque 3:2 cinematic cavern background, with detailed dragon artwork at the margins and a deliberately quiet central reel area.
- `slot-treasures.png`: 1254 x 1254, RGBA PNG, approximately 1.96 MB. Supplemental 2-column x 2-row atlas: closed chest and ruby on the first row; emerald and sapphire on the second. The original open chest remains available for opened vault containers.
- The symbols were generated as dimensional fantasy artwork with fine metallic filigree and jewel materials. Live flames and other motion are supplied by the renderer, not baked into a sprite animation.

Safe source crops in atlas pixels; columns are x, y, width, height:

| Symbol | x | y | Width | Height |
| --- | ---: | ---: | ---: | ---: |
| Dagger | 24 | 24 | 352 | 342 |
| Goblet | 425 | 38 | 248 | 325 |
| Treasure chest | 728 | 38 | 350 | 326 |
| Crown | 1082 | 38 | 358 | 325 |
| Dragon eye | 16 | 372 | 348 | 345 |
| Fire orb | 375 | 374 | 350 | 341 |
| Emerald egg | 745 | 381 | 306 | 329 |
| Ruby egg | 1116 | 380 | 297 | 334 |
| Sapphire egg | 39 | 717 | 309 | 344 |
| Rainbow egg | 394 | 717 | 313 | 344 |
| Elixir | 774 | 710 | 253 | 357 |
| Skeleton key | 1095 | 716 | 346 | 342 |

The crop rectangles preserve complete silhouettes and avoid adjacent icons. The renderer can trim low-opacity padding within each rectangle and scale the resulting bounds uniformly, preserving the artwork's aspect ratio.

Supplemental treasure atlas safe source crops in pixels:

| Treasure | x | y | Width | Height |
| --- | ---: | ---: | ---: | ---: |
| Closed chest | 40 | 75 | 612 | 522 |
| Ruby | 680 | 70 | 537 | 530 |
| Emerald | 50 | 672 | 557 | 541 |
| Sapphire | 660 | 669 | 556 | 544 |

The closed chest crosses its nominal 627-pixel quadrant edge by nine pixels. The explicit source rectangle preserves the full lid and metalwork. All three gemstones remain within their quadrants, and all four source rectangles have clear gutters.

## Inspection and transparency

Both final files were inspected through `view_image`. The atlas contains real alpha, not a painted checkerboard. Pillow inspection of the original generated atlas found an alpha range of 0 to 255, 816,715 fully transparent pixels, and 754,722 partial-alpha pixels. Most interior alpha is 253. All four corners and ten representative spaces between icons sampled alpha 0.

The generator left very low alpha residue, commonly 1 to 4, including some enclosed key openings. This limitation is recorded separately from the verified exterior probes; the source does not have mathematically zero alpha in every intended cutout. Inspect the native composited render for visible matte or fringes before accepting the displayed result. The source PNG is preserved without raster repainting.

Representative zero-alpha probes (x, y): (0,0), (1447,0), (0,1085), (1447,1085), (390,200), (710,200), (1076,200), (200,369), (550,369), (900,370), (1275,370), (720,720), (370,1070), (1050,900).

The supplemental treasure atlas was also inspected through `view_image`. Pillow verified RGBA, alpha range 0 to 255, and 742,367 fully transparent pixels. The four corners and central/row/column gutter probes (627,627), (313,627), (940,627), and (627,940) are exactly alpha 0. No painted checkerboard or exterior shadow is present. Source alpha and antialiasing are preserved unchanged.

## Generation prompts

### Symbol atlas, initial generation

```text
Use case: stylized-concept
Asset type: production game symbol sprite atlas for an adult dark fantasy Dragon Slots game.
Primary request: Create one lavish, exceptionally high quality original raster atlas of exactly TWELVE separate fantasy item icons on REAL TRANSPARENT background, RGBA. Use a regular FOUR COLUMN by THREE ROW grid in a 2048 by 1536 canvas if possible (4:3 aspect ratio). Each cell is an equal square with its single object centered and no content crossing cell boundaries. All silhouettes must fit inside the inner 76% of each cell with generous invisible padding. Exactly these subjects, in reading order: Row 1: ornate silver dagger with red gem hilt angled diagonal; gilded sculpted ruby goblet; open gold treasure chest sparkling with coins and gems; regal sculpted gold crown encrusted with rubies. Row 2: a fierce amber dragon eye framed by obsidian crimson dragon scales (no word WILD); a round volcanic molten fire orb, dark obsidian core split by flowing orange-gold magma fissures with modest integrated flames hugging the silhouette (runtime animated flames will be drawn around it); emerald green dragon egg with exquisite overlapping scales and dark gold bands; ruby red dragon egg with exquisite overlapping scales and dark gold bands. Row 3: sapphire blue dragon egg with exquisite overlapping scales and dark gold bands; iridescent rainbow opal dragon egg with exquisite overlapping scales and dark gold bands; magenta glass elixir bottle with gilded stopper and luminous liquid; ornate gold skeleton key angled diagonal with red gem bow and clearly visible open cutouts.
Style/medium: AAA fantasy casino item art, dimensional sculpted high relief, elegant cinematic painterly realism, impeccable intricate materials and silhouette readability at small size, rich gemstones, polished metallic bevels, dramatic warm key light and cool rim light, jewel tones, mature and premium, fiercely beautiful. Consistent materials and lighting across all 12 icons.
Constraints: isolated items on genuine fully transparent alpha. No surface, no backdrop, no drop shadows outside the objects, no rectangular panels, no circular badge borders, no slot machine, no UI, no lettering, no numbers, no watermark. Every intended background pixel including all gaps, holes, and perforations must be alpha 0. Never paint or simulate a checkerboard; checkerboard must not exist in image pixels. Do not make a poster. Do not add extra objects. Deliver the exact 4-column 3-row atlas, transparent.
```

### Symbol atlas, accepted refinement

The initial atlas was supplied as the edit target, with transparency preserved.

```text
Use case: precise-object-edit. Edit target is the supplied transparent Dragon Slots 12-symbol atlas. Preserve the beautiful detailed original subject designs, original materials, color palette, lighting, exact twelve subjects, and EXACT four-column three-row reading order. Change ONLY their composition/padding: each object must now be fully contained and centered inside the inner 74% width and height of its own equal square grid cell, leaving a generous transparent gap on EVERY side. No part of any object may touch a cell boundary or canvas border. Keep the entire dagger, entire treasure chest and entire crown visible, never crop their edges. Keep the image aspect ratio 4:3 and the exact 4x3 grid. Maintain as much crisp high resolution detail as possible; requested output 2048x1536. Objects must be separate isolated cutouts with clean anti-aliased edges, no residual bright red matte outside silhouettes. Genuine RGBA alpha transparency in all exterior spaces, enclosed holes and key bow cutouts. Never paint or simulate a checkerboard. No text, no backgrounds, no panels, no borders, no shadows beyond silhouettes.
```

### Dragon sanctum background

```text
Use case: stylized-concept
Asset type: production background painting for premium adult dark-fantasy Dragon Slots tabletop game, landscape 3:2 aspect ratio at 1536x1024 or higher.
Primary request: A cinematic ancient dragon sanctum, fierce and beautiful, lavish AAA game key art. A colossal dark crimson dragon curls along the OUTER LEFT and OUTER RIGHT margins of an obsidian cavern, framing a broad empty central void where a slot reel interface will be overlaid. Put the wonderfully detailed horned dragon head at the upper left, its amber eye fierce and luminous, mouth subtly open with hot ember light, and a huge claw and coiling armored tail at the right outer edge. There is no central subject. Keep the entire central 70% of width and middle 78% height VERY DARK, near black desaturated wine and charcoal, quiet soft atmospheric negative space. Intricately detailed obsidian pillars at the far edges, melted gold veins, a restrained dragon hoard in the bottom corners, glowing magma fissures and drifting embers concentrated in corners. Rich fine scales, horns, metallic gold reflections, deep cinematic atmospheric perspective, sculptural red-gold rim lighting, elegant composition for a mature audience, spectacular detail along the edges with dark readable center.
Style/medium: high end painterly photoreal fantasy environment, tangible beautiful dragon materials, cinematic chiaroscuro, elegant and menacing, beautifully crafted.
Constraints: Environment artwork only, full bleed, opaque background. NO lettering, NO symbols, NO slot reels, NO interface, NO logo, NO watermark, NO borders. Do not place bright flames, hot spots, treasure, dragon body, sharp detail, or focal subjects over the broad central quiet area. Outer corners may be brilliant and richly rendered, center must stay very dark. Original design.
```

## Source artifacts

### Supplemental treasure atlas prompt

The accepted 12-symbol atlas was supplied as a style/material reference only, not as the layout to retain.

```text
Use case: stylized-concept
Asset type: supplemental production sprite atlas for the premium adult dark-fantasy Dragon Slots game.
Input image: Image 1 is a STYLE AND MATERIAL REFERENCE ONLY for the gold ruby dragon chest and gemstone rendering. Create a NEW separate atlas. Do not reproduce the twelve-symbol layout.
Primary request: A square 1024x1024 image of EXACTLY FOUR separate objects, in a precise TWO COLUMN by TWO ROW layout, genuine transparent RGBA background. Each object is fully visible and centered in its 512x512 quarter-cell with at least 70 pixels fully transparent clear padding on every side. No object overlaps another cell.
Subjects, exact reading order: TOP LEFT: one ornate treasure chest in the SAME elegant sculptural polished gold and ruby style as the reference treasure chest, facing three-quarter front view, but with its domed lid completely SHUT, large front ruby clasp locked, no visible coins or overflowing contents, powerful dragon filigree and relief work, high quality metallic reflections. TOP RIGHT: one large loose DEEP RUBY RED gemstone, a faceted cushion-to-octagonal cut with subtle three-dimensional tilt, visibly translucent crystalline depth with complex internal refractions, exquisitely polished faces and sharp white specular reflections. BOTTOM LEFT: one large loose vivid EMERALD GREEN gemstone in same cut/style and scale. BOTTOM RIGHT: one large loose rich SAPPHIRE BLUE gemstone in same cut/style and scale.
Style: lavish dimensional AAA fantasy casino asset painting, refined sculpted realism, crisp high-relief detail, compelling mature jewel colors, dramatic warm and cool rim light, rich refracted jewel interiors. All jewels should be visually spectacular, tangible expensive jewels, NOT flat vector polygons. Each gemstone is a SINGLE isolated solid crystal with no metal setting, stand, ring, support, separate sparkles, particles, or hovering accessories. Reflections stay within the silhouette.
Constraints: true alpha0 transparency throughout every exterior background pixel and between all four assets. Clean crisp antialiased edges, no color matte, no ground shadow, no glow or haze outside silhouettes, no background, no checkerboard, no poster, no lettering, no text, no UI, no borders, no watermark. The chest is visibly CLOSED. Full objects entirely within their cells; generous clear transparent gutters. Do not add objects.
```

The supplemental generated source is `C:\Users\abiem\.codex\generated_images\01a0efee-bee6-7563-ab48-07f42849f2e7\exec-dc6ae066-4433-4ef5-b006-cb7aae2ad0a6.png`.

The selected generated outputs were copied unchanged into this directory. Originals remain at:

- Atlas: `C:\Users\abiem\.codex\generated_images\01a0efee-bee6-7563-ab48-07f42849f2e7\exec-c35cd4bd-168d-4820-912a-71845ab4ca75.png`
- Background: `C:\Users\abiem\.codex\generated_images\01a0efee-bee6-7563-ab48-07f42849f2e7\exec-845e6cef-6d35-4473-93fd-5d8a92be51cd.png`

## Hatched dragon character atlas

`slot-dragons.png` was generated with the built-in image tool using the user's screenshot as conceptual inspiration and `slot-symbols.png` as the project's material reference. It contains original green Expand, ruby-red Collect, and sapphire-blue Boost dragons, each emerging from a matching shattered gilded scaly egg. The first row is the quiet proud pose; the second row is the corresponding awakened roar. Runtime animation supplies breathing, rising, pose crossfades, flames, and particles.

- File: `slot-dragons.png`
- Master size: 1536 x 1024 pixels, RGBA PNG, 3,190,365 bytes.
- Layout: 3 columns x 2 rows, exact 512 x 512 source cells.
- Generated source: `C:\Users\abiem\.codex\generated_images\01a0efee-bee6-7563-ab48-07f42849f2e7\exec-d4e87f6c-3337-4a85-aa5f-67c266797c18.png`
- User screenshot reference: `C:\Users\abiem\AppData\Local\Temp\codex-clipboard-34dd92af-7c5f-48ae-88d4-d9a19a2787ad.png`. The original game UI and specific dragon design were not reproduced.

| Pose | x | y | Width | Height | Local visible bounds at alpha > 32 (left, top, right, bottom) |
| --- | ---: | ---: | ---: | ---: | --- |
| Expand quiet | 0 | 0 | 512 | 512 | 62, 17, 486, 507 |
| Collect quiet | 512 | 0 | 512 | 512 | 50, 14, 480, 505 |
| Boost quiet | 1024 | 0 | 512 | 512 | 50, 12, 475, 505 |
| Expand roar | 0 | 512 | 512 | 512 | 24, 2, 502, 483 |
| Collect roar | 512 | 512 | 512 | 512 | 7, 2, 496, 483 |
| Boost roar | 1024 | 512 | 512 | 512 | 16, 1, 494, 483 |

Use the same scale for both poses and anchor the shell base consistently. The roaring row's shell base sits approximately 22 source pixels higher within its cell; a 22-pixel downward destination offset aligns it to the quiet row for crossfading. Do not independently fit the wider roaring wings to the quiet pose width, which would shrink the body during the transition. All substantial silhouettes are contained in their cells; the horn tips and wingtips are complete.

### Dragon transparency and visual inspection

The generated atlas was inspected with `view_image`, and Pillow confirmed RGBA, alpha range 0 to 254, and 594,827 fully transparent pixels. All four canvas corners, column-gutter probes (512,100), (1024,100), (512,510), (1024,510), (512,1023), (1024,1023), and row-edge probes (20,512), (1530,512) have alpha exactly 0. Alpha is preserved unchanged.

The source stores colored RGB values beneath fully transparent background pixels. A viewer that ignores alpha may show the hidden brown/colored background; those colors are invisible under correct alpha compositing. The image contains no painted checkerboard. As with the other generated sprites, some very low alpha antialias residue remains around the silhouettes; inspect the actual native composite for visible fringes.

### Dragon generation prompt

```text
Use case: stylized-concept
Asset type: production six-pose character sprite atlas for a premium adult dark-fantasy Dragon Slots game.
Input images: Image 1 is conceptual inspiration ONLY for a fierce appealing young dragon hatching from an egg; do not copy its exact dragon design, UI, text or composition. Image 2 is the project's material/style reference: polished ornate gold and translucent jewel-colored dragon scales. Create ORIGINAL dragon characters in this style.
Primary request: Exactly SIX fully isolated character sprites arranged in a regular THREE COLUMN by TWO ROW grid on a 1536x1024 landscape canvas, each equal 512x512 cell. Each column contains two poses of the SAME original dragon character with the SAME identity, colors, markings, proportions and egg-shell design. Every entire silhouette stays inside the inner 76% width and 78% height of its cell. Generous perfectly clear transparent margins and gutters; all horns, wings, claws and shell fragments entirely visible with no cutoffs or overlaps. Consistent camera, front-facing three-quarter view, consistent scale, centered shell base and aligned baseline across ALL six cells. Facing mostly forward and slightly toward the viewer's left. Dragons have strong readable proud upright silhouettes, large expressive fierce amber eyes, sculpted brows, swept-back gold horns, small gold belly armor scales and gold claws, detailed leathery wing membranes and gemstone-colored scales. Young dragons are appealing but fierce and majestic, with mature intricate AAA casino quality rather than toy-like cartoons.
Exact layout: TOP LEFT an emerald GREEN dragon in the quiet proud pose, mouth closed and wings half-folded, rising from a shattered emerald green scaly egg with delicate gold filigree matching Image 2. TOP MIDDLE a ruby RED dragon in the same quiet proud pose emerging from a broken ruby red scaly gilded egg. TOP RIGHT a sapphire BLUE dragon in the same quiet proud pose emerging from a broken sapphire blue scaly gilded egg. BOTTOM LEFT the SAME green dragon and shell in a fierce AWAKENED ROAR pose, head raised slightly and jaws open showing ivory fangs and dark throat, chest lifted, wings raised slightly. BOTTOM MIDDLE SAME red dragon in awakened roar. BOTTOM RIGHT SAME blue dragon in awakened roar. The corresponding calm and roar poses must remain at the SAME overall scale with shell bases aligned so the game can crossfade them. Keep the roar small enough that entire wings/horns still fit clear margins.
Style and detail: sculptural richly dimensional fantasy character painting, beautiful detailed high-relief scales, gilded highlights, crystalline jewels, physically convincing polished surfaces, cinematic warm key light and cool blue rim light, crisp materials and readable facial expression at small scale. Original character designs, high-end casino game art for adult players, spectacular yet elegant. Three harmonious color variants. The cracked eggshell is physically beneath and around the dragon's lower body, small fragments attached near its base, not a flat badge or background disc.
Transparent constraints: genuine RGBA alpha transparency. ALL exterior background pixels alpha 0, no checkerboard pixels. No backdrop, floor, ground shadow, exterior glow, aura, haze, flames, smoke, particles, sparkles, or lighting outside character silhouettes; runtime animation adds effects separately. No text, no lettering, no labels, no border, no watermark, no UI. Exactly six dragons, arranged in 3 columns and 2 rows, no extra subjects. Complete silhouettes and ample invisible gutters are essential.
```

## Adult WILD guardian atlases

The adult WILD character was generated as an original design using the supplied video references only for visual guidance. The design uses a mature front-facing ivory/silver dragon, red-and-gold crownlike horns, narrow amber eyes, dark red folded wings, and an elongated armored body. It is distinct from the small header hatchlings. No reference-video pixels, UI, frames, logos, or text were copied into the final assets.

### Files and registered source maps

- `slot-wild-portraits.png`: 2172 x 724, RGBA PNG, 1,450,445 bytes. Three portrait poses, left to right: watchful closed mouth; jaw opening; full forward roar.
- `slot-wild-colossus.png`: 1254 x 1254, RGBA PNG, 2,591,562 bytes. Two complete tall full-body poses, left to right: watchful closed mouth; forward roar.
- Existing background, header hatchling, and other symbol assets were preserved.

Use these registered source rectangles in pixels rather than nominal equal atlas cells. Each atlas has equal source dimensions across its poses, matched centerlines and baselines, and complete silhouettes:

| Atlas | Pose | x | y | Width | Height |
| --- | --- | ---: | ---: | ---: | ---: |
| Portraits | Watchful | 96 | 59 | 600 | 600 |
| Portraits | Jaw opening | 786 | 59 | 600 | 600 |
| Portraits | Full roar | 1476 | 59 | 600 | 600 |
| Colossus | Watchful | 56 | 6 | 570 | 1240 |
| Colossus | Full roar | 630 | 6 | 570 | 1240 |

Portrait alpha > 32 bounds in original master pixels are (117,74)-(674,644), (809,75)-(1363,644), and (1499,75)-(2054,644). The registered 600-pixel crops center those near-identical silhouettes and keep horn/bust padding. The initial portrait generation touched canvas edges; it was rejected and replaced by the accepted padded refinement.

The live forward lean retains the same 600-pixel fitting and pose registration, scales the face about its lower neck, and holds that approach through the breath. Its visible source height eases from 600 to 465 pixels to let the lower bust recede; an alpha mask feathers that moving lower edge. The moving mouth anchor uses the same fitted source and transform. This is a rendering treatment of the unchanged atlas, not an edited bitmap. Live plumes use one procedural reel-space heat field as separate jets become rounded billows and a broad joined inferno that spills above and outside the reel frame. The wall opens through its centre to reveal the opaque full-body guardian from head to feet, followed by a central mouth jet. Its first 3% of length stays fixed at the jaw; independently advected rolling and curling bends and varying width shape the downstream plume. This refinement changes only the unjoined single-mouth jet geometry (`heads == 1`, joining zero), preserving the accepted wall and edge-wisp field. Narrow, irregular side wisps remain from shoulders to feet, flickering across the side borders with moving gaps. They hold for a few seconds, then detach and dissolve upward. The original core coordinates keep mouth anchors aligned while the surrounding render area allows this fire spill. No video frames or fire textures were copied into the assets.

The native `verify_slots_wilds` fixture records eight seconds as 192 PNG frames at 24 fps. It passes separate-breath, seam-growth, inferno-coverage, repeat-clock, control-pixel and independent game-state checks, plus exterior spill, a clear centre, lingering side motion, decay and extinction checks. The existing age-2.55 capture measures nonlinear finishing-jet curvature after removing a best-fit straight line; the double/triple results are 6.33% / 5.81% of cell width, with 40 repeat-clock and 214 control comparisons unchanged. Late-fire measurements compare with the same guardian after extinction to avoid counting its silver/red sprite or static neighbouring frames as flame. Native double/triple finishing-breath stills were visually reviewed, and before/after stage comparisons preserve the accepted wall and side wisps with identical side-coverage measurements. Physical projector/phone acceptance remains pending; the source transparency evidence below is unchanged.

Tall alpha > 32 bounds are (64,19)-(617,1225) and (638,19)-(1191,1225). Both silhouettes are 553 x 1206 with identical top and bottom, so no crossfade baseline offset is needed. Keep the same physical scale and preserve each rectangle's aspect ratio. All horn tips, wing extremities, claws, and tail are complete. Native rendering supplies movement and external fire/electric effects; no effects or WILD typography are baked into these assets.

### Alpha and inspection evidence

Both selected sources were visually inspected with `view_image` and copied unchanged into this directory. Pillow verified RGBA and alpha extrema 0 to 255. Portraits contain 989,526 fully transparent pixels; colossus contains 546,549. All outer corners and representative clear gutters of the portraits are alpha 0. Colossus outer corners, top/bottom center gutters, and outer side probes are alpha 0; the exact center gutter at (627,627) contains a very faint alpha-1 antialias residue, consistent with earlier tool-generated cutouts. The source pixels contain no painted checkerboard. Native compositing should be checked for visible fringes; source antialiasing has not been repainted.

Inspected guidance:
- `artifacts/slots-reel-upgrade/reference/frame-000010.000.png`
- `artifacts/slots-reel-upgrade/reference/frame-000195.000.png`
- `artifacts/slots-reel-upgrade/reference/contact-01.jpg`
- `artifacts/slots-reel-upgrade/reference/wild-0-12-4fps/contact-03.jpg`

Built-in image generation was used throughout. Accepted source artifacts:
- Portraits: `C:\Users\abiem\.codex\generated_images\01a0efee-bee6-7563-ab48-07f42849f2e7\exec-419110a1-67ec-4b8c-8d27-0e2731b13a0b.png`
- Colossus: `C:\Users\abiem\.codex\generated_images\01a0efee-bee6-7563-ab48-07f42849f2e7\exec-dad617e2-be03-4d42-a9e9-2819a50a84ff.png`

### Adult portrait generation prompt

```text
Use case: stylized-concept
Asset type: original adult dragon guardian character portrait sprite atlas for Dragon Slots, premium casino game art.
Reference image: The supplied contact sheet is VISUAL GUIDANCE ONLY. Focus exclusively on the regal front-facing gray/ivory adult dragon guardian in the WILD reel tiles. Create an ORIGINAL character matching that general mature guardian aesthetic. Do not reproduce the reference UI, logos, frames, exact creature design, or any lettering.
Primary request: ONE transparent sprite atlas with THREE equal SQUARE cells side by side, 3 columns x1 row, aspect ratio 3:1 (requested1536x512). Exactly three animation portraits of the SAME mature dragon guardian, same identity and head proportions, same horn topology, same materials, same directly frontal camera, registered at the same scale and neck-base position. Each is an isolated head-and-upper-chest bust, not the full body. All horns and shoulder wing edges fit fully within the inner84% of their own square cell, leaving clean transparent gutters. No element crosses a cell or outer canvas boundary.
Character: majestic ancient adult dragon guardian, directly FRONT-ON, bilateral face, elongated angular ivory/silver metallic-scaled face and armored throat, silver-gray layered cheek plates and strong bony brows, narrow fierce amber-red eyes, long angular muzzle rather than rounded baby snout. A regal crownlike fan of six tall curved horns with deep ruby-red ridges and warm gold/ivory horn tips. Dark red leathery wing membranes tucked behind the neck as restrained silhouettes, and deep charcoal/reddish armored shoulder scales. Pale segmented chest plates, elegant mature regal anatomy, formidable and beautiful, no baby proportions and no huge childlike eyes. Beautiful crisp 3D painterly AAA fantasy material rendering, sculpted high relief, polished highlights, subtle surface texture, restrained blue rimlight ON the scales only, warm gold key light. Match the adult gray-white and red palette in the reference, not a blue baby dragon.
Exact poses: LEFT CELL: guardian steady front-facing closed-mouth watchful proud pose. CENTER CELL: SAME guardian with head reared slightly upward and jaw partly open in preparation to roar, showing ivory fangs and dark crimson mouth. RIGHT CELL: SAME guardian in forceful forward roar with jaws wide open and fangs exposed, still looking straight forward. Keep the head, horn fan and neck-base registered across poses so game can crossfade. Do not change identity, camera angle, scale, horn shape or apparent age between cells. All three equal visual sizes.
Constraints: true RGBA transparent background; all intended exterior background pixels alpha0. No painted checkerboard, no floor, no shadow, no glow, no aura, no flames, no smoke, no particles outside silhouettes. Runtime adds blue fire/electric effects. No WILD text, no text of any kind, no frame or badge, no background, no UI, no watermark. Exactly THREE original portraits, frontal view, fully visible silhouettes, clean antialias edges and generous transparent separation.
```

### Accepted portrait padding refinement

```text
Use case: precise-object-edit
Edit target: the supplied three-pose guardian portrait atlas. Preserve the EXACT original adult silver/ivory dragon identity, every facial design feature, red-and-gold horns, colors, materials, front-facing camera, and the three poses in their exact order. Change ONLY framing and empty padding: zoom OUT so EACH entire portrait occupies only70% of its own equal SQUARE cell. The whole image remains a THREE-column ONE-row triptych,3:1 aspect. Show all COMPLETE horn tips at the top, all shoulder edges at the sides, and a complete curved lower-bust silhouette. The tallest horn MUST end at least10% of one-cell-height BELOW the top canvas edge. Each lowest bust edge MUST end at least10% of one-cell-height ABOVE the bottom canvas edge. Make the portraits substantially smaller inside their cells, with abundant blank transparent space around all sides. No subject touches canvas edges or neighboring cells. All three faces stay at the SAME scale and their lower bust bases align. Restore any horn tips or shoulder extremities cropped from the supplied image. Keep magnificent crisp original detail and fierce adult anatomy. Transparent RGBA background, all exterior background alpha0, clean antialiasing. No text, checkerboard, frame, glow, aura, smoke, shadow, or background. Do not alter dragon identity or three expressions. This is a transparent sprite atlas, not an edge-to-edge banner.
```

### Full-body guardian generation prompt

```text
Use case: stylized-concept
Asset type: full-height adult dragon guardian sprite atlas for merged multi-cell WILD reels in a premium fantasy casino game.
Input image: The supplied three-portrait atlas is the EXACT CHARACTER IDENTITY AND MATERIAL reference. Use the SAME adult silver/ivory dragon guardian, same symmetrical red/gold crownlike horn fan, amber eyes, longangular mature face, white/metallic cheekscales, red leathery wings, and ivory segmented chest. Extend this same original character to full body; no different dragon design.
Primary request: A SQUARE transparent atlas containing exactly TWO very TALL portrait sprites side by side. Each sprite's cell has aspect ratio1:2,width:height. Requested atlas1536x1536, with two768x1536 cells. LEFT: proud closed-mouth full-body guardian corresponding to left portrait. RIGHT: the SAME full-body guardian in fierce frontal jaw-open roar corresponding to right portrait. The two bodies are IDENTICALLY registered: same verticalbodycenterline, same headscale, same camera, same top hornheight, same chest/forelegs/tail position, same feetbaseline. Only necklift and jawopening change subtly for the roar. No spreadwing pose.
Character pose/composition: Directly FRONT-FACING mature guardian standing upright like an ancient regal sentinel, an elongated slender imposing body tailored for a single narrow slot reel spanning2or3 rows. Full elegant neck extends below the magnificent crowned head into a strong muscular chest, layered polished ivory segmented throat/belly armor, dark metallicgray-red side scales, TWO well-defined clawed frontlegs down the sides with elegant gold/ivory talons visible near the lowerbody, backlegs poised beneath, a narrow tail curled neatly behind the feet within the same narrow silhouette. The dark red leathery wings are tucked closely and vertically along the body behind the shoulders, NOT stretched wide. Head and hornfan remain the main focalpoint, but the body fills most of the tallcell so this reads as a continuous fullcreature, not a head floating above empty space. Mature realistic reptilian anatomy, powerful yet graceful. No egg, no baby features, no pedestal. The original ivorysilver/red/gold palette must stay consistent with the reference portraits.
Quality: magnificent intricately detailed 3D painterly AAA adult fantasy casino art, fine sculpted metallic scales, immaculate layered neckplates, softgold highlights and blue rimlight only ON the body, fierce beautiful expression. Complete horns, wing tips, claws, and tail must be visible.
Padding/registration CRITICAL: Fit each ENTIRE fullbody comfortably inside its tallcell with8% emptytransparent padding on every side. Nothing touches any imageedge or central dividingline. Align headcenter, spine and feetbaseline between the two poses. Keep same creature identity and bodily proportions. Exactly TWO originals, calm and roaring, in equalportraitcells.
Transparency constraints: real RGBA alpha. Every exteriorbackground pixel alpha0. No castshadow, no floor, no background, no halo, no fire, no electricity, no smoke, no particles, no aura beyond silhouettes. Runtime provides those effects. No WILDlettering, no text, no frame, no panel, no UI, no watermark, and never any checkerboard pixels.
```
