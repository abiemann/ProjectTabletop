# Local drawer glyph integrity recordings

These fixtures were captured from the user's Water Garden board on 2026-10-16
with the Elgato Facecam 4K at 1920x1080. Only the lower-left control area remains;
all other camera and generated-scene pixels have been replaced with opaque black.
The original camera geometry, control bounds and pixels inside that area remain
unchanged. The pictures are regression evidence, not game assets.

`drawer-ink-clear.png` is the unobstructed triangle and separate chevron at
07:05:57 UTC. `drawer-ink-held-9.png` through `drawer-ink-held-19.png` are eleven
successive steady-finger captures from the following recorded hold. Most of the
projected symbol is still recognizable on the fingers, but a local stroke is
broken at a finger seam. Whole-glyph correlation remains above .80, so the old
full-height sectors incorrectly kept these frames clear.

`drawer-spread-clear.png` is an unobstructed earlier chevron/underline at
06:42:09 UTC. A small uniform expansion of its dark ink preserves the arrow but
reduces correlation around its apex. It is a negative for local integrity:
correlation loss alone must not manufacture missing original ink.

Each prefix has its matching generated reference and camera-to-board metadata.
Both arrow recordings explicitly identify control 0 as a vector glyph. Local
stroke checks apply only to those renderer-identified symbols; ordinary text
keeps its existing caption/sector matcher.

`native-drawer-raster` is generated renderer evidence from the 2026-10-16 Globe
verification. The 3840x2160 open drawer and 1000x1000 expected reference are
unchanged within their bottom control row. Camera mapping is reconstructed from
the native verification's documented projector corners and saved calibrated map.
Its intact ZOOM- lettering has a subpixel raster displacement of the Z's top
stroke. Only the arrow at control 0 is tagged as vector ink; all four captions
must remain clear. The recorded images contain no camera photograph.

Tests also apply brightness, colour, smooth shading, clipping and optical
softness to the clear recordings. The same glyph rules apply to every board;
button plates, borders, colour changes and hand presence do not qualify a hold.
