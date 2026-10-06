# Generated menu control reflectance fixture

Both PNGs contain only app-generated artwork from the isolated native compositor
verification on October 5, 2026. No live camera, person, hand, object, room, or user
photograph supplied any pixels. The `camera` filename describes the detector's
simulated camera input: it is a 1000 × 1000 native render of the menu, including its
calibrated projection transform. The `expected` image is the compositor's separate
1000 × 1000 board reference. The JSON records their generated geometry and masks,
without live diagnostics, paths, tracking identities, or capture timestamps.

The regression changes only an unlabelled part of the Blackjack card. It verifies
byte for byte that the distant menu arrow remains unchanged. All eight captions
still match their generated shapes. Previously, changing that other card moved
the global camera-colour fit enough to claim fresh reflectance on the untouched
arrow: 8.5% control coverage, 16.1% caption coverage, and 68.8% ink coverage. Two
fresh frames then produced a false search-light hint.

`--hand-reflectance-fit` replays cold and warm detectors, eight consecutive frames
of the unrelated card change, and removal. It requires every caption to remain
intact and forbids false reflectance confirmation or acquisition hints. Separate
compact-control and optical-history regressions retain real readable lettering
projected onto a locally changed surface, both 7% area floors, stationary fingers,
fresh-frame confirmation, and removal.

The PNGs were converted losslessly from the generated-only failure evidence. The
verifier constructs an isolated compositor without a live MainWindow, camera,
output window, or control pipe; the source is `VerifySharedBoardAcquisitionAsync`
in `MainWindow.SharedBoardAcquisitionVerification.cs`. The regression paints the
recorded edge rectangle BGRA `(75, 95, 185, 255)` at its original calibrated
coordinates. Fixture images are used only by offline regression verification,
never by application startup or tracking.
