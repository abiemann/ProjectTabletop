# Local hand models

The app bundles the full-precision OpenCV Zoo MediaPipe palm detector and hand
landmark estimator. Inference runs on the local CPU using OpenCvSharp DNN. No
camera images are uploaded and no model download is needed when the app runs.

Source: [OpenCV Zoo](https://github.com/opencv/opencv_zoo/tree/47534e27c9851bb1128ccc0102f1145e27f23f98/models),
pinned at `47534e27c9851bb1128ccc0102f1145e27f23f98`.

| File | Bytes | SHA-256 |
|---|---:|---|
| `palm_detection_mediapipe_2023feb.onnx` | 3,905,734 | `78ff51c38496b7fc8b8ebdb6cc8c1abb02fa6c38427c6848254cdaba57fcce7c` |
| `handpose_estimation_mediapipe_2023feb.onnx` | 4,099,621 | `db0898ae717b76b075d9bf563af315b29562e11f8df5027a1ef07b02bef6d81c` |

The downloads were checked against the SHA-256 hashes and sizes in the upstream
Git LFS pointers. Both model directories are distributed under Apache License
2.0; their original licenses are included beside the models. The C# hand detector
adapts preprocessing and coordinate recovery from the same revision's
`mp_palmdet.py` and `mp_handpose.py`.

Upstream model documentation:

- [Palm detector](https://github.com/opencv/opencv_zoo/tree/47534e27c9851bb1128ccc0102f1145e27f23f98/models/palm_detection_mediapipe)
- [Hand landmark estimator](https://github.com/opencv/opencv_zoo/tree/47534e27c9851bb1128ccc0102f1145e27f23f98/models/handpose_estimation_mediapipe)

The hand estimator produces 21 landmarks. Landmark 8 is the index fingertip. Its
predicted relative depth and handedness are not proof of board contact. This
prototype uses only the two-dimensional camera location for the fingertip cursor.
