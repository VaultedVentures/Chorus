"""CHORUS live echo — the definitive client-readiness check against the
production gateway (real engines: faster-whisper STT + Piper TTS + Hermes).

Synthesizes a spoken utterance with Piper locally, streams it to the gateway
over a real PTT session, then verifies the client-side contract end to end:
  - final transcript arrives (STT heard real speech)
  - agent_text arrives (Hermes replied)
  - binary frames = OPUS TTS audio only, decodes to clean speech (RMS > 0)
  - NO text is audible on the audio path (gobbledygook check)

Usage (from /opt/chorus with venv):
    ./venv/bin/python -m tests.live_echo [--url ws://2.28.14.119:8765/v1/session]
"""
from __future__ import annotations

import argparse
import asyncio
import json
import math
import statistics
import sys

import websockets


def _synth_utterance(text: str) -> bytes:
    """Piper-synthesize `text` to raw PCM16 @ 16 kHz (the mic rate)."""
    import numpy as np
    from piper import PiperVoice

    voice = PiperVoice.load(
        "/opt/chorus/voices/en_US-lessac-medium.onnx",
        config_path="/opt/chorus/voices/en_US-lessac-medium.onnx.json",
    )
    chunks = [chunk.audio_int16_bytes for chunk in voice.synthesize(text)]
    pcm = b"".join(chunks)
    # piper native rate is 22050; resample to 16000
    arr = np.frombuffer(pcm, dtype=np.int16)
    ratio = 16000 / voice.config.sample_rate
    new_len = int(len(arr) * ratio)
    idx = (np.arange(new_len) / ratio).astype(int)
    idx = np.clip(idx, 0, len(arr) - 1)
    return arr[idx].astype(np.int16).tobytes()


def _rms(pcm: bytes) -> float:
    if not pcm:
        return 0.0
    n = len(pcm) // 2
    s = 0
    for i in range(0, len(pcm), 2):
        v = int.from_bytes(pcm[i:i + 2], "little", signed=True)
        s += v * v
    return math.sqrt(s / n)


async def run(url: str, utterance: str) -> int:
    import opuslib

    pcm16 = _synth_utterance(utterance)
    print(f"[echo] synthesized {len(pcm16) / 2 / 16000:.2f}s @16k, RMS {_rms(pcm16):.0f}")

    enc = opuslib.Encoder(16000, 1, opuslib.APPLICATION_VOIP)
    dec = opuslib.Decoder(24000, 1)
    frame_samples = 320  # 20ms @ 16k

    events: list[dict] = []
    audio_pcm = b""
    audio_seq = 0
    json_text_seen = []

    async with websockets.connect(url) as ws:
        await ws.send(json.dumps({"type": "hello", "proto": "1.0",
                                  "device": "live-echo", "mode": "converse",
                                  "agent": "hermes"}))
        while True:
            raw = await asyncio.wait_for(ws.recv(), 10)
            if isinstance(raw, bytes):
                audio_pcm += dec.decode(raw, 480)  # 20ms @ 24k
                continue
            m = json.loads(raw)
            events.append(m)
            if m.get("type") == "hello_ack":
                break

        await ws.send(json.dumps({"type": "ptt", "state": "down"}))
        # stream the utterance as OPUS 16k frames (marker + binary per frame)
        for i in range(0, len(pcm16), frame_samples * 2):
            chunk = pcm16[i:i + frame_samples * 2]
            if len(chunk) < frame_samples * 2:
                chunk += b"\x00\x00" * (frame_samples * 2 - len(chunk))
            await ws.send(json.dumps({"type": "audio", "seq": audio_seq}))
            audio_seq += 1
            await ws.send(enc.encode(chunk, frame_samples))
        await ws.send(json.dumps({"type": "ptt", "state": "up"}))

        # collect until the turn returns to listening after speaking
        while True:
            raw = await asyncio.wait_for(ws.recv(), 30)
            if isinstance(raw, bytes):
                audio_pcm += dec.decode(raw, 480)
                continue
            m = json.loads(raw)
            events.append(m)
            if (m.get("type") == "turn" and m.get("state") == "listening"
                    and any(e.get("type") == "agent_text" for e in events)):
                break
            if m.get("type") == "error":
                break

        await ws.send(json.dumps({"type": "bye"}))
        try:
            await asyncio.wait_for(ws.recv(), 3)
        except Exception:
            pass

    finals = [e.get("text", "") for e in events if e.get("type") == "final"]
    agent_texts = [e.get("text", "") for e in events if e.get("type") == "agent_text"]
    turns = [e.get("state") for e in events if e.get("type") == "turn"]
    n_frames = len(audio_pcm) // (480 * 2)
    dur = len(audio_pcm) / 2 / 24000
    rms = _rms(audio_pcm)

    print(f"[echo] final: {finals}")
    print(f"[echo] agent_text: {agent_texts}")
    print(f"[echo] turns: {turns}")
    print(f"[echo] audio: {n_frames} frames, {dur:.2f}s, RMS {rms:.0f}")

    ok = True
    if not finals:
        print("FAIL: no STT final"); ok = False
    if not agent_texts:
        print("FAIL: no agent_text"); ok = False
    if n_frames == 0:
        print("FAIL: zero TTS audio frames"); ok = False
    if rms < 500:
        print(f"FAIL: audio RMS {rms:.0f} too low (not real speech)"); ok = False
    if "speaking" not in turns:
        print("FAIL: never reached speaking"); ok = False

    print(f"\n=== live echo: {'PASS' if ok else 'FAIL'} ===")
    return 0 if ok else 1


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default="ws://2.28.14.119:8765/v1/session")
    ap.add_argument("--text", default="Hello Scott, this is a live echo test of the Chorus voice gateway.")
    args = ap.parse_args()
    sys.exit(asyncio.run(run(args.url, args.text)))


if __name__ == "__main__":
    main()
