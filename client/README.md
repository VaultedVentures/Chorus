# CHORUS desktop client — .NET 9 WinForms (Windows) + headless smoke harness.

## What this is

The CHORUS desktop SysTray voice client (Scott-approved two-surface design):

1. **Voice console window (PRIMARY)** — a real Form: conversation transcript,
   big turn-state indicator (idle / listening / thinking / processing /
   speaking), captions, mute, agent selector, reconnect. Movable, resizable,
   pinnable (always-on-top), minimizable-to-tray. Closing hides to the tray.
2. **SysTray daemon** — owns the global hotkeys, the mic, auto-reconnect and
   status. **Ctrl+Shift+Space** = hold-to-talk, Win+Shift+W = wake window,
   Win+Shift+R = read screen text. Works from ANY app, even with the console
   hidden. All three combos are configurable via `chorus.json`
   (`PttHotkey` / `WakeHotkey` / `TextSelectHotkey`).
3. **Text Select (ScreenToTextToSpeech)** — Win+Shift+R (or tray "Read Screen
   Text" / console "Read Screen" button) dims the screen; click-drag a
   rectangle over any text/image; CHORUS OCRs the region with the built-in
   Windows OCR engine and reads it aloud locally via SAPI. Fully local, no
   gateway involvement. See `docs/chorus-text-select.md`.

Wire contract: `docs/chorus-protocol-v1.md` — binary frames are OPUS audio
only; text frames are JSON events only. The receive-loop branch that enforces
this lives in `src/Chorus.Core/ChorusClient.cs` (the gobbledygook fix).

## Layout

```
src/Chorus.Core/     protocol client + OPUS codec (Concentus) — portable, no UI
src/Chorus.App/      WinForms voice console + tray daemon + global hotkeys + NAudio
tests/Chorus.Core.Tests/  unit tests (run on any OS)
tests/Chorus.Smoke/       headless smoke test of the protocol client (any OS)
```

## Build

Requires .NET SDK 9 (any OS; WinForms cross-compiles from Linux/macOS):

```bash
./build.sh                 # = build + publish self-contained win-x64
./build.sh Release win-x64
```

Deployable: `dist/win-x64/Chorus.exe` — copy to Windows and run.

## Verify

```bash
# unit tests (protocol parsing + opus codec + screen-text pipeline)
dotnet test tests/Chorus.Core.Tests

# smoke against the live gateway (no Windows needed)
dotnet run --project tests/Chorus.Smoke -- --url ws://2.28.14.119:8765/v1/session
dotnet run --project tests/Chorus.Smoke -- --wav test-input-16k-mono.wav   # full PTT exchange

# screen-text pipeline on Linux (PIL render -> tesseract OCR -> clean/chunk;
# WinRT OCR itself is Windows-only)
python3 tests/textselect_pipeline_check.py
```

> Linux note: the Concentus native loader dlopens `libopus.so` directly (it does
> not follow the .NET `runtimes/<rid>/native/` convention). On Linux, put
> `libopus.so` on the loader path (`LD_LIBRARY_PATH=.../runtimes/linux-x64/native`
> or `/usr/lib`) or the smoke falls back to the managed decoder, which decodes
> low-bitrate 24 kHz frames to near-silence. On Windows, `build.sh` copies
> `opus.dll` next to `Chorus.exe` — that is what the runtime loader finds.

## Config

Configuration is loaded from `chorus.json` next to the EXE (a default file is
written on first run; see `chorus.example.json`). Environment variables
override the file. Precedence: env var > config file > built-in default.

| Field           | Env var              | Default                              | Meaning                        |
|-----------------|----------------------|--------------------------------------|--------------------------------|
| `GatewayUrl`    | `CHORUS_URL`         | `ws://2.28.14.119:8765/v1/session`   | Gateway endpoint               |
| `Agent`         | `CHORUS_AGENT`       | `hermes`                             | Default agent id               |
| `MicDevice`     | `CHORUS_DEVICE`      | `""` (system default)                | Mic device: `""`, index (`"3"`), or name substring (`"USB Audio"`) |
| `StartHidden`   | `CHORUS_START_HIDDEN`| `true`                               | Start to tray with no main window (console via tray menu) |
| `MicBufferMs`   | —                    | `20`                                 | Capture frame size (20 ms @ 16 kHz = 320 samples) |
| `ClientDevice`  | —                    | `desktop-win`                        | Device id sent in the hello handshake |
| `PttHotkey`     | `CHORUS_PTT_HOTKEY`  | `Ctrl+Shift+Space`                   | Hold-to-talk combo (global)    |
| `WakeHotkey`    | `CHORUS_WAKE_HOTKEY` | `Win+Shift+W`                        | Manual wake-window combo (hotkey path) |
| `TextSelectHotkey`| `CHORUS_TEXT_SELECT_HOTKEY` | `Win+Shift+R`                 | Read-screen-text combo         |
| `WakePhrase`    | `CHORUS_WAKE_PHRASE` | `hey chorus`                         | Wake phrase (acoustic model is fixed to "hey chorus" — the setting labels the engine, it does not retrain it) |
| `WakeEnabled`   | `CHORUS_WAKE_ENABLED`| `true`                               | Continuous wake-word listening on startup (tray/console "Wake" checkbox toggles live) |
| `WakeSensitivity` | `CHORUS_WAKE_SENSITIVITY` | `0.4` (0..1)                    | Higher triggers more easily (more false positives); 0 = same-voice clear speech only |
| `WakeCooldownMs`| `CHORUS_WAKE_COOLDOWN_MS` | `2000`                            | Min gap between wake triggers (double-trigger suppression) |
| `WakeSessionIdleMs` | `CHORUS_WAKE_SESSION_IDLE_MS` | `45000`                  | Wake session auto-closes after this much silence, re-arming continuous listening |

Hotkey syntax: one or more modifiers (`Ctrl`, `Alt`, `Shift`, `Win`) joined
with `+`, then a key (`A`-`Z`, `0`-`9`, `F1`-`F24`, `Space`, `Tab`, `Enter`,
`Esc`, `Home`, `End`, `PageUp`, `PageDown`, `Insert`, `Delete`, `Backspace`,
arrows). At least one modifier is required. Invalid specs fall back to the
default with a tray warning.

## Push-to-talk behavior

- Press and HOLD the PTT combo from any application — the mic opens, audio
  streams to the gateway, the tray icon turns amber and the tooltip shows
  "transmitting". Release to stop: the mic closes, in-flight frames are
  flushed, and `ptt up` closes the stream.
- The combo is registered at the OS level (`RegisterHotKey`), so it never
  reaches the focused application.
- Release is detected by polling the physical key state, so it works even if
  you switch apps while holding; a 60 s watchdog force-releases a wedged key.
- If another application already owns your chosen combo, CHORUS shows a tray
  balloon naming the conflict — change `PttHotkey` in `chorus.json`.

The session id is persisted as `session.id` next to the EXE so reconnects and
restarts resume the same gateway session.

## Wake word behavior

With `WakeEnabled` on (default), CHORUS listens for the wake phrase — by
default **"hey chorus"** — continuously and offline. There is no cloud
involvement: the packaged acoustic model (21 MFCC templates of the phrase, in
three voices and speaking rates, embedded in `Chorus.Core.dll`) is matched
against the mic stream by a banded online-DTW matcher running on the capture
thread (~1 FFT + 26 mel filters per 10 ms hop — negligible CPU).

- **Trigger path** — hearing the phrase emits the same `wake` event as the
  Win+Shift+W hotkey: the server opens a speech window, the client gates the
  mic stream with its VAD, and the turn flows like a PTT turn. The wake word
  cannot fire mid-conversation (PTT hold, active wake session, or the agent
  speaking all suppress it).
- **Latency** — the matcher accepts a match only inside a 400 ms freshness
  window after the alignment ends, so a trigger lands within 500 ms of the
  phrase end by construction (verified: measured triggers land at −70…−220 ms,
  i.e. right at phrase end).
- **False-positive suppression** — an energy floor skips near-silence, a
  250 ms minimum-silence onset reset keeps partial phrases from stretching to
  fit a template, the DTW band rejects implausible rates, and the freshness
  window drops stale alignments. `WakeCooldownMs` (default 2000) suppresses a
  second trigger from the same phrase or an immediate repeat.
- **Mute** — while the console Mute checkbox is on, no wake events are
  emitted (engine-level mute + the app stops feeding frames).
- **Sensitivity** — `WakeSensitivity` maps 0..1 onto a DTW threshold:
  `0.30` (least sensitive, same-voice clear speech) to `0.40` (most
  sensitive, every measured true positive plus some acoustic near-misses).
  The default `0.4` → threshold `0.34`: all measured true positives score
  ≤ 0.332, near-misses ≥ 0.356.
- **Session end** — a wake session auto-closes after `WakeSessionIdleMs`
  (default 45 s) of silence, re-arming continuous listening for the next
  "hey chorus".
- **The engine is fixed to the phrase** — the acoustic model is the literal
  phrase "hey chorus". `WakePhrase` is a label/status string (and a future
  hook), it does not retrain the model. The console "Wake" checkbox and the
  tray/console status toggle continuous listening live.

The model and its Python reference DSP live in the repo so the whole chain is
regenerable: `tests/wakeword_templates.py` (builds the packaged `.mfc`
templates), `tests/wakeword_pipeline_check.py` (end-to-end acceptance:
latency ≤ 500 ms, muted, cooldown, non-targets), and the C# unit tests pin
the C# DSP against the Python-computed MFCC fixture.

## Microphone permission

On startup the app probes the microphone. If Windows blocks mic access
(privacy settings) or no input device exists, the failure is surfaced
immediately: a tray balloon, a red status line in the console transcript, and
the tray tooltip. Fix in Windows Settings → Privacy & security → Microphone,
then retry via the tray menu.
