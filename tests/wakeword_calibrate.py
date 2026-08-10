"""Calibrate the reference spotter: score curves + non-target rejection."""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).parent))
from wakeword_lib import (
    FS, HOP, WIN_LEN, ENERGY_FLOOR, OnlineDtw, dtw_distance,
    hop_rms, mfcc_stream, mfcc_frame, load_template,
)

RES = Path(__file__).parent.parent / "client/src/Chorus.Core/WakeWord/Resources"
REPO = Path(__file__).parent.parent
names = ["lessac-085", "lessac-10", "lessac-115", "amy-09", "amy-10", "amy-11",
         "ryan-095", "ryan-10", "ryan-105"]
tpls = [load_template(RES / f"hey-chorus-{n}.mfc") for n in names]

canon = np.fromfile(RES / "canonical_hey_chorus.pcm", dtype=np.int16)


def online_curve(pcm: np.ndarray):
    """Return per-hop min end-cost across templates + rms."""
    matchers = [OnlineDtw(t) for t in tpls]
    costs, rms = [], []
    buf = pcm.astype(np.int16)
    while len(buf) >= WIN_LEN:
        frame = buf[:WIN_LEN]
        buf = buf[HOP:]
        x = mfcc_frame(frame)
        cs = [m.step(x) for m in matchers]
        costs.append(min(cs))
        rms.append(float(np.sqrt(np.mean(frame.astype(np.float64) ** 2))))
    return costs, rms


costs, rms = online_curve(canon)
print(f"canonical: {len(costs)} hops, phrase end hop ~{len(costs)}")
for i, (c, r) in enumerate(zip(costs, rms)):
    if i % 8 == 0 or i == len(costs) - 1:
        print(f"  hop {i:3d} t={i*10:4d}ms rms={r:7.1f} minEnd={c:.3f}")
low = min(costs)
print(f"min end-cost during canonical: {low:.3f} at hop {int(np.argmin(costs))} "
      f"({int(np.argmin(costs))*10}ms) rms={rms[int(np.argmin(costs))]:.0f}")
print()

# --- non-target probes: raw sine/noise and piper phrases ---
import subprocess, tempfile, wave

def synth_phrase(text: str, voice: str = "lessac", scale: float = 1.0):
    model = REPO / "voices" / "en_US-lessac-medium.onnx"
    cfg = REPO / "voices" / "en_US-lessac-medium.onnx.json"
    with tempfile.TemporaryDirectory() as td:
        inp = Path(td) / "in.txt"; out = Path(td) / "out.wav"
        inp.write_text(text + "\n")
        subprocess.run(["/opt/chorus/venv/bin/piper", "-m", str(model), "-c", str(cfg),
                        "-i", str(inp), "-f", str(out), "--length-scale", str(scale)],
                       check=True, capture_output=True)
        with wave.open(str(out), "rb") as w:
            rate = w.getframerate(); raw = w.readframes(w.getnframes())
        pcm = np.frombuffer(raw, dtype=np.int16)
        n_out = int(round(len(pcm) * FS / rate))
        xo = np.linspace(0, 1, len(pcm), endpoint=False)
        xn = np.linspace(0, 1, n_out, endpoint=False)
        return np.interp(xn, xo, pcm.astype(np.float64)).astype(np.int16)

from wakeword_lib import trim_silence

probes = {
    "hey charlie": "hey charlie",
    "hey gorgeous": "hey gorgeous",
    "hey siri": "hey siri",
    "okay google": "okay google",
    "hello there": "hello there",
    "good morning chorus": "good morning chorus",
    "the chorus is loud": "the chorus is loud",
    "what's the weather like": "what's the weather like",
}
print("=== non-target probes (lessac voice) ===")
for label, text in probes.items():
    pcm = trim_silence(synth_phrase(text))
    c, r = online_curve(pcm)
    print(f"  {label:22s} minEnd={min(c):.3f}  (hops={len(c)})")

# noise probes
rng = np.random.default_rng(7)
noise = (rng.standard_normal(FS * 2) * 400).astype(np.int16)
c, r = online_curve(noise)
print(f"  {'white noise':22s} minEnd={min(c):.3f}")
t = np.arange(FS * 2) / FS
chirp = (8000 * np.sin(2 * np.pi * (300 * t + 150 * t**2))).astype(np.int16)
c, r = online_curve(chirp)
print(f"  {'sine chirp':22s} minEnd={min(c):.3f}")
silence = np.zeros(FS, dtype=np.int16)
c, r = online_curve(silence)
print(f"  {'silence':22s} minEnd={min(c):.3f}")
