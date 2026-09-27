# Fresh hand-candidate continuity

`hand-candidate-continuity.json` contains only numeric landmark coordinates,
model confidence, handedness scores, and previous-bounds IoU from eight local
camera detections on 2026-09-27. It contains no camera pixels or video. The
sequence numbers identify the original observations for local replay.

On each frame, periodic tiled palm search produced an alternative inference of
the already tracked hand. Its confidence exceeded the fresh tracked-ROI fit by
only 0.000450–0.001869, despite six of the search fits shortening the extended
fingers substantially. The current tracked fits overlapped their preceding
frame's bounds by 0.970849–0.996132. Simple score ordering selected the tile,
which made the hand spotlight contract for one frame before recovering.

The regression exercises selection on these exact fresh candidate sets, then
checks counterexamples where search must retain priority or find another hand.
It does not smooth, interpolate, or substitute previous-frame landmarks.
