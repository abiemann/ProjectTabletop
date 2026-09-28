# Native Paint control optical history

These fixtures reproduce the false illumination reported on September 28, 2026.
The camera was 1920 × 1080 with the board viewed at its calibrated 180-degree
orientation. Exit was visibly intact, but the running detector retained an older
vertical text registration at -8 logical pixels. Its correlation fell to about
0.38 and it measured over 9% disturbance on the untouched button.

- `paint-idle-controls-camera.png` preserves only the two generated Exit/Save
  control panels and a 12-logical-pixel registration margin from a hand-free
  camera frame. It retains the original native dimensions and camera optics.
- `paint-idle-controls-expected.png` preserves the corresponding generated app
  control patches in their original 1000 × 1000 board reference.

Every pixel outside those two narrow patches is opaque black. These fixtures
contain no hand, object, artwork, tabletop, room, or other user camera content.
The regression retains the exact calibration and generated label bounds.

The test first verifies a cold clean frame, then establishes a valid registration
with a vertically shifted version of the same control pixels. Restoring the
original clean camera frame reproduces the old detector's failure. It checks
Exit alone shifting while Save remains untouched, and both controls shifting.
The recovered optics must then recognize stationary synthetic four-finger
strips after two fresh frames, retain both independent 7% coverage requirements,
keep untouched Save unlit, and remove illumination when the strips disappear.
