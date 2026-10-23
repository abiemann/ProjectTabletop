# Duck Hunt artwork

`mallard-photo.png` is an original photorealistic female mallard generated with
the built-in image tool on 2026-10-22, using the user's duck photograph as subject
inspiration. It is a 1312 × 1199 RGBA PNG with a transparent exterior and no ground
shadow. The source reference is not distributed with the application.

Generation prompt:

> Use case: photorealistic-natural. Asset type: high-resolution transparent PNG game sprite for a 4K projected tabletop. Create a NEW original photorealistic full-body female mallard duck, standing naturally on two webbed feet and facing left in a three-quarter side view. The user's reference shows a brown mottled duck with cream feather edges, a softly streaked tan head, glossy dark eye, and a muted yellow-ochre bill. Use that subject and natural photographic quality as inspiration, not the reference's watermark or background. Show exquisitely detailed overlapping brown and cream feathers, realistic proportions, complete bill, tail and both feet. Soft neutral daylight. Center the complete duck with a little empty margin on all sides. No ground plane, cast shadow, platform, props, text, logos or watermarks. The entire exterior and gaps between toes and legs must be genuinely transparent alpha=0, not a checkerboard, matte or colored background. Clean antialiased feather edges. Avoid saturated red markings and cartoon or painted styling. Produce the highest available image detail.

An image-tool cleanup pass preserved the same subject and removed background
haze. With the user's explicit approval, a final script cleared 1,851 residual
pixels with alpha 1 or 2 located more than seven pixels from the opaque duck.
All RGB values and the antialiased boundary were preserved. Alpha validation
confirmed transparent canvas edges and no residue beyond that boundary.

Walking reuses this unchanged PNG. Cached GPU geometry separates the body and
two legs along their feathered roots; the legs alternate a small swing and lift
while the body's centre stays at a fixed height. A brief horizontal turn mirrors
the artwork, and the hit-test silhouette uses the same scale, direction and foot
transforms. No additional raster frames or per-frame image copies are required.

## Mode backgrounds

`arcade-welcome-v1.png` and `hunt-field-v1.png` are original opaque RGB images generated with the built-in image tool on 2026-10-22. The welcome image supplies a colorful wetland, pond and ducks around an open center for the native title and buttons. The field image uses a low grass-level viewpoint, pines on both sides and open sky, inspired by the user's reference. No lettering or controls are baked into either image.

Both files are 1672 × 941 pixels. The prompts requested native 3840 × 2160, but the tool returned these smaller images; they are not native 4K and have not been artificially upscaled. The renderer crops to the physical board aspect instead of stretching the scene and caches each decoded image per graphics device. The field is loaded when Hunt is first opened. Full generation prompts and tool provenance are in [background-prompts.txt](background-prompts.txt).
