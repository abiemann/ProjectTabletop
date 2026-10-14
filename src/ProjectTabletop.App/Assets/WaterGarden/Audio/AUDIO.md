# Water Garden fountain sound

`fountain-loop.wav` uses the selected **Swale** water recording for the garden's
two-ledge slate cascade. The clip is trimmed and its repeat boundary is overlapped;
there are no added audio layers, filters, gain changes or runtime sound synthesis.

## Source and license

- Recording: [Swale](https://commons.wikimedia.org/wiki/File:Swale.ogg)
  by Siddharth Patil (Ksd5 / Sidpatil).
- License: [CC0 1.0 Universal](https://creativecommons.org/publicdomain/zero/1.0/).
  The dedication and fallback license are included in `CC0-1.0.txt`.
- Local source: `artifacts/youtube-water-garden/audio/Swale-Siddharth-Patil.ogg`.
- Source SHA-256: `b71def4798508e588229a5f801fefaed9ac51c5dbad224274123c92e6d183fff`.

## Preparation

Prepared on 2026-10-14 using `tools/prepare_water_garden_audio.py`. The script
decodes the first 31 seconds of the local source to 48 kHz mono. A one-second
equal-power circular overlap closes the boundary into a 30-second loop. No EQ,
layering, loudness normalization or additional gain is applied. The app plays it
at 45% volume through the same explicit display-speaker route used by Football.
The shared `DisplaySoundPlayer` repeats the clip using `MediaPlaybackList.AutoRepeatEnabled`.

The clip is bundled for offline playback and begins only after a completed
garden frame contains emitted fountain water. Loading artwork or elapsed wall
time alone cannot start it. Moving the wand and reusing a cached preview do not
restart it. Reset silences the clip until new fountain water is rendered.
Leaving or hiding the board, calibration, disabling display audio, losing the
output or closing the app stops playback. An absent display endpoint never
redirects the fountain to another speaker.

## Selected output and checks

- PCM WAV, 48,000 Hz, mono, signed 16-bit, 30.000 seconds.
- Size: 2,880,044 bytes.
- SHA-256: `c658f48098c1dcc53ab7f67e61c584f91c43f62c883691a0608bd68a5594957d`.
- Peak: -3.10 dBFS; RMS: -29.67 dBFS.
- The repeat-boundary step is 0.00580 and the overlap's internal join step is
  0.00824, both below the recording's 99.9th-percentile adjacent-sample step of
  0.12982. Every one-second window contains audio; there is no silent loop gap.
- Full measurements are recorded in `artifacts/water-fountain-audio/prepared-loop.json`.

The camera-free `verify_water_garden_audio` check exercises real offscreen
rendering and source physics: the dry source stays silent, emitted water starts
audio only when rendered, and reset waits for newly rendered flow. It also checks
cached previews, repeated updates, navigation, calibration and reveal, stale
callbacks, disposal, and silence without an enabled display endpoint. These
checks do not open the webcam or speakers; listening on the projector remains a
live check.

To reproduce, run `python tools/prepare_water_garden_audio.py --ffmpeg /path/to/ffmpeg`
with NumPy installed. It reads the local source path above by default;
`--source /path/to/Swale-Siddharth-Patil.ogg` selects another copy of that same
recording. The script verifies the source hash and saves measurements in the
ignored `artifacts/water-fountain-audio` directory. It performs no downloads.
Only the prepared WAV, credits and license are shipped.
