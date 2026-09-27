# Hand regression fixture

`mediapipe-pointing-up.jpg` is an unmodified copy of MediaPipe's
`tasks/testdata/vision/pointing_up.jpg` (358 x 376 pixels).

- Upstream project: [google-ai-edge/mediapipe](https://github.com/google-ai-edge/mediapipe).
- Verified revision: `2863977e6672bb8c847e34402798e2f743745c60`.
- [Immutable asset URL](https://storage.googleapis.com/mediapipe-assets/tasks/testdata/vision/pointing_up.jpg?generation=1782185079090086).
- SHA-256: `ecf8ca2611d08fa25948a4fc10710af9120e88243a54da6356bacea17ff3e36e`.
- [Upstream download manifest and checksum](https://github.com/google-ai-edge/mediapipe/blob/2863977e6672bb8c847e34402798e2f743745c60/third_party/external_files.bzl).
- [Upstream package declaration](https://github.com/google-ai-edge/mediapipe/blob/2863977e6672bb8c847e34402798e2f743745c60/mediapipe/tasks/testdata/vision/BUILD)
  lists this image and declares `licenses = ["notice"] # Apache 2.0`.

Copyright the MediaPipe Authors. Distributed under the Apache License, Version
2.0; the complete upstream license is included as `MEDIAPIPE-LICENSE.txt`.
The file was downloaded on 2026-09-26 from the equivalent original
`https://storage.googleapis.com/mediapipe-assets/pointing_up.jpg` URL; its hash
matches the revision-pinned manifest above.

## Independent landmark reference

[MediaPipe's expected landmarks](https://github.com/google-ai-edge/mediapipe/blob/2863977e6672bb8c847e34402798e2f743745c60/mediapipe/tasks/testdata/vision/pointing_up_landmarks.pbtxt)
place the index fingertip (landmark 8) at normalized coordinates
`(0.47388697, 0.19592366)`, approximately `(169.65, 73.67)` pixels. The regression
uses those published values, not coordinates produced by this app. An 18-pixel
tolerance accommodates differences between the upstream landmark model and
the OpenCV Zoo conversion. The wider frame allows 24 pixels because its hand
occupies less of the palm detector input. Both tolerances remain much smaller
than the separation from the folded fingers or wrist.

The regression also rotates the photograph by 90 degrees, embeds it off-center
in a wider frame, adds camera row padding, and places two separated copies
(one mirrored) in a single frame to check two-hand detection. These
transformations occur in memory; the source image remains unchanged. Existing
board photographs and uniform frames are negative controls. This is an offline inference and
coordinate-mapping check; live projection, motion, and occlusion still need
hardware validation.
