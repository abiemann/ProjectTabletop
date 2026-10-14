"""Prepare a gentle wand-water loop from the bundled, verified pure-water recording.

Requires NumPy and FFmpeg. Reads local files only; never plays audio or downloads.
The fountain asset is read unchanged, with its existing CC0 source provenance.
"""

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import wave

import numpy as np


SOURCE_PAGE = "https://freesound.org/people/Yoyodaman234/sounds/349072/"
PREPARED_SHA256 = "7aa54fd5e1a80243b2c03c3b56bcf8eca7ffe07681557ef1c88a86b49f535496"
RATE = 48000
START_SECONDS = 12.5
DURATION_SECONDS = 8
CROSSFADE_SECONDS = .25
DECLICK_SECONDS = .002
FILTERS = "highpass=f=300,lowpass=f=5000"
TARGET_RMS_DBFS = -32


def signal_metrics(samples):
    blocks = samples.reshape(-1, RATE // 40)
    rms = np.sqrt(np.mean(blocks ** 2, axis=1))
    levels = 20 * np.log10(np.maximum(rms, 1e-12))
    # Short overlapping spectra reveal rumbling bursts that whole-clip RMS hides.
    size = 4096
    windows = np.lib.stride_tricks.sliding_window_view(samples, size)[::size // 2]
    powers = abs(np.fft.rfft(windows * np.hanning(size), axis=1)) ** 2
    frequencies = np.fft.rfftfreq(size, 1 / RATE)
    low = powers[:, frequencies < 300].sum(axis=1) / powers.sum(axis=1)
    return dict(
        peak_dbfs=float(20 * np.log10(np.max(abs(samples)))),
        rms_dbfs=float(20 * np.log10(np.sqrt(np.mean(samples ** 2)))),
        low_300hz_power_percent=float(100 * powers[:, frequencies < 300].sum() / powers.sum()),
        maximum_window_low_300hz_percent=float(100 * np.max(low)),
        maximum_25ms_rise_db=float(np.max(np.diff(levels))),
        minimum_25ms_rms=float(np.min(rms)),
        onset_100ms_rms_dbfs=float(20 * np.log10(np.sqrt(np.mean(samples[:RATE // 10] ** 2)))),
        onset_500ms_rms_dbfs=float(20 * np.log10(np.sqrt(np.mean(samples[:RATE // 2] ** 2)))),
        seam_step=float(abs(samples[0] - samples[-1])),
        digital_clipped_samples=int(np.count_nonzero(abs(samples) >= 1)))


def main():
    root = Path(__file__).resolve().parents[1]
    assets = root / "src/ProjectTabletop.App/Assets/WaterGarden/Audio"
    cached_ffmpeg = (root / "artifacts/youtube-water-garden/audio-tools/"
                     "imageio_ffmpeg/binaries/ffmpeg-win-x86_64-v7.1.exe")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=assets / "fountain-loop.wav")
    parser.add_argument("--ffmpeg", default=shutil.which("ffmpeg") or str(cached_ffmpeg))
    args = parser.parse_args()
    if hashlib.sha256(args.source.read_bytes()).hexdigest() != PREPARED_SHA256:
        raise ValueError("The prepared pure-water source checksum changed")
    result = subprocess.run(
        [args.ffmpeg, "-v", "error", "-i", str(args.source), "-af", FILTERS,
         "-f", "f32le", "-ar", str(RATE), "-ac", "1", "pipe:1"],
        check=True, stdout=subprocess.PIPE)
    source = np.frombuffer(result.stdout, dtype="<f4").astype(np.float64)
    start = round(START_SECONDS * RATE)
    length = DURATION_SECONDS * RATE
    overlap = round(CROSSFADE_SECONDS * RATE)
    clip = source[start:start + length + overlap]
    if len(clip) != length + overlap:
        raise ValueError("The prepared water source is too short")
    loop = clip[:length].copy()
    angle = np.linspace(0, np.pi / 2, overlap)
    # At the repeat boundary the tail continues into the overlap, which then
    # returns smoothly to the opening. No padded silence or synthesized layer.
    loop[:overlap] = clip[length:] * np.cos(angle) + clip[:overlap] * np.sin(angle)
    edge = np.sin(np.linspace(0, np.pi / 2, round(DECLICK_SECONDS * RATE))) ** 2
    loop[:len(edge)] *= edge
    loop[-len(edge):] *= edge[::-1]
    gain = 10 ** (TARGET_RMS_DBFS / 20) / np.sqrt(np.mean(loop ** 2))
    if not np.isfinite(loop).all() or np.max(abs(loop)) * gain >= .5:
        raise ValueError("Unexpected water waveform or excessive peak")
    pcm = np.rint(loop * gain * 32768).astype("<i2")
    decoded = pcm.astype(np.float64) / 32768
    metrics = signal_metrics(decoded)
    if (metrics["digital_clipped_samples"] or metrics["seam_step"] != 0
            or metrics["minimum_25ms_rms"] < .001
            or metrics["maximum_25ms_rise_db"] > 10
            or metrics["low_300hz_power_percent"] > 1
            or metrics["maximum_window_low_300hz_percent"] > 5):
        raise ValueError("The water loop has a gap, abrupt burst, or low rumble")
    output = assets / "wand-swish-loop.wav"
    with wave.open(str(output), "wb") as writer:
        writer.setnchannels(1)
        writer.setsampwidth(2)
        writer.setframerate(RATE)
        writer.writeframes(pcm.tobytes())
    data = output.read_bytes()
    metrics.update(
        recording="Trickling Water 3", author="Yoyodaman234", source_page=SOURCE_PAGE,
        license="CC0 1.0 Universal", license_url="https://creativecommons.org/publicdomain/zero/1.0/",
        prepared_source="Assets/WaterGarden/Audio/fountain-loop.wav",
        prepared_source_sha256=PREPARED_SHA256,
        excerpt_seconds=[START_SECONDS, START_SECONDS + DURATION_SECONDS + CROSSFADE_SECONDS],
        filters=FILTERS, crossfade_seconds=CROSSFADE_SECONDS, crossfade="equal-power circular overlap",
        edge_declick_seconds=DECLICK_SECONDS, target_rms_dbfs=TARGET_RMS_DBFS,
        processing="No compression, limiting, synthetic layers, pitch changes or time stretching",
        duration_seconds=DURATION_SECONDS, sample_rate=RATE, channels=1, bits_per_sample=16,
        bytes=len(data), sha256=hashlib.sha256(data).hexdigest(),
        auditory_review="Signal checks only; display-speaker listening remains a live check")
    cache = root / "artifacts/water-wand-audio"
    cache.mkdir(parents=True, exist_ok=True)
    (cache / "prepared-loop.json").write_text(json.dumps(metrics, indent=2) + "\n",
                                             encoding="utf-8", newline="\n")
    print(json.dumps(metrics, indent=2))


if __name__ == "__main__":
    main()
