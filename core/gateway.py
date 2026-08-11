"""CHORUS Voice Gateway — the keystone service.

Implements docs/chorus-protocol-v1.md:
  wss://voice.<host>/v1/session
  hello -> hello_ack, audio OPUS frames, vad/ptt/wake/barge_in/cancel,
  partial/final, turn events, agent_text, TTS audio frames, voicemail, error.

v1 scope: PTT + wake open speech windows, lexical Turn-Completion Predictor,
barge-in on PENDING/SPEAKING, dictate + converse modes, echo/Hermes adapters,
mock or real speech engines (registry-driven).

Run:
  CHORUS_MOCK=1 ./venv/bin/python -m core.gateway   # conformance mode
  ./venv/bin/python -m core.gateway                  # real engines
"""

from __future__ import annotations

import asyncio
import json
import os
import time
import uuid

import websockets
from websockets.server import WebSocketServerProtocol

from .state import TurnMachine, TurnState, Complete
from .predictor import classify
from agents.registry import AgentRegistry
from engines.speech import SpeechRegistry

PROTO = "1.0"
USE_MOCK = os.environ.get("CHORUS_MOCK", "0") == "1"


class Session:
    def __init__(self, ws, session_id: str, device: str, mode: str, agent_id: str,
                 speech: SpeechRegistry, agents: AgentRegistry):
        self.ws = ws
        self.session_id = session_id
        self.device = device
        self.mode = mode  # "converse" | "dictate"
        self.agent_id = agent_id
        self.speech = speech
        self.agents = agents
        self.machine = TurnMachine()
        self.machine.open(session_id, agent_id)
        self._audio_buf = bytearray()  # PCM16 accumulate per turn
        self._tts_task: asyncio.Task | None = None
        self._pending_task: asyncio.Task | None = None
        self._wake_task: asyncio.Task | None = None
        self._transcript: list[dict] = []
        self._seq = 0

    # -- send helpers ------------------------------------------------------
    async def send_json(self, obj: dict) -> None:
        await self.ws.send(json.dumps(obj))

    async def send_turn(self) -> None:
        await self.send_json(self.machine.event())

    async def send_error(self, code: str, detail: str = "") -> None:
        d = {"type": "error", "code": code}
        if detail:
            d["detail"] = detail
        await self.send_json(d)

    # -- audio handling ----------------------------------------------------
    async def on_audio(self, frame: bytes) -> None:
        """One binary OPUS frame from the client. Decode to PCM, buffer."""
        if self.machine.state not in (TurnState.LISTENING, TurnState.IDLE):
            # audio outside a speech window is ignored (client VAD misbehaviour)
            return
        try:
            import opuslib
            dec = opuslib.Decoder(16000, 1)
            pcm = dec.decode(frame, 320)  # 20ms @ 16k = 320 samples
            self._audio_buf += pcm
        except Exception:
            return

    def _drain_pcm(self) -> bytes:
        b = bytes(self._audio_buf)
        self._audio_buf.clear()
        return b

    # -- turn handling ------------------------------------------------------
    async def end_speech(self, final_text: str | None = None) -> None:
        """vad speech_end / ptt up: run predictor, enter PENDING."""
        pcm = self._drain_pcm()
        if pcm:
            stt = self.speech.stt_provider()
            try:
                text = await stt.transcribe(pcm)
            except Exception as e:
                await self.send_error("internal", f"stt failed: {e}")
                text = ""
            if text:
                await self.send_json({"type": "final", "text": text})
                # Interpretation layer: reconstruct intended words from
                # context (fixes accent/misrecognition mangling).
                agent = self.agents.get(self.agent_id)
                if agent is not None:
                    try:
                        corrected = await agent.handler.correct_transcript(text, self._transcript)
                    except Exception:
                        corrected = text
                    if corrected and corrected != text:
                        await self.send_json({"type": "corrected", "text": corrected})
                        text = corrected
                self._transcript.append({"role": "user", "text": text})
        else:
            text = final_text or ""

        if not text:
            # no speech — if this was a wake window, it just expires
            self.machine.cancel()
            await self.send_turn()
            return

        # barge-in vs pending decision: the predictor runs here
        verdict = classify(text)
        self.machine.end_speech(verdict)
        await self.send_turn()

        # schedule the PENDING timeout -> if it fires, dispatch
        async def _pending_expiry():
            await asyncio.sleep(self.machine.pending_timeout_ms / 1000)
            if self.machine.state == TurnState.PENDING:
                await self.dispatch(text)

        self._pending_task = asyncio.create_task(_pending_expiry())

    async def dispatch(self, text: str) -> None:
        """PROCESSING -> agent reply -> SPEAKING (TTS frames)."""
        if self.machine.state != TurnState.PENDING:
            return
        self.machine.process()
        await self.send_turn()

        agent = self.agents.get(self.agent_id)
        try:
            reply = await agent.handler.reply(text, self.session_id, self._transcript)
        except Exception as e:
            await self.send_error("internal", f"agent failed: {e}")
            self.machine.cancel()
            await self.send_turn()
            return

        await self.send_json({"type": "agent_text", "agent": self.agent_id, "text": reply})
        self._transcript.append({"role": "agent", "text": reply, "agent": self.agent_id})

        self.machine.speak()
        await self.send_turn()

        tts = self.speech.tts_provider()
        voice = agent.voice if agent else "en_US-lessac-medium"
        try:
            pcm = await tts.synthesize(reply, voice=voice)
            # stream as OPUS frames (24kHz)
            import opuslib
            enc = opuslib.Encoder(24000, 1, opuslib.APPLICATION_VOIP)
            frame_ms = 20
            frame_samples = int(24000 * frame_ms / 1000)  # 480 samples @ 24k
            frame_len = frame_samples * 2  # bytes
            for i in range(0, len(pcm), frame_len):
                chunk = pcm[i:i + frame_len]
                if len(chunk) < frame_len:
                    chunk += b"\x00\x00" * (frame_len - len(chunk))
                op = enc.encode(chunk, frame_samples)
                await self.send_json({"type": "audio", "seq": self._seq, "agent": self.agent_id})
                self._seq += 1
                await self.ws.send(op)
                # Pace to real-time (20ms of audio per frame) so the client's
                # playback buffer drains at consumption rate instead of
                # overflowing and dropping the tail of long replies.
                await asyncio.sleep(frame_ms / 1000.0)
                if self.machine.state != TurnState.SPEAKING:
                    break  # barge-in stopped playback
        except Exception as e:
            await self.send_error("tts_failed", str(e))

        # back to LISTENING
        self.machine.barge_in() if self.machine.state == TurnState.SPEAKING else None
        self.machine.state = TurnState.LISTENING
        await self.send_turn()

    async def on_barge_in(self) -> None:
        if self.machine.state in (TurnState.PENDING, TurnState.SPEAKING):
            if self._pending_task:
                self._pending_task.cancel()
            self.machine.barge_in()
            await self.send_turn()

    # -- dictate mode --------------------------------------------------------
    async def dictate_audio(self, frame: bytes) -> None:
        await self.on_audio(frame)

    async def on_wake(self) -> None:
        """wake word fired: open speech window with expiry timer."""
        self.machine.start_speech(via="wake")
        await self.send_turn()
        # schedule the wake window expiry (configurable for tests)
        window_ms = int(os.environ.get("CHORUS_WAKE_MS", "20000"))

        async def _expire():
            await asyncio.sleep(window_ms / 1000)
            if self.machine.state == TurnState.LISTENING:
                await self.send_error("wake_timeout", "no speech in wake window")
                self.machine.cancel()
                await self.send_turn()

        self._wake_task = asyncio.create_task(_expire())

    async def dictate_end(self) -> None:
        pcm = self._drain_pcm()
        if not pcm:
            return
        stt = self.speech.stt_provider()
        try:
            text = await stt.transcribe(pcm)
        except Exception as e:
            await self.send_error("internal", f"stt failed: {e}")
            return
        if text:
            await self.send_json({"type": "final", "text": text})

    # -- lifecycle -----------------------------------------------------------
    async def close(self) -> None:
        if self._pending_task:
            self._pending_task.cancel()
        if self._tts_task:
            self._tts_task.cancel()


class Gateway:
    def __init__(self, speech: SpeechRegistry, agents: AgentRegistry):
        self.speech = speech
        self.agents = agents
        self.sessions: dict[str, Session] = {}

    def _new_session_id(self) -> str:
        return uuid.uuid4().hex

    async def handler(self, ws: WebSocketServerProtocol, path: str | None = None) -> None:
        session = None
        try:
            async for raw in ws:
                if isinstance(raw, bytes):
                    if session:
                        if session.mode == "dictate":
                            await session.dictate_audio(raw)
                        else:
                            await session.on_audio(raw)
                    continue

                try:
                    msg = json.loads(raw)
                except Exception:
                    if session:
                        await session.send_error("proto_violation", "invalid json")
                    else:
                        await ws.send(json.dumps({
                            "type": "error", "code": "proto_violation",
                            "detail": "invalid json",
                        }))
                    continue
                t = msg.get("type")

                if t == "hello":
                    if session:
                        await session.send_error("proto_violation", "hello twice")
                        continue
                    proto = msg.get("proto", "")
                    if proto.split(".")[0] != PROTO.split(".")[0]:
                        await ws.send(json.dumps({
                            "type": "error", "code": "proto_violation",
                            "detail": f"unsupported proto {proto}",
                        }))
                        continue
                    sid = msg.get("session_id") or self._new_session_id()
                    session = Session(
                        ws, sid, msg.get("device", ""), msg.get("mode", "converse"),
                        msg.get("agent", "hermes"), self.speech, self.agents,
                    )
                    self.sessions[sid] = session
                    await session.send_json({
                        "type": "hello_ack", "session_id": sid, "proto": PROTO,
                        "mode": session.mode, "agent_roster": self.agents.roster(),
                    })
                    continue

                if session is None:
                    await ws.send(json.dumps({
                        "type": "error", "code": "proto_violation",
                        "detail": "hello first",
                    }))
                    continue

                if t == "audio":
                    pass  # binary handled above; control-only marker
                elif t == "vad":
                    if session.mode == "dictate":
                        if msg.get("state") == "speech_end":
                            await session.dictate_end()
                        continue
                    if msg.get("state") == "speech_end":
                        await session.end_speech()
                    elif msg.get("state") == "speech_start":
                        if session.machine.state in (TurnState.PENDING, TurnState.SPEAKING):
                            # talking during pending/speaking = barge-in
                            await session.on_barge_in()
                        else:
                            session.machine.start_speech(via="vad")
                elif t == "ptt":
                    if msg.get("state") == "down":
                        session.machine.start_speech(via="ptt")
                        await session.send_turn()
                    else:  # up
                        await session.end_speech()
                elif t == "wake":
                    await session.on_wake()
                elif t == "barge_in":
                    await session.on_barge_in()
                elif t == "cancel":
                    session.machine.cancel()
                    await session.send_turn()
                elif t == "ping":
                    await session.send_json({"type": "pong"})
                elif t == "bye":
                    await session.send_json({"type": "bye_ack", "session_id": session.session_id})
                    break
                else:
                    await session.send_error("proto_violation", f"unknown type {t}")
        except websockets.exceptions.ConnectionClosed:
            pass
        except Exception:
            pass
        finally:
            if session:
                self.sessions.pop(session.session_id, None)
                await session.close()

    async def run(self, host: str = "0.0.0.0", port: int = 8765) -> None:
        async with websockets.serve(self.handler, host, port):
            print(f"[chorus] gateway listening on wss://{host}:{port} (mock={USE_MOCK})")
            await asyncio.Future()


def main() -> None:
    speech = SpeechRegistry.build(use_mock=USE_MOCK)
    agents = AgentRegistry.build(use_mock=USE_MOCK)
    gw = Gateway(speech, agents)
    asyncio.run(gw.run(port=int(os.environ.get("CHORUS_PORT", "8765"))))


if __name__ == "__main__":
    main()
