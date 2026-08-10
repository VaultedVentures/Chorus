"""CHORUS conformance suite — scripted WebSocket client asserting the spec.

Usage (from /opt/chorus with venv):
    CHORUS_MOCK=1 ./venv/bin/python -m tests.conformance

Each test = one scripted exchange, assert per event, exit non-zero on failure.
Per docs/chorus-protocol-v1.md §B: happy path, barge-in, pending cancel,
wake timeout, error matrix, dictate, resume.
"""

from __future__ import annotations

import asyncio
import json
import os
import sys

import websockets

URL = os.environ.get("CHORUS_URL", "ws://localhost:8765/v1/session")

PASS = 0
FAIL = 0


def check(cond: bool, label: str) -> None:
    global PASS, FAIL
    if cond:
        PASS += 1
        print(f"  ok  {label}")
    else:
        FAIL += 1
        print(f"  FAIL {label}")


async def ev(ws, timeout=3.0):
    """Read one message (text or binary); return (kind, payload)."""
    import websockets
    raw = await asyncio.wait_for(ws.recv(), timeout)
    if isinstance(raw, bytes):
        return "binary", raw
    return "json", json.loads(raw)


async def hello(ws, mode="converse", agent="hermes", session_id=""):
    await ws.send(json.dumps({
        "type": "hello", "proto": "1.0", "device": "conformance",
        "mode": mode, "agent": agent, "session_id": session_id,
    }))
    kind, m = await ev(ws)
    check(kind == "json" and m.get("type") == "hello_ack"
          and m.get("proto") == "1.0", "hello -> hello_ack with proto")
    if m.get("type") == "hello_ack":
        check(any(a["id"] == "hermes" for a in m.get("agent_roster", [])),
              "roster contains hermes")
    return m


async def _send_audio(ws, frames=3):
    """Send a few OPUS silence frames (16kHz 20ms)."""
    import opuslib
    enc = opuslib.Encoder(16000, 1, opuslib.APPLICATION_VOIP)
    silence = enc.encode(b"\x00\x00" * 320, 320)  # 20ms @ 16k = 320 samples
    for i in range(frames):
        await ws.send(json.dumps({"type": "audio", "seq": i}))
        await ws.send(silence)


async def test_happy_path():
    print("T1 happy path: ptt -> audio -> final -> pending -> processing -> speaking -> agent_text")
    async with websockets.connect(URL) as ws:
        await hello(ws)
        await ws.send(json.dumps({"type": "ptt", "state": "down"}))
        kind, m = await ev(ws)
        check(kind == "json" and m.get("type") == "turn" and m["state"] == "listening",
              "ptt down -> listening")
        await _send_audio(ws)
        await ws.send(json.dumps({"type": "ptt", "state": "up"}))
        seen = {"final": False, "pending": False, "processing": False,
                "speaking": False, "agent_text": False, "audio": False}
        for _ in range(8):
            kind, m = await ev(ws, timeout=5)
            if kind == "binary":
                seen["audio"] = True
                continue
            t = m.get("type")
            if t == "final":
                seen["final"] = True
            elif t == "turn":
                if m.get("state") == "pending":
                    seen["pending"] = True
                    check("timeout_ms" in m, "pending carries timeout_ms")
                elif m.get("state") == "processing":
                    seen["processing"] = True
                elif m.get("state") == "speaking":
                    seen["speaking"] = True
            elif t == "agent_text":
                seen["agent_text"] = True
                check(m.get("agent") == "hermes", "agent_text stamped with agent")
        check(all(seen.values()), "full happy-path event sequence")


async def test_barge_in():
    print("T2 barge-in: speech during pending -> cancel, back to listening")
    async with websockets.connect(URL) as ws:
        await hello(ws)
        await ws.send(json.dumps({"type": "ptt", "state": "down"}))
        await ev(ws)  # listening
        await _send_audio(ws)
        await ws.send(json.dumps({"type": "ptt", "state": "up"}))
        # wait for pending event
        for _ in range(3):
            kind, m = await ev(ws)
            if kind == "json" and m.get("type") == "turn" and m.get("state") == "pending":
                break
        await ws.send(json.dumps({"type": "barge_in"}))
        kind, m = await ev(ws)
        check(kind == "json" and m.get("type") == "turn" and m.get("state") == "listening",
              "barge_in -> listening")


async def test_pending_cancel():
    print("T3 pending cancel: user speaks during pending -> no reply dispatched")
    async with websockets.connect(URL) as ws:
        await hello(ws)
        await ws.send(json.dumps({"type": "ptt", "state": "down"}))
        await ev(ws)
        await _send_audio(ws)
        await ws.send(json.dumps({"type": "ptt", "state": "up"}))
        for _ in range(3):
            kind, m = await ev(ws)
            if kind == "json" and m.get("type") == "turn" and m.get("state") == "pending":
                break
        # user resumes talking mid-pending
        await ws.send(json.dumps({"type": "vad", "state": "speech_start"}))
        kind, m = await ev(ws)
        check(kind == "json" and m.get("type") == "turn" and m.get("state") == "listening",
              "speech during pending -> listening (pending cancelled)")
        # no processing/agent_text should arrive within 500ms
        try:
            kind2, m2 = await ev(ws, timeout=0.5)
            check(m2.get("type") not in ("agent_text",), "no reply dispatched after cancel")
        except asyncio.TimeoutError:
            check(True, "no reply dispatched after cancel (timeout = silence)")


async def test_wake_timeout():
    print("T4 wake timeout: wake with no speech -> wake_timeout/expiry (waits production 20s window)")
    async with websockets.connect(URL) as ws:
        await hello(ws)
        await ws.send(json.dumps({"type": "wake"}))
        kind, m = await ev(ws)
        check(kind == "json" and m.get("type") == "turn" and m.get("state") == "listening",
              "wake -> listening window")
        # no audio -> window expires back to idle
        got_timeout = False
        got_idle = False
        for _ in range(2):
            kind, m = await ev(ws, timeout=25)
            if kind == "json" and m.get("type") == "error" and m.get("code") == "wake_timeout":
                got_timeout = True
            if kind == "json" and m.get("type") == "turn" and m.get("state") == "idle":
                got_idle = True
        check(got_timeout and got_idle, "wake window expires with wake_timeout + idle")


async def test_error_matrix():
    print("T5 error matrix")
    async with websockets.connect(URL) as ws:
        # garbage json
        await ws.send("not json")
        kind, m = await ev(ws)
        check(kind == "json" and m.get("type") == "error", "garbage json -> error")
        # unknown type after hello
        await hello(ws)
        await ws.send(json.dumps({"type": "warp_drive"}))
        kind, m = await ev(ws)
        check(kind == "json" and m.get("type") == "error", "unknown type -> error")
        # wrong proto
        async with websockets.connect(URL) as ws2:
            await ws2.send(json.dumps({"type": "hello", "proto": "9.9"}))
            kind, m = await ev(ws2)
            check(kind == "json" and m.get("type") == "error", "bad proto -> error")


async def test_dictate():
    print("T6 dictate mode: audio -> final, no turn events")
    async with websockets.connect(URL) as ws:
        await hello(ws, mode="dictate")
        import opuslib
        enc = opuslib.Encoder(16000, 1, opuslib.APPLICATION_VOIP)
        silence = enc.encode(b"\x00\x00" * 320, 320)  # 20ms @ 16k
        await ws.send(json.dumps({"type": "audio", "seq": 0}))
        await ws.send(silence)
        await ws.send(json.dumps({"type": "vad", "state": "speech_end"}))
        kind, m = await ev(ws)
        check(kind == "json" and m.get("type") == "final", "dictate -> final text")
        try:
            kind2, m2 = await ev(ws, timeout=0.5)
            check(m2.get("type") != "turn", "dictate never emits turn events")
        except asyncio.TimeoutError:
            check(True, "dictate never emits turn events (timeout = silence)")


async def test_resume():
    print("T7 resume: bye then hello with same session_id")
    sid = ""
    async with websockets.connect(URL) as ws:
        m = await hello(ws, session_id="conformance-session-1")
        sid = m.get("session_id")
        await ws.send(json.dumps({"type": "bye"}))
        kind, m = await ev(ws)
        check(kind == "json" and m.get("type") == "bye_ack", "bye -> bye_ack")
    async with websockets.connect(URL) as ws:
        m = await hello(ws, session_id=sid)
        check(m.get("session_id") == sid, "resume keeps session_id")


async def main():
    tests = [test_happy_path, test_barge_in, test_pending_cancel,
             test_wake_timeout, test_error_matrix, test_dictate, test_resume]
    for t in tests:
        try:
            await t()
        except Exception as e:
            global FAIL
            FAIL += 1
            print(f"  FAIL {t.__name__} raised: {e}")
    print(f"\n=== conformance: {PASS} passed, {FAIL} failed ===")
    sys.exit(1 if FAIL else 0)


if __name__ == "__main__":
    asyncio.run(main())
