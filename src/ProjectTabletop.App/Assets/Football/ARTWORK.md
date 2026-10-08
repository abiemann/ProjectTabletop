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

The car front, boxing glove, frying pan and football boot are original native
vector artwork. Each uses the same physical contact radius. Their team colours,
curved silhouettes, materials and highlights are drawn at render time, without
image or font dependencies. A neutral platinum inlay at the exact centre of
human-controlled kickers illuminates a physical black tip and keeps projected
team colours away from it, improving camera separation without changing the collider.
The menu thumbnail is rendered from this same
artwork and the original spherical football.

All football-board artwork is bundled with Project Tabletop under the project's
license. It introduces no external renderer, downloaded texture or asset package.
