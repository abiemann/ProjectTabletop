# Clear second menu page

Captured from the local Project Tabletop camera/projector session on 2026-10-17
at 03:19 UTC, with the board clear of hands and markers. The generated menu
contains Roulette, Paint, Photo Copy and Blackjack thumbnails.

The JSON retains the exact camera dimensions, calibration map and control masks.
The gzip files contain lossless BGRA camera and generated-reference buffers.
`camera-adjacent` is the adjacent clear PNG capture converted from BGR to BGRA
without changing its colour values.

This case exposed false whole-board obstruction evidence on the saturated Paint
thumbnail when the photometric model fitted only caption plates. Regression also
checks cold and stationary synthetic obstructions over this recorded artwork,
including a bright obstruction and a dim obstruction in the central gap.
