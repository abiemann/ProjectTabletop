# Compact Paint control regression

These PNGs preserve the actual Win2D output captured by the isolated native
shared-board verification on September 28, 2026. They contain generated app
graphics and synthetic colored finger strips, with no user webcam pixels.

- `paint-compact-controls-expected.png`: the generated board-UV control reference.
- `paint-compact-controls-empty.png`: the same generated board projected through
  the verification fixture's calibrated transform.
- `paint-compact-controls-occupied.png`: that projected board with four stationary
  synthetic strips over Exit. Save is untouched.

All images retain their original 1000 × 1000 BGRA pixels. The regression stores
the exact camera-to-board transform and opaque control bounds. Its label regions
are the rendered bright glyph bounds plus four pixels of registration margin.

The former free x/y photometric fit absorbed the Exit obstruction and produced
only a false Save candidate. Compact controls that also provide all lighting
references now fit color and exposure without those underconstrained spatial
terms. Independent reference panels retain the full model. The regression checks
the first occupied frame, a clean-reference-first sequence, stationary evidence,
both 7% coverage gates, and removal.
