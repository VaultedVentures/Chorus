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
| `WakeHotkey`    | `CHORUS_WAKE_HOTKEY` | `Win+Shift+W`                        | Wake-word window combo         |
| `TextSelectHotkey`| `CHORUS_TEXT_SELECT_HOTKEY` | `Win+Shift+R`                 | Read-screen-text combo         |

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

## Microphone permission

On startup the app probes the microphone. If Windows blocks mic access
(privacy settings) or no input device exists, the failure is surfaced
immediately: a tray balloon, a red status line in the console transcript, and
the tray tooltip. Fix in Windows Settings → Privacy & security → Microphone,
then retry via the tray menu.
