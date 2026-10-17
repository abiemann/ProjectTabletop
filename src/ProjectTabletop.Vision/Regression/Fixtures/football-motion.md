# Football marker movement recordings

These two raw 1920x1080 camera frames were captured on 2026-10-16 with the
same guided Football profile as the dim-gap recordings. The PNG files preserve
512x384 crops with unchanged pixels. Tests rescale only normalized area to keep
the original marker pixel geometry.

- `early`: 16:51:12 UTC, origin (660,320). A blurred partner has long-edge
  transitions around 22–24 grayscale levels over the original four-pixel probe.
  Bounded wider contrast confirmation at the same narrow local maxima recovers
  the pair without changing the required contrast or learned shape. The frame
  supplied as its own projected scene must still be rejected. Full-frame replay
  also recovers it with the actual saved scene history.
- `gap-axis`: 17:17:49 UTC, origin (540,440). The test supplies opposing edges
  measured offline to isolate the gap check: sampling along only the first strip
  changed its answer when strip order was reversed. The shared aligned axis must
  give the same answer for either ordering and either endpoint direction. This
  is not an end-to-end acquisition assertion; the detector still rejects this
  more heavily blurred frame.

A later 17:22 fast-movement frame is also still rejected and remains in the
ignored diagnostic captures. These regressions do not claim successful live
tracking of every frame. The photographs are detector evidence, not game artwork.
