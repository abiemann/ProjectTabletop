"""Prepare the Globe sky from NASA SVS Deep Star Maps 2020 (galactic, 16K).

Requires numpy, OpenEXR and Pillow; these are asset-preparation dependencies only.
Download the source separately from https://svs.gsfc.nasa.gov/4851/.
The original HDR catalogue map is reprojected, or preserved at full resolution
with --panorama as four lossless longitude tiles. Neither mode applies
generative edits.
"""

import argparse
import hashlib
import json
import math
from pathlib import Path

import numpy as np
import OpenEXR
from PIL import Image


SOURCE_SHA256 = "20363595f309da9626111937d6d75555530937914527abbd6d81132764434aa7"
SOURCE_URL = "https://svs.gsfc.nasa.gov/vis/a000000/a004800/a004851/starmap_2020_16k_gal.exr"


def encode_srgb(linear: np.ndarray, exposure: float) -> np.ndarray:
    linear = np.clip(linear * exposure, 0, 1)
    srgb = np.where(linear <= .0031308, 12.92 * linear,
                    1.055 * np.power(linear, 1 / 2.4) - .055)
    return np.rint(srgb * 255).astype(np.uint8)


def save_image(image: np.ndarray, output: Path, panorama: bool) -> dict:
    output.parent.mkdir(parents=True, exist_ok=True)
    if not panorama:
        Image.fromarray(image).save(output, optimize=True)
        return {"sha256": hashlib.file_digest(output.open("rb"), "sha256").hexdigest()}

    tiles = []
    for index in range(4):
        left, width = index * 4096, 4096
        path = output.with_name(f"{output.stem}-{index}{output.suffix}")
        Image.fromarray(image[:, left:left + width]).save(path, optimize=True)
        size = path.stat().st_size
        if size >= 100 * 1024 * 1024:
            raise ValueError(f"Panorama tile exceeds the 100 MiB asset limit: {path}")
        tiles.append({"file": path.name, "bounds": [left, 0, width, 8192], "bytes": size,
                      "sha256": hashlib.file_digest(path.open("rb"), "sha256").hexdigest()})
    return {"tiles": tiles}


def prepare(source: Path, output: Path, exposure: float, panorama: bool = False) -> None:
    source_hash = hashlib.file_digest(source.open("rb"), "sha256").hexdigest()
    if source_hash != SOURCE_SHA256:
        raise ValueError("Source does not match the documented NASA 16K galactic map")
    with OpenEXR.File(str(source)) as exr:
        sky = exr.channels()["RGB"].pixels
        if sky.shape != (8192, 16384, 3):
            raise ValueError(f"Unexpected NASA source dimensions: {sky.shape}")

        if panorama:
            # Preserve every source texel and the whole celestial sphere. The
            # runtime renderer supplies its view and rotation; no resize or
            # projection is baked into this asset.
            image = np.empty(sky.shape, dtype=np.uint8)
            for start in range(0, sky.shape[0], 64):
                stop = min(start + 64, sky.shape[0])
                image[start:stop] = encode_srgb(sky[start:stop].astype(np.float32), exposure)
            projection = {"projection": "plate-carree", "coordinateSystem": "galactic",
                          "horizontalAndVerticalFieldDegrees": [360, 180], "samplesPerPixel": 1}
        else:
            image = stereographic(sky, exposure)
            projection = {"projection": "stereographic", "centreGalacticLongitudeLatitudeDegrees": [0, 0],
                          "horizontalAndVerticalFieldDegrees": 160, "rollDegrees": 35, "samplesPerPixel": 4}
        outputs = save_image(image, output, panorama)

    metadata = {
        "source": SOURCE_URL, "sourceSha256": source_hash,
        "sourceDimensions": [16384, 8192], "outputDimensions": [image.shape[1], image.shape[0]],
        **projection,
        "linearExposureMultiplier": exposure, "encoding": "sRGB",
        **outputs,
        "credit": "NASA/Goddard Space Flight Center Scientific Visualization Studio; ESA/Gaia/DPAC",
    }
    output.with_suffix(".json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps(metadata, indent=2))


def stereographic(sky: np.ndarray, exposure: float) -> np.ndarray:
    # Stereographic mapping is locally conformal: small star images stay
    # round, including near the edges. The galactic plane crosses the
    # centre diagonally. No stars, clouds, blur or glow are added.
    size, samples = 4096, 2
    field_degrees, roll_degrees = 160, 35
    limit = 2 * math.tan(math.radians(field_degrees) / 4)
    roll = math.radians(roll_degrees)
    x = ((np.arange(size * samples, dtype=np.float32) + .5)
         / (size * samples) * 2 - 1)[None, :] * limit
    image = np.empty((size, size, 3), dtype=np.uint8)
    for start in range(0, size, 64):
        stop = min(start + 64, size)
        y = (1 - (np.arange(start * samples, stop * samples, dtype=np.float32)
                  + .5) / (size * samples) * 2)[:, None] * limit
        radius_squared = x * x + y * y
        inverse = 1 / (4 + radius_squared)
        right, up = 4 * x * inverse, 4 * y * inverse
        forward = (4 - radius_squared) * inverse
        horizontal = right * math.cos(roll) - up * math.sin(roll)
        vertical = right * math.sin(roll) + up * math.cos(roll)
        longitude = np.arctan2(horizontal, forward)
        latitude = np.arcsin(np.clip(vertical, -1, 1))
        # NASA's galactic map has longitude zero at the centre and
        # increasing longitude to the left; source coordinates are centres.
        sx = ((.5 - longitude / (2 * math.pi)) * sky.shape[1] - .5) % sky.shape[1]
        sy = np.clip((.5 - latitude / math.pi) * sky.shape[0] - .5, 0, sky.shape[0] - 1)
        x0, y0 = sx.astype(np.int32), sy.astype(np.int32)
        x1, y1 = (x0 + 1) % sky.shape[1], np.minimum(y0 + 1, sky.shape[0] - 1)
        fx, fy = (sx - x0)[..., None], (sy - y0)[..., None]
        linear = ((sky[y0, x0].astype(np.float32) * (1 - fx)
                   + sky[y0, x1].astype(np.float32) * fx) * (1 - fy)
                  + (sky[y1, x0].astype(np.float32) * (1 - fx)
                     + sky[y1, x1].astype(np.float32) * fx) * fy)
        # Integrate four subpixels in linear light before display encoding.
        linear = linear.reshape(stop - start, samples, size, samples, 3).mean(axis=(1, 3))
        image[start:stop] = encode_srgb(linear, exposure)
    return image


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--exposure", type=float, default=4)
    parser.add_argument("--panorama", action="store_true",
                        help="Preserve the full 16384 by 8192 celestial sphere as four 4096 by 8192 PNG tiles")
    args = parser.parse_args()
    if not math.isfinite(args.exposure) or args.exposure <= 0:
        parser.error("Exposure must be finite and positive")
    prepare(args.source, args.output, args.exposure, args.panorama)
