# CHORUS Text Select — ScreenToTextToSpeech

Status: implemented (card `3b8e6043-cad1-8137`, split out of CHORUS Phase 1
client `3b8e6043-cad1-8158`). Separate feature, **no gateway involvement** —
selection, OCR and reading are all local to the Windows client.

## What it does

Select any on-screen text or image and have CHORUS read it aloud:

1. **Trigger** — global hotkey **Win+Shift+R**, tray menu **"Read Screen
   Text"**, or the console **"Read Screen"** button. Pressing again while the
   overlay is up cancels it; pressing while reading stops the reading
   (toggle).
2. **Overlay (card Option 1)** — the desktop freezes and is dimmed by a
   semi-opaque layer. Click-drag-release defines a transparent rectangle over
   the text/image (the selected area stays fully readable; a live size readout
   follows the cursor). Esc cancels.
3. **OCR** — the selected region is cropped from the frozen desktop and
   recognized with the **built-in Windows OCR engine** (`Windows.Media.Ocr`,
   WinRT). No tesseract binaries or language packs to ship — it uses the OCR
   language packs already on Windows 10/11.
4. **Read aloud** — recognized text is cleaned (control chars, whitespace
   runs, NBSP) and spoken in **bounded chunks via SAPI** (`System.Speech`).
   Chunking matters: SAPI `Speak()` becomes unreliable past a few hundred
   chars (the ClipReader v1.1 incident), so long selections are split at
   sentence/paragraph boundaries (max 400 chars/chunk).

The read is fully local. PTT (Win+Shift+T) or wake (Win+Shift+W) while
reading stops it — the mic always takes priority.

## Architecture

```
src/Chorus.Core/ScreenText/          portable, unit-tested on any OS
  ScreenRect.cs       drag normalization, DPI scale, OCR fit-within geometry
  OcrTextCleaner.cs   raw OCR -> clean speakable text (+ preview helper)
  TtsChunker.cs       SAPI-safe chunking (400-char cap, sentence-aware)
src/Chorus.App/
  TextSelectController.cs    orchestration: capture -> overlay -> OCR -> speak
  TextSelectOverlayForm.cs   dim layer + drag rectangle (card Option 1)
  WindowsOcrReader.cs        WinRT Windows.Media.Ocr reader (Windows-only)
  SapiSpeechSynthesizer.cs   dedicated STA worker + BlockingCollection queue
tests/Chorus.Core.Tests/     29 new unit tests (58 total)
tests/Chorus.TextPipelineCheck/  Linux harness: OCR text -> clean/chunk
tests/textselect_pipeline_check.py  PIL render -> tesseract -> pipeline check
```

## Why WinRT OCR (and not Tesseract)

The card allows either. WinRT was chosen because:

- Zero packaging: no `tesseract.dll`/`leptonica` natives, no `eng.traineddata`
  download; the engine is already on every Windows 10/11 machine (OCR language
  packs ship with the display language).
- Better accuracy on modern UI text than stock tesseract.
- Fallback is graceful: if no OCR language pack is installed, the console
  reports it instead of failing silently.

## Verification status

- Unit tests: **58/58 pass** (29 pre-existing + 29 new: geometry, cleaner,
  chunker).
- win-x64 self-contained publish: green (`Chorus.exe` + `opus.dll`), with
  `System.Speech.dll` + Windows SDK projections embedded in the single file.
- Linux pipeline check: PIL renders a text image → tesseract OCR → the exact
  `OcrTextCleaner`/`TtsChunker` code produces clean SAPI-safe chunks
  (`python3 tests/textselect_pipeline_check.py`).
- Not verified on this box (needs Windows): the overlay interaction, WinRT OCR
  accuracy, and SAPI audio — Loretta reviews on the Windows machine.

## Future

- Option 2 (see-through console region) if the overlay feels heavy.
- Copyable-text fast path (UI Automation `TextPattern`) before falling back to
  OCR.
