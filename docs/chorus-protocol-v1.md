# CHORUS Voice Protocol v1 — Message-Type Discipline

Status: authoritative contract for CHORUS Phase 1 (gateway + desktop SysTray
client + VoiceMic web component).
Endpoint: `ws://voice.<host>/v1/session` (dev: `ws://2.28.14.119:8765/v1/session`)
Proto string: `1.0`

## 1. The ONE rule that fixes gobbledygook

**The WebSocket carries exactly two frame kinds, and they are never mixed:**

| Frame kind | Meaning | What the receiver MUST do |
|---|---|---|
| `binary` | OPUS-encoded audio (TTS out / mic in) | Decode with Opus **only**. Never parse as text. Never speak it as JSON. |
| `text` | JSON event | `json.loads` and dispatch on `type`. **Never** feed to the OPUS decoder. **Never** read aloud. |

A client's ReceiveLoop MUST branch on `isinstance(frame, bytes)` (binary → OPUS
decoder) vs text (→ JSON event). There is no third kind. If a text frame is ever
decoded as OPUS — or a binary frame is ever parsed as JSON — the result is the
"gobbledygook" bug (protocol metadata/JSON text audible through the speaker).

## 2. Event vocabulary (text frames)

### Client → server
- `hello` — `{type, proto, device?, mode?, agent?, session_id?}`. `mode`: `converse` | `dictate`. Must be the first message.
- `ptt` — `{type:"ptt", state:"down"|"up"}`. Opens/closes a push-to-talk speech window.
- `wake` — `{type:"wake"}`. Opens a wake-word speech window (default 20 s, `CHORUS_WAKE_MS`).
- `vad` — `{type:"vad", state:"speech_start"|"speech_end"}`. Speech detection events.
- `audio` — `{type:"audio", seq:N}`. Control-only marker, sent by the client immediately before each binary mic frame. The server ignores the payload (`pass`); the marker exists so a receiver can attribute the following binary frame.
- `barge_in` — `{type:"barge_in"}`. User speech during PENDING/SPEAKING.
- `cancel` — `{type:"cancel"}`. Abort the current turn.
- `ping` — `{type:"ping"}` → server answers `{type:"pong"}`.
- `bye` — `{type:"bye"}` → server answers `{type:"bye_ack", session_id}`.

### Server → client
- `hello_ack` — `{type, session_id, proto, mode, agent_roster:[{id, display_name, voice}]}`.
- `turn` — `{type:"turn", state:"idle"|"listening"|"pending"|"processing"|"speaking"}`; `pending` additionally carries `complete:"thinking"|"likely"|"uncertain"` and `timeout_ms`.
- `final` — `{type:"final", text}`. User transcript (STT result).
- `agent_text` — `{type:"agent_text", agent, text}`. The agent's reply as TEXT. **Captions only — never spoken.** The spoken form is the binary OPUS stream that follows.
- `audio` — `{type:"audio", seq:N, agent}`. Server-side marker sent immediately before each binary TTS OPUS frame (same convention as client→server).
- `error` — `{type:"error", code, detail?}`. Codes: `proto_violation`, `internal`, `tts_failed`, `wake_timeout`.
- `pong` — reply to `ping`.
- `bye_ack` — `{type:"bye_ack", session_id}`.

## 3. Turn lifecycle

```
IDLE --ptt down/wake/vad start--> LISTENING --ptt up/vad end--> PENDING
PENDING --(predictor verdict timeout)--> PROCESSING --agent reply--> SPEAKING
SPEAKING --TTS frames streamed--> LISTENING
any user speech during PENDING/SPEAKING = barge_in -> LISTENING
```

Clients mirror server `turn` events; they never compute transitions themselves.
`pending.timeout_ms` is the dynamic countdown: likely 1100 ms, uncertain 3000 ms,
thinking 8000 ms. The client starts the ring/indicator on `pending`, and if no
further event arrives before `timeout_ms`, the server will dispatch (PROCESSING).

## 4. Audio

- **Mic in (client → server):** 16 kHz mono, 20 ms frames (320 samples/frame),
  OPUS/VOIP. Client sends `{type:"audio", seq}` text marker, then the binary frame.
- **TTS out (server → client):** 24 kHz mono, 20 ms frames (480 samples/frame),
  OPUS/VOIP. Server sends `{type:"audio", seq, agent}` text marker, then the binary
  frame. Decode with
  `OpusDecoder.Decode(ReadOnlySpan<byte>, Span<short>, frameSize, fec:false)` (Concentus 2.1.0).
- Audio outside a speech window is ignored by the server (client VAD misbehaviour guard).

## 5. Conformance

`/opt/chorus/tests/conformance.py` — 32 checks across 7 scenarios (happy path,
barge-in, pending cancel, wake timeout, error matrix, dictate, resume).
Run: `CHORUS_MOCK=1 ./venv/bin/python -m tests.conformance` → expect `32 passed, 0 failed`.

## 6. Gobbledygook checklist (client side)

1. ReceiveLoop branches on frame kind BEFORE anything else:
   `if (e.MessageType == WebSocketMessageType.Binary) → opus decode; else → JSON parse`.
2. `agent_text` and `final` are rendered in the transcript window; they are NEVER
   passed to the TTS/SAPI reader and never sent to the OPUS decoder.
3. The text `audio` marker is a control event; the audio arrives in the NEXT binary
   frame. Do not treat the marker itself as audio.
4. Nothing textual is ever audible: captions come from JSON `text` fields; sound
   comes exclusively from binary OPUS frames.
