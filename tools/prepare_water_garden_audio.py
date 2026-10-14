"""Restore the verified pure-water fountain loop from its prepared local backup.

Usage: python tools/prepare_water_garden_audio.py [--source /path/to/prepared.wav]
Requires NumPy. The original preparation recipe is not retained; this restores
the exact previously checked loop without reprocessing it or downloading audio.
Playback volume is controlled by MainWindow.WaterGardenAudio.cs.
"""

import argparse
import hashlib
import io
import json
from pathlib import Path
import wave

import numpy as np


PREPARED_SHA256 = "7aa54fd5e1a80243b2c03c3b56bcf8eca7ffe07681557ef1c88a86b49f535496"


def main():
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path,
                        default=root / "artifacts/water-fountain-audio/water-only-preview.wav")
    args = parser.parse_args()
    data = args.source.read_bytes()
    if hashlib.sha256(data).hexdigest() != PREPARED_SHA256:
        raise ValueError("The prepared pure-water loop checksum changed")
    with wave.open(io.BytesIO(data), "rb") as reader:
        channels, rate = reader.getnchannels(), reader.getframerate()
        frames = reader.getnframes()
        if (channels, rate, reader.getsampwidth(), frames) != (2, 48000, 2, 1536000):
            raise ValueError("Unexpected prepared fountain format")
        samples = np.frombuffer(reader.readframes(frames), dtype="<i2")
    decoded = samples.reshape(-1, channels).astype(np.float64) / 32768
    seam = np.abs(decoded[0] - decoded[-1])
    typical_limit = np.percentile(np.abs(np.diff(decoded, axis=0)), 99.9, axis=0)
    per_second_rms = np.sqrt(np.mean(decoded.reshape(-1, rate, channels) ** 2, axis=1))
    if (not np.isfinite(decoded).all() or np.max(np.abs(decoded)) >= 1
            or np.any(seam > typical_limit) or np.min(per_second_rms) < .001):
        raise ValueError("The prepared fountain loop clips or has a gap or abnormal seam")

    output = root / "src/ProjectTabletop.App/Assets/WaterGarden/Audio/fountain-loop.wav"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(data)
    metrics = dict(duration_seconds=frames / rate, sample_rate=rate, channels=channels,
                   bits_per_sample=16, bytes=len(data), sha256=PREPARED_SHA256,
                   peak_dbfs=float(20 * np.log10(np.max(np.abs(decoded)))),
                   rms_dbfs=float(20 * np.log10(np.sqrt(np.mean(decoded ** 2)))),
                   seam_step=seam.tolist(), adjacent_step_p999=typical_limit.tolist(),
                   minimum_second_rms=float(np.min(per_second_rms)),
                   maximum_second_rms=float(np.max(per_second_rms)))
    cache = root / "artifacts/water-fountain-audio"
    cache.mkdir(parents=True, exist_ok=True)
    (cache / "prepared-loop.json").write_text(json.dumps(metrics, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(metrics, indent=2))


if __name__ == "__main__":
    main()
