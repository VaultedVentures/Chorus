"""Measure banded end-costs: cross-voice true positives vs near-misses."""
from __future__ import annotations

import subprocess
import sys
import tempfile
import wave
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).parent))
from wakeword_lib import (
    FS, HOP, WIN_LEN, OnlineDtw, mfcc_frame, mfcc_stream, load_template,
    sensitivity_to_threshold, trim_silence,
)

RES = Path(__file__).parent.parent / "client/src/Chorus.Core/WakeWord/Resources"
REPO = Path(__file__).parent.parent

VOICES = {
    "lessac": (REPO / "voices" / "en_US-lessac-medium.onnx",
               REPO / "voices" / "en_US-lessac-medium.onnx.json"),
    "amy": (Path("/tmp/wwtest/en_US-amy-medium.onnx"),
            Path("/tmp/wwtest/en_US-amy-medium.onnx.json")),
    "ryan": (Path("/tmp/wwtest/en_US-ryan-high.onnx"),
             Path("/tmp/wwtest/en_US-ryan-high.onnx.json")),
}


def synth(text: str, voice: str, scale: float) -> np.ndarray:
    model, cfg = VOICES[voice]
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


names = sorted(p.stem for p in RES.glob("hey-chorus-*.mfc"))
tpls = [load_template(RES / f"{n}.mfc") for n in names]
print(f"{len(tpls)} templates: {names}")


def min_end_cost(pcm: np.ndarray) -> tuple[float, int]:
    matchers = [OnlineDtw(t) for t in tpls]
    buf = trim_silence(pcm).astype(np.int16)
    best, best_hop = float("inf"), -1
    hop = 0
    while len(buf) >= WIN_LEN:
        frame = buf[:WIN_LEN]
        buf = buf[HOP:]
        x = mfcc_frame(frame)
        for m in matchers:
            c = m.step(x)
            if c < best:
                best, best_hop = c, hop
        hop += 1
    return best, best_hop


print("=== TRUE POSITIVES (cross-voice / cross-rate / punctuation) ===")
cases = [
    ("hey chorus (amy 1.05)", "hey chorus", "amy", 1.05),
    ("hey chorus (ryan 0.9)", "hey chorus", "ryan", 0.90),
    ("hey chorus (lessac 0.95)", "hey chorus", "lessac", 0.95),
    ("Hey Chorus! (lessac 1.0)", "Hey Chorus!", "lessac", 1.0),
    ("hey chorus. (amy 1.0)", "hey chorus.", "amy", 1.0),
    ("hey chorus? (ryan 1.0)", "hey chorus?", "ryan", 1.0),
    ("HEY CHORUS (lessac 1.1)", "HEY CHORUS", "lessac", 1.1),
    ("hey chorus (amy 0.85)", "hey chorus", "amy", 0.85),
]
for label, text, voice, scale in cases:
    c, h = min_end_cost(synth(text, voice, scale))
    print(f"  {label:32s} minEnd={c:.3f} @hop {h}")

print()
print("=== NEAR-MISSES / NON-TARGETS (lessac) ===")
for text in ["hey gorgeous", "hey charlie", "hey siri", "okay google",
             "hello there", "good morning chorus", "the chorus is loud",
             "a chorus of birds", "hey coursing", "hay chorus", "hey corps",
             "what's the weather like", "hey cordless"]:
    c, h = min_end_cost(synth(text, "lessac", 1.0))
    print(f"  {text:32s} minEnd={c:.3f} @hop {h}")

print()
print("thresholds: sens=0.3 ->", round(sensitivity_to_threshold(0.3), 3),
      "| 0.5 ->", round(sensitivity_to_threshold(0.5), 3),
      "| 0.7 ->", round(sensitivity_to_threshold(0.7), 3),
      "| 1.0 ->", round(sensitivity_to_threshold(1.0), 3))
