"""Prepare the bundled 8K NASA Black Marble texture from the verified 3 km map."""

import argparse
import hashlib
from pathlib import Path

from PIL import Image


SOURCE_SHA256 = "230aac448ae68c358be433dd518888cccb3a85ccf66f7b44326441c324ad6725"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    if hashlib.sha256(args.source.read_bytes()).hexdigest() != SOURCE_SHA256:
        raise SystemExit("The source is not the verified NASA Black Marble 2016 map.")
    # The verified NASA map is just above Pillow's default warning threshold.
    Image.MAX_IMAGE_PIXELS = 100_000_000
    with Image.open(args.source) as source:
        if source.size != (13500, 6750) or source.mode != "RGB":
            raise SystemExit("Expected the 13500 x 6750 RGB global colour map.")
        source.resize((8192, 4096), Image.Resampling.LANCZOS).save(args.output)
    with Image.open(args.output) as result:
        assert result.size == (8192, 4096) and result.mode == "RGB"
    print(f"{args.output}: {args.output.stat().st_size} bytes")
    print(f"SHA-256: {hashlib.sha256(args.output.read_bytes()).hexdigest()}")


if __name__ == "__main__":
    main()
