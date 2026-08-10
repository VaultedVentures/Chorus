# CHORUS desktop client — .NET 9 WinForms (Windows) + headless smoke harness.

## What this is

The CHORUS desktop SysTray voice client (Scott-approved two-surface design):

1. **Voice console window (PRIMARY)** — a real Form: conversation transcript,
   big turn-state indicator (idle / listening / thinking / processing /
   speaking), captions, mute, agent selector, reconnect. Movable, resizable,
   pinnable (always-on-top), minimizable-to-tray. Closing hides to the tray.
2. **SysTray daemon** — owns the global hotkeys, the mic, auto-reconnect and
   status. Win+Shift+T = hold-to-talk, Win+Shift+W = wake window. Works from
   ANY app, even with the console hidden.

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
# unit tests (protocol parsing + opus codec)
dotnet test tests/Chorus.Core.Tests

# smoke against the live gateway (no Windows needed)
dotnet run --project tests/Chorus.Smoke -- --url ws://2.28.14.119:8765/v1/session
dotnet run --project tests/Chorus.Smoke -- --wav test-input-16k-mono.wav   # full PTT exchange
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

The session id is persisted as `session.id` next to the EXE so reconnects and
restarts resume the same gateway session.

## Microphone permission

On startup the app probes the microphone. If Windows blocks mic access
(privacy settings) or no input device exists, the failure is surfaced
immediately: a tray balloon, a red status line in the console transcript, and
the tray tooltip. Fix in Windows Settings → Privacy & security → Microphone,
then retry via the tray menu.
