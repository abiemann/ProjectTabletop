"""Prepare the openly licensed Football effects. Requires Python, NumPy and FFmpeg.

Usage: python tools/prepare_football_audio.py --ffmpeg /path/to/ffmpeg
Sources are cached under artifacts/football-audio; only the small finished MP3s
are shipped. See Assets/Football/Audio/AUDIO.md for credits and source licenses.
"""

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import urllib.request

import numpy as np


RATE = 48000
SOURCES = {
    "whistle": ("https://cdn.freesound.org/previews/538/538422_11966684-hq.mp3",
                "1bc56409d3439a16fd54bc8fa83affb7e2bc69519b15ac49979d924e895910bf"),
    "crowd": ("https://cdn.freesound.org/previews/678/678544_14787385-hq.mp3",
              "77765a7ec1439e63527e22d323e9699c97c8cd6ce010c310ef5843179f95f402"),
    "chant": ("https://cdn.freesound.org/previews/527/527740_6652872-hq.mp3",
              "702b5a70275146eb75db7fe1ba0b81f247015ef29e5ebebb31a254f2e8d4e2c1"),
    "stadium-soccer1": ("https://cdn.freesound.org/previews/237/237679_2378802-hq.mp3",
                        "628a884062e85f7315a87be91947dc4ba5d5632285c60a4dbc17f9b360d6d70b"),
    "stadium-soccer2": ("https://cdn.freesound.org/previews/237/237678_2378802-hq.mp3",
                        "1851e61523968ae47bf7b46af1f5130b2c13e13592b6bd01f628f0e103db5d37"),
    "stadium-victory-reduced": ("https://cdn.freesound.org/previews/397/397435_4019029-hq.mp3",
                                "efe57f1e7dbed21e1561b8090cf54301a78775b4b8bf8ff418507c7e364f7f56"),
}


def fade(samples, attack=0.015, release=0.12):
    result = samples.copy()
    start = min(len(result), round(attack * RATE))
    end = min(len(result), round(release * RATE))
    if start:
        result[:start] *= np.linspace(0, 1, start)[:, None]
    if end:
        result[-end:] *= np.linspace(1, 0, end)[:, None]
    return result


def peak(samples, db):
    return samples * (10 ** (db / 20) / max(0.0001, np.max(np.abs(samples))))


def add(target, source, seconds, gain=1):
    start = round(seconds * RATE)
    count = min(len(source), len(target) - start)
    target[start:start + count] += source[:count] * gain


def seconds(value):
    return np.zeros((round(value * RATE), 2), dtype=np.float32)


def loudness(ffmpeg, samples, target):
    # Offline only: align perceived levels while retaining the source crowd's
    # attack/decay, with a true-peak ceiling to leave MP3 encoding headroom.
    command = [ffmpeg, "-v", "info", "-f", "f32le", "-ar", str(RATE),
               "-ac", "2", "-i", "pipe:0"]
    raw = samples.astype("<f4").tobytes()
    analysis = subprocess.run(command + ["-af", f"loudnorm=I={target}:TP=-2:LRA=11:print_format=json",
                              "-f", "null", "-"], input=raw, capture_output=True, check=True)
    log = analysis.stderr.decode("utf-8")
    stats = json.loads(log[log.rfind("{"):log.rfind("}") + 1])
    parameters = (f"loudnorm=I={target}:TP=-2:LRA=11:linear=true:"
                  f"measured_I={stats['input_i']}:measured_TP={stats['input_tp']}:"
                  f"measured_LRA={stats['input_lra']}:measured_thresh={stats['input_thresh']}:"
                  f"offset={stats['target_offset']}")
    rendered = subprocess.run(command + ["-af", parameters, "-f", "f32le", "-ar", str(RATE),
                              "-ac", "2", "pipe:1"], input=raw, capture_output=True, check=True)
    return np.frombuffer(rendered.stdout, dtype="<f4").reshape(-1, 2).copy()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--only", nargs="+", help="Render only these effect names; leave all others unchanged")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    cache = root / "artifacts/football-audio"
    output = root / "src/ProjectTabletop.App/Assets/Football/Audio"
    cache.mkdir(parents=True, exist_ok=True)
    output.mkdir(parents=True, exist_ok=True)
    sources = {}
    for name, (url, expected_hash) in SOURCES.items():
        path = cache / f"{name}-source.mp3"
        if not path.exists():
            urllib.request.urlretrieve(url, path)
        actual_hash = hashlib.sha256(path.read_bytes()).hexdigest()
        if expected_hash and actual_hash != expected_hash:
            raise ValueError(f"Source checksum changed: {name}")
        raw = subprocess.check_output([
            args.ffmpeg, "-v", "error", "-i", str(path), "-f", "f32le",
            "-acodec", "pcm_f32le", "-ar", str(RATE), "-ac", "2", "pipe:1"])
        sources[name] = np.frombuffer(raw, dtype="<f4").reshape(-1, 2).copy()

    whistle = peak(fade(sources["whistle"], .008, .045), -6)
    final_whistle = seconds(1.35)
    short = fade(whistle[:round(.20 * RATE)], .008, .035)
    add(final_whistle, short, 0)
    add(final_whistle, short, .37, .94)
    add(final_whistle, whistle, .80)

    crowd = sources["crowd"]
    cheer = peak(fade(crowd[:round(5.8 * RATE)], .045, 1.1), -3)
    voice = peak(fade(sources["chant"], .008, .06), -8)
    chants = []
    for variant, beats in enumerate(((.35, 1.7, 3.05, 4.4), (.35, 1.05, 2.45, 3.15, 4.55))):
        chant = seconds(6.3)
        bed_start = round((7 + variant * 6.5) * RATE)
        bed = peak(fade(crowd[bed_start:bed_start + len(chant)], .25, 1.3), -20)
        add(chant, bed, 0)
        for number, beat in enumerate(beats):
            # Recorded voices remain human; slight stereo differences and a
            # quiet delayed tail place them among the recorded spectators.
            group = voice[:, ::-1] if number % 2 else voice
            add(chant, group, beat, 1 if number % 2 == 0 else .9)
            add(chant, group[:, ::-1], beat + .085, .24)
            add(chant, group, beat + .17, .10)
        chants.append(peak(fade(chant, .015, .5), -4))

    # Different real goal reactions: a broad stadium eruption, then a sharper
    # initial roar. Both target the unchanged original cheer's -14.6 LUFS.
    cheer2 = loudness(args.ffmpeg,
                     fade(sources["stadium-soccer2"][round(3.7 * RATE):round(9.5 * RATE)], .025, .9), -14.6)
    cheer3 = loudness(args.ffmpeg,
                     fade(sources["stadium-soccer1"][round(.85 * RATE):round(6.65 * RATE)], .025, .9), -14.6)
    # A longer, slightly fuller sports-win ovation replaces the musical sting.
    victory = loudness(args.ffmpeg,
                       fade(sources["stadium-victory-reduced"][round(1 * RATE):round(8.6 * RATE)], .025, 1.5), -13.5)

    effects = {"start-whistle": whistle, "end-whistle": final_whistle,
               "goal-cheer": cheer, "goal-cheer-2": cheer2, "goal-cheer-3": cheer3,
               "crowd-chant-1": chants[0],
               "crowd-chant-2": chants[1], "victory": victory}
    report = []
    if args.only and set(args.only) - effects.keys():
        parser.error(f"Unknown effects: {sorted(set(args.only) - effects.keys())}")
    for name, samples in effects.items():
        if args.only and name not in args.only:
            continue
        path = output / f"{name}.mp3"
        subprocess.run([
            args.ffmpeg, "-y", "-v", "error", "-f", "f32le", "-ar", str(RATE),
            "-ac", "2", "-i", "pipe:0", "-c:a", "libmp3lame", "-b:a", "192k",
            "-map_metadata", "-1", str(path)], input=samples.astype("<f4").tobytes(), check=True)
        report.append({"file": path.name, "seconds": round(len(samples) / RATE, 4),
                       "bytes": path.stat().st_size,
                       "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
