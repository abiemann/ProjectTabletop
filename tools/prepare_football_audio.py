"""Prepare the openly licensed Football effects. Requires Python, NumPy and FFmpeg.

Usage: python tools/prepare_football_audio.py --ffmpeg /path/to/ffmpeg
Sources are cached under artifacts/football-audio; finished MP3 effects and a
gapless WAV ambience loop are shipped. See Assets/Football/Audio/AUDIO.md for
credits and source licenses.
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
    "whistle-pablo": ("https://cdn.freesound.org/previews/90/90743_1476665-hq.mp3",
                      "68b815d65b169d0d634d9bd31fd0b6fb5be6b5d66e1cf7046b23d574c3119d86"),
    "chant-wimbledon": ("https://cdn.freesound.org/previews/654/654084_12932169-hq.mp3",
                        "5aae7b9a5261d54dee3e2b473ca0d17e4c135127e09fa0cf1b1ce42318517942"),
    "chant-aris": ("https://cdn.freesound.org/previews/263/263679_1467893-hq.mp3",
                  "9b890df3a85e141c726a0289964d945f81bec633489b4362c32ffa9818d51b1d"),
    "stadium-soccer2": ("https://cdn.freesound.org/previews/237/237678_2378802-hq.mp3",
                        "1851e61523968ae47bf7b46af1f5130b2c13e13592b6bd01f628f0e103db5d37"),
    "stadium-millerntor": ("https://cdn.freesound.org/previews/829/829455_3625328-hq.mp3",
                           "0f5b0bf37d3c41b52fbfbba9400f3db904f94af50d68e385b2537db781fccdb2"),
    "stadium-victory-reduced": ("https://cdn.freesound.org/previews/397/397435_4019029-hq.mp3",
                                "efe57f1e7dbed21e1561b8090cf54301a78775b4b8bf8ff418507c7e364f7f56"),
    "stadium-ambience": ("https://cdn.freesound.org/previews/829/829454_3625328-hq.mp3",
                         "00728388fe04840db4f8fd98e76d9140336bba313b27b82314d7a4951d27829c"),
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


def loudness(ffmpeg, samples, target, constant_gain=False):
    # Offline only: align perceived levels while retaining the source crowd's
    # attack/decay, with a true-peak ceiling to leave MP3 encoding headroom.
    command = [ffmpeg, "-v", "info", "-f", "f32le", "-ar", str(RATE),
               "-ac", "2", "-i", "pipe:0"]
    raw = samples.astype("<f4").tobytes()
    analysis = subprocess.run(command + ["-af", f"loudnorm=I={target}:TP=-2:LRA=11:print_format=json",
                              "-f", "null", "-"], input=raw, capture_output=True, check=True)
    log = analysis.stderr.decode("utf-8")
    stats = json.loads(log[log.rfind("{"):log.rfind("}") + 1])
    if constant_gain:
        # A single gain preserves the cyclic waveform and natural dynamics;
        # there is no time-varying processing or filter state at the loop seam.
        gain_db = min(target - float(stats["input_i"]), -2 - float(stats["input_tp"]))
        return samples * 10 ** (gain_db / 20)
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

    # Preserve the real whistle's sharp attack and sustained tone. A longer
    # blast gives more authority without substantially raising perceived level.
    whistle_master = peak(sources["whistle-pablo"][round(.06 * RATE):round(1.02 * RATE)], -4.5)
    whistle = fade(whistle_master[:round(.82 * RATE)], .004, .045)
    final_whistle = seconds(1.72)
    short = fade(whistle_master[:round(.24 * RATE)], .004, .025)
    add(final_whistle, short, 0)
    add(final_whistle, short, .38, .96)
    add(final_whistle, fade(whistle_master, .004, .055), .76)

    # Genuine massed football crowds retain the stadium's space and energy.
    # Match both passages to the goal cheers without synthetic voice layering.
    chants = [loudness(args.ffmpeg, fade(sources[name][start * RATE:end * RATE], .2, 1.2), -14.6)
              for name, start, end in (("chant-wimbledon", 0, 8), ("chant-aris", 20, 28))]

    # Each side has its own real stadium roar. Keep the approved Southampton
    # attack and gain, but fade it out sooner to avoid the following announcer.
    red_goal = loudness(args.ffmpeg,
                     fade(sources["stadium-soccer2"][round(3.7 * RATE):round(9.5 * RATE)], .025, .9), -14.6)
    red_goal = fade(red_goal[:4 * RATE], attack=0, release=1.1)
    blue_goal = loudness(args.ffmpeg,
                        fade(sources["stadium-millerntor"][round(1.8 * RATE):round(7.6 * RATE)], .025, .9), -14.6)
    # A longer, slightly fuller sports-win ovation replaces the musical sting.
    victory = loudness(args.ffmpeg,
                       fade(sources["stadium-victory-reduced"][round(1 * RATE):round(8.6 * RATE)], .025, 1.5), -13.5)

    # Thirty seconds of steady stadium atmosphere, with a three-second
    # equal-power overlap. Put the blend at the beginning so the file's last
    # sample and first sample remain adjacent samples of the original take.
    ambience_source = sources["stadium-ambience"][286 * RATE:319 * RATE]
    ambience = ambience_source[:30 * RATE].copy()
    overlap = 3 * RATE
    phase = np.linspace(0, np.pi / 2, overlap)[:, None]
    ambience[:overlap] = (ambience_source[30 * RATE:] * np.cos(phase)
                         + ambience_source[:overlap] * np.sin(phase))
    ambience = loudness(args.ffmpeg, ambience, -23, constant_gain=True)

    effects = {"start-whistle": whistle, "end-whistle": final_whistle,
               "goal-red": red_goal, "goal-blue": blue_goal,
               "crowd-chant-1": chants[0],
               "crowd-chant-2": chants[1], "victory": victory,
               "stadium-ambience": ambience}
    report = []
    if args.only and set(args.only) - effects.keys():
        parser.error(f"Unknown effects: {sorted(set(args.only) - effects.keys())}")
    for name, samples in effects.items():
        if args.only and name not in args.only:
            continue
        is_loop = name == "stadium-ambience"
        path = output / f"{name}.{'wav' if is_loop else 'mp3'}"
        encoding = ["-c:a", "pcm_s16le"] if is_loop else ["-c:a", "libmp3lame", "-b:a", "192k"]
        subprocess.run([
            args.ffmpeg, "-y", "-v", "error", "-f", "f32le", "-ar", str(RATE),
            "-ac", "2", "-i", "pipe:0", *encoding,
            "-map_metadata", "-1", str(path)], input=samples.astype("<f4").tobytes(), check=True)
        report.append({"file": path.name, "seconds": round(len(samples) / RATE, 4),
                       "bytes": path.stat().st_size,
                       "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
