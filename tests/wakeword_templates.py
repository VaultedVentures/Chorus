"""Generate the packaged CHORUS wake-word templates ("hey chorus").

Build-time tool (run on the build host, not on Windows):
  1. Synthesizes "hey chorus" with Piper across several voices + speaking
     rates (cross-speaker robustness for the template matcher).
  2. Resamples to the mic pipeline rate (16 kHz), trims silence, computes
     MFCC streams with the reference DSP (wakeword_lib.py).
  3. Writes compact .mfc templates + the canonical PCM + the Python MFCC
     fixture into the C# projects, where they are embedded resources.

Run:  ./venv/bin/python -m tests.wakeword_templates
"""

from __future__ import annotations

import json
import os
import subprocess
import tempfile
import wave
from pathlib import Path

import numpy as np

try:  # `python -m tests.wakeword_templates` (tests/ is a package)
    from .wakeword_lib import (
        FS, HOP, WIN_LEN, mfcc_stream, resample_to_16k, save_template, trim_silence,
    )
except ImportError:  # `python tests/wakeword_templates.py` (tests/ on path)
    from wakeword_lib import (
        FS, HOP, WIN_LEN, mfcc_stream, resample_to_16k, save_template, trim_silence,
    )

REPO = Path(__file__).resolve().parent.parent
VENV_PIPER = REPO / "venv" / "bin" / "piper"

RESOURCES = REPO / "client" / "src" / "Chorus.Core" / "WakeWord" / "Resources"
FIXTURES = REPO / "client" / "tests" / "Chorus.Core.Tests" / "Fixtures"

PHRASE = "hey chorus"

VOICES = [
    # (label, model_path, config_path, length_scales)
    ("lessac", REPO / "voices" / "en_US-lessac-medium.onnx",
     REPO / "voices" / "en_US-lessac-medium.onnx.json",
     [0.80, 0.85, 0.90, 0.95, 1.0, 1.05, 1.10, 1.15, 1.20]),
    ("amy", Path("/tmp/wwtest/en_US-amy-medium.onnx"),
     Path("/tmp/wwtest/en_US-amy-medium.onnx.json"),
     [0.85, 0.90, 0.95, 1.0, 1.05, 1.10, 1.15]),
    ("ryan", Path("/tmp/wwtest/en_US-ryan-high.onnx"),
     Path("/tmp/wwtest/en_US-ryan-high.onnx.json"),
     [0.90, 0.95, 1.0, 1.05, 1.10]),
]


def synth(voice: tuple, length_scale: float) -> np.ndarray:
    """Synthesize PHRASE -> 16k int16 mono PCM."""
    _, model, config, _ = voice
    with tempfile.TemporaryDirectory() as td:
        out = Path(td) / "out.wav"
        inp = Path(td) / "in.txt"
        inp.write_text(PHRASE + "\n")
        subprocess.run(
            [str(VENV_PIPER), "-m", str(model), "-c", str(config),
             "-i", str(inp), "-f", str(out), "--length-scale", str(length_scale)],
            check=True, capture_output=True,
        )
        with wave.open(str(out), "rb") as w:
            rate = w.getframerate()
            ch = w.getnchannels()
            raw = w.readframes(w.getnframes())
        pcm = np.frombuffer(raw, dtype=np.int16)
        if ch > 1:  # keep first channel if piper ever emits stereo
            pcm = pcm[::ch]
        return resample_to_16k(pcm, rate)


def main() -> None:
    RESOURCES.mkdir(parents=True, exist_ok=True)
    FIXTURES.mkdir(parents=True, exist_ok=True)

    templates: list[tuple[str, np.ndarray]] = []
    canonical: np.ndarray | None = None

    for voice in VOICES:
        label, _, _, scales = voice
        for scale in scales:
            pcm = synth(voice, scale)
            trimmed = trim_silence(pcm)
            mfcc = mfcc_stream(trimmed)
            if len(mfcc) < 20:
                print(f"  !! {label}@{scale}: only {len(mfcc)} frames — skipping")
                continue
            name = f"hey-chorus-{label}-{str(scale).replace('.', '')}"
            templates.append((name, mfcc))
            if canonical is None and label == "lessac" and abs(scale - 1.0) < 1e-9:
                canonical = trimmed
            print(f"  {name}: {len(mfcc)} frames x {mfcc.shape[1]} mfcc ({trimmed.size/FS:.2f}s)")

    if canonical is None:
        raise SystemExit("no canonical utterance generated — aborting")

    # --- write templates ---
    manifest = []
    for name, mfcc in templates:
        path = RESOURCES / f"{name}.mfc"
        save_template(str(path), mfcc)
        manifest.append(path.name)
    print(f"templates -> {RESOURCES}")

    # --- canonical PCM (embedded resource; drives the C# contract test) ---
    pcm_path = RESOURCES / "canonical_hey_chorus.pcm"
    canonical.astype("<i2").tofile(pcm_path)
    print(f"canonical PCM -> {pcm_path} ({canonical.size/FS:.2f}s)")

    # --- Python MFCC fixture (the C# test must reproduce this matrix) ---
    canon_mfcc = mfcc_stream(canonical)
    fix = {
        "fs": FS, "win": WIN_LEN, "hop": HOP, "dim": int(canon_mfcc.shape[1]),
        "frames": int(canon_mfcc.shape[0]),
        "matrix": canon_mfcc.tolist(),
    }
    fix_path = FIXTURES / "canonical_mfcc.json"
    fix_path.write_text(json.dumps(fix))
    print(f"mfcc fixture -> {fix_path} ({canon_mfcc.shape[0]} frames)")

    # --- sanity: the reference spotter must self-detect the canonical PCM ---
    try:
        from .wakeword_lib import WakeWordSpotter
    except ImportError:
        from wakeword_lib import WakeWordSpotter
    spot = WakeWordSpotter([m for _, m in templates], sensitivity=0.5)
    trigs = spot.feed(canonical)
    print(f"self-check: {len(trigs)} trigger(s) on canonical PCM")
    for t in trigs:
        print(f"  score={t.score:.3f} template={t.template_index}")
    if not trigs:
        print("  !! WARNING: reference spotter did not self-detect")

    print("\nDone. Template manifest:")
    for m in manifest:
        print(f"  {m}")


if __name__ == "__main__":
    main()
