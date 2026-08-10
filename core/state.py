"""CHORUS turn state machine — pure logic, no I/O.

The contract from docs/chorus-protocol-v1.md:
    IDLE -> LISTENING -> PENDING -> PROCESSING -> SPEAKING -> LISTENING

Rules:
- Clients mirror server events; they never compute transitions themselves.
- PENDING carries the Turn-Completion Predictor verdict + timeout_ms.
- Any user speech during PENDING or SPEAKING is a barge-in -> LISTENING.
- WAKE is an on-ramp into LISTENING with a speech window.
"""

from __future__ import annotations

from enum import Enum
from dataclasses import dataclass, field


class TurnState(str, Enum):
    IDLE = "idle"
    LISTENING = "listening"
    PENDING = "pending"
    PROCESSING = "processing"
    SPEAKING = "speaking"


class Complete(str, Enum):
    THINKING = "thinking"   # user still forming thoughts — long wait
    LIKELY = "likely"       # reply imminent — short ring
    UNCERTAIN = "uncertain"  # predictor unsure — medium wait


# Timeouts (ms) by verdict — the dynamic countdown from the design doc.
TIMEOUT_MS = {
    Complete.LIKELY: 1100,
    Complete.UNCERTAIN: 3000,
    Complete.THINKING: 8000,
}

WAKE_SPEECH_WINDOW_MS = 20_000


@dataclass
class TurnMachine:
    state: TurnState = TurnState.IDLE
    agent: str = "hermes"
    session_id: str = ""
    pending_verdict: Complete | None = None
    pending_timeout_ms: int = 0
    wake_deadline_ms: int = 0  # 0 = not in wake window
    seq: int = 0

    def open(self, session_id: str, agent: str = "hermes") -> None:
        """hello accepted."""
        self.session_id = session_id
        self.agent = agent
        self.state = TurnState.IDLE

    def start_speech(self, via: str = "ptt") -> None:
        """ptt down / wake / vad speech_start -> LISTENING."""
        if self.state in (TurnState.PENDING, TurnState.SPEAKING):
            # user talked over us — that's a barge-in, handled by caller
            return
        self.state = TurnState.LISTENING
        if via == "wake":
            self.wake_deadline_ms = WAKE_SPEECH_WINDOW_MS

    def end_speech(self, verdict: Complete = Complete.LIKELY,
                   timeout_ms: int | None = None) -> None:
        """vad speech_end / ptt up -> PENDING with predictor verdict."""
        self.state = TurnState.PENDING
        self.pending_verdict = verdict
        self.pending_timeout_ms = timeout_ms or TIMEOUT_MS[verdict]
        self.wake_deadline_ms = 0

    def process(self) -> None:
        """verdict: COMPLETE -> dispatch to agent."""
        self.state = TurnState.PROCESSING
        self.pending_verdict = None
        self.pending_timeout_ms = 0

    def speak(self) -> None:
        """agent reply streaming -> SPEAKING."""
        self.state = TurnState.SPEAKING

    def barge_in(self) -> None:
        """user speech during PENDING/SPEAKING -> back to LISTENING."""
        self.state = TurnState.LISTENING
        self.pending_verdict = None
        self.pending_timeout_ms = 0
        self.wake_deadline_ms = 0

    def cancel(self) -> None:
        """abort current turn -> IDLE."""
        self.state = TurnState.IDLE
        self.pending_verdict = None
        self.pending_timeout_ms = 0
        self.wake_deadline_ms = 0

    def wake_expired(self) -> bool:
        """True if wake window elapsed with no speech."""
        return self.state == TurnState.LISTENING and self.wake_deadline_ms > 0

    def event(self) -> dict:
        """Server->client turn event for the current state."""
        if self.state == TurnState.PENDING:
            return {
                "type": "turn",
                "state": "pending",
                "complete": self.pending_verdict.value,
                "timeout_ms": self.pending_timeout_ms,
            }
        return {"type": "turn", "state": self.state.value}
