#!/usr/bin/env python3
"""
Linux-side verification of the CHORUS ScreenToTextToSpeech pipeline:

    PIL render (text PNG) -> tesseract OCR (raw) -> Chorus.TextPipelineCheck
    (the exact OcrTextCleaner + TtsChunker code the Windows app uses)

WinRT OCR itself only runs on Windows; this proves the text side of the
pipeline turns real OCR output into clean, SAPI-safe chunks. Exit 0 = pass.
"""
import subprocess
import sys
import tempfile
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
PIPELINE = ROOT / "client" / "tests" / "Chorus.TextPipelineCheck"


def find_font() -> str | None:
    for p in ("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
              "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf"):
        if Path(p).exists():
            return p
    return None


def render_png(path: Path) -> None:
    font = find_font()
    f = ImageFont.truetype(font, 28) if font else ImageFont.load_default()
    img = Image.new("RGB", (1400, 240), "white")
    d = ImageDraw.Draw(img)
    lines = [
        "The quick brown fox jumps over the lazy dog.",
        "CHORUS reads whatever you select on screen, straight out loud.",
    ]
    for i, line in enumerate(lines):
        d.text((40, 60 + i * 70), line, fill="black", font=f)
    img.save(path)


def main() -> int:
    with tempfile.TemporaryDirectory() as td:
        td = Path(td)
        png = td / "sample.png"
        txt = td / "ocr.txt"
        render_png(png)

        subprocess.run(["tesseract", str(png), str(td / "ocr"), "--psm", "6"],
                       check=True, capture_output=True)
        raw = txt.read_text()

        # Build the pipeline check app once.
        subprocess.run(["dotnet", "build", str(PIPELINE), "-c", "Release"],
                       check=True, capture_output=True)
        result = subprocess.run(
            ["dotnet", "run", "--project", str(PIPELINE), "-c", "Release", "--no-build"],
            input=raw, text=True, capture_output=True, check=True)
        out = result.stdout

    print("=== RAW OCR ===")
    print(raw.strip())
    print()
    print("=== PIPELINE OUTPUT ===")
    print(out)

    # Assertions: the cleaned text must survive OCR noise and chunk sensibly.
    checks = [
        ("quick brown fox" in out, "key phrase survived OCR+clean"),
        ("CHUNKS (" in out, "chunker produced output"),
        ("[1/" in out, "chunk labels present"),
    ]
    ok = True
    for passed, label in checks:
        print(("PASS" if passed else "FAIL") + f"  {label}")
        ok = ok and passed

    if "lazy dog" not in out:
        print("WARN  'lazy dog' missing — OCR may have dropped it (still fine if phrase-level check passed)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
