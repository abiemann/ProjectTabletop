"""Prepare the user-selected Swale recording as a continuous fountain loop.

Usage: python tools/prepare_water_garden_audio.py --ffmpeg /path/to/ffmpeg
Requires NumPy and FFmpeg. The original OGG stays in the local artifacts folder;
only the prepared PCM WAV is shipped. No audio processing runs during gameplay.
"""

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import wave

import numpy as np


SOURCE_SHA256 = "b71def4798508e588229a5f801fefaed9ac51c5dbad224274123c92e6d183fff"
RATE = 48000
LOOP_SECONDS = 30
OVERLAP_SECONDS = 1


def main():
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--source", type=Path,
                        default=root / "artifacts/youtube-water-garden/audio/Swale-Siddharth-Patil.ogg")
    args = parser.parse_args()
    cache = root / "artifacts/water-fountain-audio"
    output = root / "src/ProjectTabletop.App/Assets/WaterGarden/Audio"
    cache.mkdir(parents=True, exist_ok=True)
    output.mkdir(parents=True, exist_ok=True)
    if hashlib.sha256(args.source.read_bytes()).hexdigest() != SOURCE_SHA256:
        raise ValueError("The selected Swale recording checksum changed")

    raw = subprocess.check_output([
        args.ffmpeg, "-v", "error", "-i", str(args.source), "-f", "f32le",
        "-ar", str(RATE), "-ac", "1", "pipe:1"])
    samples = np.frombuffer(raw, dtype="<f4")
    length = LOOP_SECONDS * RATE
    overlap = OVERLAP_SECONDS * RATE
    section = samples[:length + overlap]
    if len(section) != length + overlap:
        raise ValueError("The source does not contain the expected 31-second excerpt")
    # Preserve the recording's mono image, dynamics and tone. Only blend the
    # end into the beginning; no other recordings, filters or gain are added.
    loop = section[:length].copy()
    phase = np.linspace(0, np.pi / 2, overlap)
    loop[:overlap] = section[length:] * np.cos(phase) + section[:overlap] * np.sin(phase)
    if not np.isfinite(loop).all() or np.max(np.abs(loop)) >= 1:
        raise ValueError("The fountain loop is invalid or clips")
    pcm = np.round(loop * 32767).astype("<i2")

    # Validate the actual shipped PCM across both joins and every second.
    decoded = pcm.astype(np.float32) / 32768
    seam = float(abs(decoded[0] - decoded[-1]))
    overlap_join = float(abs(decoded[overlap] - decoded[overlap - 1]))
    typical_limit = float(np.percentile(np.abs(np.diff(decoded)), 99.9))
    per_second_rms = np.sqrt(np.mean(decoded.reshape(-1, RATE) ** 2, axis=1))
    if max(seam, overlap_join) > typical_limit or np.min(per_second_rms) < .001:
        raise ValueError("The prepared fountain loop has a gap or abnormal seam")
    destination = output / "fountain-loop.wav"
    with wave.open(str(destination), "wb") as writer:
        writer.setnchannels(1)
        writer.setsampwidth(2)
        writer.setframerate(RATE)
        writer.writeframes(pcm.tobytes())

    metrics = dict(source_sha256=SOURCE_SHA256, duration_seconds=len(pcm) / RATE,
                   sample_rate=RATE, channels=1, bits_per_sample=16,
                   overlap_seconds=OVERLAP_SECONDS,
                   peak_dbfs=float(20 * np.log10(np.max(np.abs(decoded)))),
                   rms_dbfs=float(20 * np.log10(np.sqrt(np.mean(decoded ** 2)))),
                   seam_step=seam, overlap_join_step=overlap_join,
                   adjacent_step_p999=typical_limit,
                   minimum_second_rms=float(np.min(per_second_rms)),
                   maximum_second_rms=float(np.max(per_second_rms)),
                   bytes=destination.stat().st_size,
                   sha256=hashlib.sha256(destination.read_bytes()).hexdigest())
    (cache / "prepared-loop.json").write_text(json.dumps(metrics, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(metrics, indent=2))


if __name__ == "__main__":
    main()
