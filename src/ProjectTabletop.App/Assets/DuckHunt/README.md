# Duck Hunt artwork

## Mode backgrounds

`arcade-welcome-v1.png` and `hunt-field-v1.png` are original opaque RGB images generated with the built-in image tool on 2026-10-22. The welcome image supplies a colorful wetland, pond and ducks around an open center for the native title and buttons. The field image uses a low grass-level viewpoint, pines on both sides and open sky, inspired by the user's reference. No lettering or controls are baked into either image.

Both files are 1672 × 941 pixels. The prompts requested native 3840 × 2160, but the tool returned these smaller images; they are not native 4K and have not been artificially upscaled. The renderer crops to the physical board aspect instead of stretching the scene and caches each decoded image per graphics device. The field is loaded when Hunt is first opened. Full generation prompts and tool provenance are in [background-prompts.txt](background-prompts.txt).

## Flying mallard

`flying-mallard-body.png` (1983 × 793) and `flying-mallard-wing.png` (1024 × 1536)
are original photorealistic female mallard layers generated with the built-in
image tool on 2026-10-23. The horizontal body faces right, with tucked feet; the
separate wing preserves individual feather tips and a blue speculum. Reusing the
wing behind and in front of the body permits continuous wingbeat animation from
two cached textures instead of a large animation sheet.

Both files are RGBA PNGs with verified alpha-zero exteriors. With the user's
explicit approval, an alpha-only cleanup cleared 801 nearly invisible body
pixels and 1,013 wing pixels more than seven pixels from the main feather
component. All RGB values and antialiased feather boundaries were preserved.
Generation prompts, hashes, alpha bounds and cleanup details are recorded in
[flying-mallard-prompts.txt](flying-mallard-prompts.txt).

## Target Practice saloon gallery

`target-practice-saloon-v1.png` is the user-approved original saloon environment
generated with the built-in image tool on 2026-10-24. It is copied unchanged from
the approved concept: a front-facing Gilded Age New York saloon gallery with
carved walnut, brass details, two empty target shelves and a lower conveyor.
It is a native 1672 × 941 RGB PNG, not native 4K and not artificially upscaled.
Targets and controls are supplied by the application, not baked into the image.
Its full prompt and hash are in
[target-practice-saloon-prompts.txt](target-practice-saloon-prompts.txt).

`gallery-golden-ceiling-v3.png` is the user's selected third image, an opaque
1299 × 1211 RGB full-scene outpaint made with the built-in image tool on
2026-10-25. The file is copied unchanged. The renderer samples only its ceiling
plane and extends it behind a cached silhouette of the original carved cornice
and column caps. This replaces the thin strip of old ceiling while preserving
the original wooden cornice. The lower cabinet opening is stretched at render time
from 122 to 170 source pixels, matching the middle opening. Side posts extend with
it, while the conveyor and lower panel move down 48 pixels. Target artwork keeps
its original proportions and controls remain in place. Wide boards without
a top margin do not draw the ceiling. The image is cached with the gallery
artwork; it is not native 4K and has not been artificially upscaled. See
[gallery-golden-ceiling-v3-prompts.txt](gallery-golden-ceiling-v3-prompts.txt).

`gallery-steampunk-target.png` is an original 1177 × 1337 RGBA target assembly
generated with the built-in image tool on 2026-10-24. Its round, visibly fibrous
ivory paper has two printed charcoal rings and a central plus. An aged brass and
blackened-steel retaining frame, four clips and a short pedestal visibly support
the paper. The renderer preserves the native aspect so the rings stay round;
paper hit testing and puncture clipping share the measured surface geometry and
exclude the metal clips.

Scoring follows the printed artwork, separately from that physical paper outline.
Fits to 96 dark-ring samples in the native image give an inner circle centred at
(585.0, 535.3), radius 160.8 pixels, and an outer circle centred at (585.8, 529.5),
radius 356.7 pixels. The first paper hit awards 100 inside the inner ring, 50 in
the surrounding band, or zero outside the outer ring. The source PNG is unchanged.

The middle-row challenge reuses this assembly and puncture artwork. Its holder
travels along the shelf; the paper and retaining ring turn around the vertical
stem while the circular pedestal remains supported. The paper turns away at the
earlier of its three-second facing limit or half a second after its first hit;
repeat hits do not extend that delay. The rising middle backdrop
is a cached crop of the original saloon panel, clipped inside its ornate frame.

With the user's approval, an alpha-only cleanup removed 486 isolated alpha-1
background pixels more than seven pixels from the object. Decoded-file checks
confirmed unchanged RGB and object-edge alpha, with transparent canvas edges.
Measured paper coordinates, prompt history and hash are in
[gallery-steampunk-target-prompts.txt](gallery-steampunk-target-prompts.txt).

`gallery-paper-puncture.png` is an original 1254 × 1254 RGBA decal generated with
the built-in image tool on the same date. A near-opaque black opening is bordered
by a compact grey cardboard-fibre rim with small broken white paper flaps curling
inward. The application can place opaque black beneath the opening when drawing
the decal. No sheet rectangle, exterior shadow or matte is included.

After one image-tool cleanup pass, the user's approved alpha-only cleanup cleared
185 isolated alpha-1 background pixels more than seven pixels from the paper's
main component. All RGB values and the antialiased torn edges were preserved;
exterior transparency was verified from the decoded PNG. Details and prompts are
in [gallery-paper-puncture-prompts.txt](gallery-paper-puncture-prompts.txt).

`gallery-crystal-skull.png` is an original 1254 × 1254 RGBA crystal skull generated
with the built-in image tool on 2026-10-25. Its polished facets, refraction, teeth
and eye sockets are photographic artwork. The renderer reuses it for the five
upper-shelf targets, with animated side-light reflections and fragments sampled
from the same crystal texture.

Laser-hover feedback uses a cached, socket-shaped warm yellow glow of those same
crystal facets. The source PNG is unchanged. The tint fades with the current
laser position and is composed without bright red hotspots that could resemble
an aiming dot.

After a targeted image-tool cleanup attempt left exterior residue, the user
approved clearing precisely 2,697 nearly invisible exterior pixels: 2,683 at
alpha 1 and 14 at alpha 2. All RGB values and every other alpha value are
unchanged. Canvas edges and representative exterior pixels are alpha zero.
The native dimensions, measured silhouette, hash, prompts and exact cleanup
criteria are recorded in [gallery-crystal-skull-prompts.txt](gallery-crystal-skull-prompts.txt).

`gallery-unlit-lamps.png` is a 1672 × 941 image-tool edit of the saloon, with
only the two main chimney flames removed. The renderer samples this image only
inside the physical glass-chimney outlines, retaining the original saloon
everywhere else.

`gallery-candle-flames.png` contains eight photographic flame poses in a 4 × 2
sheet at 1774 × 887 native pixels. It is deliberately opaque RGB on black for
additive light composition, not a transparent cutout. Both PNGs are unmodified
built-in image-tool outputs. The renderer removes the tiny black-level residue
in its cached light texture and balances blue light against the warm glass so
the projected base does not resemble a magenta aiming dot. It anchors the flames
at the wicks, blends adjacent poses, and uses independent lamp timing. See
[gallery-lamps-prompts.txt](gallery-lamps-prompts.txt) for prompts, hashes and
measured placement details.

## Decorated covers and middle messages

`gallery-upper-caravan.png` supplies the mirrored covered wagons, Western star
and cartridge ornament on the upper curtain. `gallery-lower-peace-pipe.png`
supplies the inward-facing Native American figures and central peace pipe on
the lower hinged cover, revised to match the caravan's muted umber and cream
engraving palette. Both are original, transparent built-in image-tool
artwork. Their prompts, dimensions, hashes and approved alpha-only cleanup
are recorded in their corresponding `*-prompts.txt` files.

The shared cover compositor caches each engraving over its original beige
surface, preserving natural proportions and the lower opening's taller layout.
Both closed covers use high-quality image sampling; the rotating lower flap
resolves at the board's output resolution to preserve the engraving's fine detail.
Decoration follows the moving front; the lower flap's wooden reverse is separate.

The middle cover uses the lighter, unoutlined [Smokum font](../Fonts/README.md) for PRESS PLAY
before a round and GAME OVER with a smaller replay prompt after completion.
These vector messages are hidden during play; the moving-target stage remains.
