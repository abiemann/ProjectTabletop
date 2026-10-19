# Football dark-turf marker fixtures

Captured from the user's live Football session on 2026-10-18 (Pacific time).
The saved marker was learned under brighter illumination. In the stationary
failure, the existing thresholds joined its two crisp black strips to the dim
pitch, leaving no accepted strips even with the projected-artwork veto disabled.
In the moving failure, a nearby white goal line dominated the artwork comparison
and incorrectly vetoed physical ink even though the rendered scene contained no
matching dark strip.

- `football-dark-turf-held.png`: stationary failure, 2026-10-19 02:51:40.3587342 UTC.
- `football-dark-turf-moving.png`: first sustained moving failure, 2026-10-19 02:51:02.3849249 UTC.
- `football-dark-turf-clear.png`: same pitch after the user removed hands and marker,
  captured in the 2026-10-19 02:53:21 UTC diagnostic session.

These are unchanged camera pixels cropped from 1920 x 1080 at `(600, 520)`
to 640 x 512. No resizing, colour correction or sharpening was applied.
The test rescales only the saved profile's normalized area for the crop.
It does not provide a recent pose hint or relearn the marker from a failure.

The moving case includes all three original rendered reference frames as PNGs.
Their pixels and timestamps are unchanged. The saved homographies translate
crop coordinates by `(600, 520)` before mapping to board coordinates. This keeps
the production artwork rejection active at the goal line. The stationary case
also verifies recovery without projected references or a recent pose.

The `held-full` and `clear-full` fixtures retain the complete 1920 x 1080 camera
frames. Their corresponding reference metadata contains the original unmodified
camera-to-board maps, search polygon, and every available projected frame and
timestamp. These catch interactions with contours on the opposite side of the
pitch that the smaller crops exclude. No pose hint is supplied. All PNG pixels
round-trip to their original BGRA captures unchanged.

Source diagnostic folders in the local `ProjectTabletop/FootballDiagnostics` directory:

- `20261019-025140-76ad7dc21e24475d98ec12bc92d332bf` (`current` and `missed`).
- `20261019-025321-c0bbde6ad530410dba334ab89a7cd4e6` (`current`, clear pitch).

The tests require recovery of the physical pair, rejection of the clear pitch,
and rejection when the same camera image is supplied as projected artwork.
Synthetic tests separately cover brighter training followed by dimmer use,
single strips, ambiguous triples and a dark rectangle with a lighter internal line.
