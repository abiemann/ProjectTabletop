# Water Garden sounds

`fountain-loop.wav` restores the previously prepared water-only recording for
the garden's two-ledge slate cascade. The prepared audio is unchanged; playback
is at 27% volume, a 40% reduction from the previous 45% setting.

## Source and license

- Recording: [Trickling Water 3](https://freesound.org/people/Yoyodaman234/sounds/349072/)
  by Yoyodaman234.
- License: [CC0 1.0 Universal](https://creativecommons.org/publicdomain/zero/1.0/).
  The dedication and fallback license are included in `CC0-1.0.txt`.
- Cached source: `artifacts/water-fountain-audio/trickling-water-source.mp3`.
- Source SHA-256: `d1be06c4de3b07415e0c13a93ff27b3700c8c010d4b734457ff1b0bb8baccfb2`.
- The original source page and license evidence are saved locally as
  `artifacts/water-fountain-audio/trickling-water-page.html`.

## Restoration and playback

Restored on 2026-10-14 from the saved `water-only-preview.wav`. Its decoded
analysis windows exactly match the prior final water-only spectral analysis.
The earlier processing recipe was not retained, so restoration copies this
verified prepared loop byte-for-byte instead of trying to recreate its edits.

The shared `DisplaySoundPlayer` repeats the clip continuously using
`MediaPlaybackList.AutoRepeatEnabled`, through the same explicit display-speaker
route used by Football. The volume reduction is applied only in the player,
without further processing or attenuation of the WAV.

The clip is bundled for offline playback and begins only after a completed
garden frame contains emitted fountain water. Loading artwork or elapsed wall
time alone cannot start it. Moving the wand and reusing a cached preview do not
restart it. Reset silences the clip until new fountain water is rendered.
Leaving or hiding the board, calibration, disabling display audio, losing the
output or closing the app stops playback. An absent display endpoint never
redirects the fountain to another speaker.

## Output and checks

- PCM WAV, 48,000 Hz, stereo, signed 16-bit, 32.000 seconds.
- Size: 6,144,044 bytes.
- SHA-256: `7aa54fd5e1a80243b2c03c3b56bcf8eca7ffe07681557ef1c88a86b49f535496`.
- Peak: -2.38 dBFS; RMS: -29.90 dBFS, before playback attenuation.
- Left/right repeat-boundary steps: 0.00516 / 0.00061, below the respective
  99.9th-percentile adjacent-sample steps of 0.07736 / 0.07855.
- Every one-second window contains audio; there is no silent loop gap.
- Measurements are recorded in `artifacts/water-fountain-audio/prepared-loop.json`.

The camera-free `verify_water_garden_audio` check exercises real offscreen
rendering and source physics: the dry source stays silent, emitted water starts
audio only when rendered, and reset waits for newly rendered flow. It also checks
cached previews, repeated updates, navigation, calibration and reveal, stale
callbacks, disposal, and silence without an enabled display endpoint. These
checks do not open the webcam or speakers; listening on the projector remains a
live check.

To restore the exact prepared asset, run `python tools/prepare_water_garden_audio.py`
with NumPy installed. It reads the saved local `water-only-preview.wav` by default;
`--source /path/to/prepared.wav` accepts another copy with the same verified hash.
The script checks format, clipping, continuity and the checksum, then copies the
WAV unchanged and saves measurements in the ignored artifacts directory.
It performs no downloads. The prepared WAV, credits and license are shipped.

## Wand interaction sound

`wand-swish-loop.wav` replaces the bathtub swish with a softer trickling texture
from the same verified pure-water recording used for the fountain:
[Trickling Water 3 by Yoyodaman234](https://freesound.org/people/Yoyodaman234/sounds/349072/),
under **CC0 1.0 Universal**. The bundled `CC0-1.0.txt` covers both loops.
The fountain asset and its playback level are unchanged.

The preparation script reads the checked-in `fountain-loop.wav`, verifies its
SHA-256 above, and selects 12.50–20.75 seconds. It applies a 300 Hz high-pass
and 5,000 Hz low-pass, folds the final 250 ms into the beginning with an
equal-power overlap, and smooths the two file edges over 2 ms. The resulting
8-second mono loop is normalized to -32 dBFS RMS, 4 dB quieter than the previous
wand asset. There is no compression, limiting, pitch change, time stretching,
synthetic layer or additional runtime audio processing.

- PCM WAV, 48,000 Hz, mono, signed 16-bit, 8.000 seconds; 768,044 bytes.
- Output SHA-256: `197fefbcf11b286940c4fa8d7a39db7e216d6328e862a9997781cd27c0e8abef`.
- Peak: -9.75 dBFS; RMS: -32.00 dBFS, before playback attenuation.
- Energy below 300 Hz: 0.204%; no analyzed short window exceeds 1.17%.
- Largest adjacent 25 ms RMS rise: 8.21 dB; every window contains water audio.
- First 500 ms: -33.02 dBFS RMS. Repeat-boundary sample step: zero.
- No clipped samples. Measurements: `artifacts/water-wand-audio/prepared-loop.json`.

These are signal checks; the replacement has not been auditioned through the
projector. The source is the previously approved pure-water recording, with a
separate excerpt and level for wand contact.

A separate shared `DisplaySoundPlayer` repeats this loop at 25% volume while
accepted wand contact remains on the water's upper surface, including stationary
contact. Repeated observations do not restart playback. Contact loss or expiry,
reset, navigation, calibration and lost output stop it. It shares the fountain's
display-speaker routing and mute setting without interrupting the fountain.
The camera-free `verify_water_garden_swish_audio` check covers contact freshness,
excluded surfaces, lifecycle stops, deferred callbacks and unavailable endpoints.

To regenerate, run `python tools/prepare_water_swish_audio.py --ffmpeg /path/to/ffmpeg`
with NumPy installed. It uses the bundled fountain WAV by default. `--source`
accepts another byte-identical copy, checked before preparation. The script
rejects silent gaps, abrupt short-window bursts, excess low-frequency energy,
clipping and a discontinuous repeat boundary. It performs no downloads or playback.
