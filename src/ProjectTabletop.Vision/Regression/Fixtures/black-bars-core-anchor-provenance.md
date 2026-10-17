# Dark-core anchor recordings

These regression photographs were captured on 2026-10-16 from Water Garden at
1920x1080 after the room lighting changed. Only raw camera pixels in rectangle
(760,650)-(1000,860) remain; the surroundings are opaque black. The retained
pixels, full frame dimensions and learned marker geometry are unchanged.

`held-1` and `held-4` correspond to 08:36:22.0459063 UTC and
08:36:24.4960335 UTC in the saved lighting capture. Both physical bars are dark,
but pebble cracks attach to their normal-threshold contours. The existing darker
mask measures separate valid strips, whose imperfect contours cannot form a
pair. These strips must remain eligible to anchor actual four-edge measurement.
The prior detector discarded them and returned no pair. No historical position
or predicted geometry supplies a positive in this test.

`clear` is the same water region after the marker was removed, at 08:37:35 UTC.
It must remain empty. Each held image is also supplied as its own projected scene
reference in a negative test, ensuring recovered strips still pass the artwork
veto. The photographs are regression evidence, not game assets.

`held-ordinary` at 08:36:43 UTC already had a successful ordinary-anchor edge
measurement. An added darker-core fit must not replace its representative with a
subpixel-shifted measurement that moves the separating-gap samples onto pebbles.
All alternative fits still participate in strip identity and ambiguity checks.

`held-shadowed-gap` at 08:55:20 UTC retains rectangle (1160,720)-(1400,930).
During movement, a projected pebble bridge joins both original masks. A single
stricter core pass separates the physical ink. Three gap samples remain clear
at the original threshold and span five sample intervals; a compact bright
fleck or uniformly dark stripe cannot supply that distributed clear evidence.
