# Photo Copy object silhouettes

`photocopy-remote-alpha.png` contains only the grayscale segmentation alpha of the
user's remote control, captured during the authorized local Photo Copy debugging
session on 2026-09-27. It contains no camera RGB, room background, or person.

The long, slightly tapered object has rounded ends. Its main outline simplifies
to six corners despite filling about 96% of its enclosing rotated rectangle.
This reproduced the incorrect circular spotlight caused by requiring exactly
four corners. The regression places this silhouette on a synthetic neutral board
and checks rectangular classification and complete illumination coverage.

`photocopy-angled-rectangle-alpha.png` contains only the grayscale segmentation
alpha of a second rectangular object from the same authorized local debugging
session. It contains no camera RGB, room background, or person. The object was
placed diagonally on a non-square board; normalizing that board to square image
coordinates turned the rectangle into a parallelogram. Its four fitted corners
had an absolute corner cosine of about 0.224, just beyond the former 0.22 limit,
and it occupied about 85.2% of its minimum-area enclosing rectangle. The test
checks affine rectangular classification, edge alignment, complete illumination
coverage, and object presence under the fitted spotlight.
