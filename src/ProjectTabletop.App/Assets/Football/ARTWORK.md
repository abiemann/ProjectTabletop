# Football artwork

The football's native spherical markings are ported from Alexander Biemann's
`SpotBopAndroid` project, as requested by the author on 2026-10-07. The source is
`app/src/main/java/biemann/android/tippytappy/scene/shared/SoccerBallRenderer.kt`.
`Projection/Football/SoccerBallRenderer.cs` preserves its twelve spherical
pentagons, thirty seams, panel colours and hemisphere clipping. A quaternion
from the game simulation rolls the markings around the ball. Project Tabletop
adds continuous spherical lighting and a separate height-dependent cast shadow.
No SpotBop branding, third-party sound or bitmap has been copied.

The pitch is original deterministic native artwork created for Project Tabletop
on 2026-10-07. Fine grass blades, cut tips, irregular patches, mower stripes and
sunlight are drawn into a device-owned cached Win2D surface. Chalk lines and
netted goals are native geometry. The supplied football-pitch photographs were
used only as visual references for the overhead composition, turf and shadows;
their pixels and watermarks are not included.

The boxing glove, frying pan and football boot are original native vector artwork.
Their team colours, curved silhouettes, materials and highlights are drawn at
render time. The car front is the bundled `car-front.png` cutout described below.
All four appearances use the same physical contact radius. Bar-anchored kickers
sit ahead of the whole marker, with a clear gap that helps keep projected artwork
away from the physical bars. Fingertip-controlled kickers retain a neutral
platinum inlay at their exact centre. The menu thumbnail uses this same artwork
and the original spherical football.

## Car-front cutout

Alexander supplied a top-down white car with red bonnet stripes on 2026-10-11
(`codex-clipboard-aae0e024-371b-4644-8a16-ae3ef7e935d7.png`) and requested removal
of everything behind a yellow curve across its windscreen, including the line.
The built-in image-generation tool prepared `car-front.png` from that image.
The result is an RGBA PNG; the exterior corners and removed windscreen region
have alpha zero, and the yellow annotation is absent. A shadow is rendered from
the sprite's alpha at runtime, rather than baked into the image. The bitmap is
decoded once per graphics device and reused for both play and the menu thumbnail.

Edit prompt:

> Precise-object-edit of attached image, for a football game's car-front sprite. Preserve the exact supplied overhead white sports car with red hood racing stripes, headlights, hood, windshield wipers and remaining lower windshield, same perspective and details. The car's FRONT is the BOTTOM of the image. REMOVE EVERYTHING BEHIND/ABOVE the curved YELLOW LINE on the windshield, and remove the ENTIRE yellow line itself. The new back edge of the cutout follows that same curved line: ends higher at both sides, dipping lower at windshield center. Retain ONLY the portion of the car BELOW the yellow curve, from that cropped windshield edge through hood to front bumper. Do not rebuild the missing rear half or upper windshield; do not change the car design. Remove the surrounding dark screenshot border, gray rectangular backdrop, background, floor, cast shadow, any detached mirror parts outside/beyond the cut. Exact car silhouette cutout, tightly framed with a small transparent margin. Output PNG with REAL alpha transparency, background pixels alpha zero, clean antialiased edges. NO checkerboard pixels, NO matte, NO exterior shadow, NO yellow line, NO text.

Original native football artwork is covered by the project's license. The car
cutout derives from the user-supplied image; this provenance record does not
establish a separate license for that source image.
