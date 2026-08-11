"""CHORUS agent adapters — plain text in, plain text out. Audio-agnostic.

Per the design: agents plug in as text adapters; the gateway owns sound.
v1 ships a Hermes adapter that calls the configured LLM (Kimi/Moonshot by
default, matching the Vaulted stack) with the transcript as the prompt.
Any future system (LifeOS backend, VOIS, VibeStation, Home Assistant later)
registers the same way: {id, name, voice, handler}.
"""

from __future__ import annotations

import asyncio
import os
from dataclasses import dataclass, field


@dataclass
class Agent:
    agent_id: str
    display_name: str
    voice: str
    handler: object


class HermesAdapter:
    """The assistant — an LLM behind a plain text interface."""

    def __init__(self):
        self.api_key = os.environ.get("KIMI_API_KEY", "")
        self.base_url = os.environ.get("KIMI_BASE_URL", "https://api.moonshot.cn/v1")
        self.model = os.environ.get("KIMI_MODEL", "kimi-k2.7-code")
        self.system = (
            "You are Hermes, the Vaulted Ventures assistant speaking through "
            "the CHORUS voice layer. Answer conversationally but concisely. "
            "The user is speaking to you; reply as you would in a voice call."
        )
        # Keep the last N turns of context when talking to the LLM.
        self.max_history_turns = 12

    async def _chat(self, messages: list[dict], max_tokens: int = 300) -> str:
        import httpx
        async with httpx.AsyncClient(timeout=60) as client:
            r = await client.post(
                f"{self.base_url}/chat/completions",
                headers={"Authorization": f"Bearer {self.api_key}"},
                json={
                    "model": self.model,
                    "messages": messages,
                    "temperature": 1,
                    "max_tokens": max_tokens,
                },
            )
            r.raise_for_status()
            data = r.json()
            return data["choices"][0]["message"]["content"].strip()

    def _with_history(self, transcript: str, history: list[dict] | None) -> list[dict]:
        """System + prior turns + current user message (windowed)."""
        messages = [{"role": "system", "content": self.system}]
        if history:
            for turn in history[-self.max_history_turns:]:
                role = "assistant" if turn.get("role") == "agent" else "user"
                messages.append({"role": role, "content": turn.get("text", "")})
        messages.append({"role": "user", "content": transcript})
        return messages

    async def correct_transcript(self, raw: str, history: list[dict] | None = None) -> str:
        """Interpretation layer: fix STT misrecognition using context.

        The raw speech-to-text can mangle words (especially with an accent —
        "phryzology" for "phraseology"). Ask the LLM to reconstruct the
        user's intended words from the conversation context. Returns the
        corrected text (unchanged if it was already clean).
        """
        if not self.api_key or not raw.strip():
            return raw
        sys_msg = (
            "You are a speech-recognition cleanup pass for a voice assistant. "
            "The user speaks with an Australian accent and the STT layer "
            "sometimes mangles words into near-homophones. Reconstruct what "
            "the user most likely INTENDED to say, using the conversation "
            "context. Fix garbled words, homophones, and dropped/inserted "
            "small words. Keep the meaning and phrasing as close to the "
            "original as possible. Reply with ONLY the corrected text — no "
            "quotes, no commentary, no prefix.\n\n"
            "EXAMPLE:\n"
            "RAW: playing off my phryzology is getting mangled\n"
            "CORRECTED: a lot of my phraseology is getting mangled"
        )
        messages = [{"role": "system", "content": sys_msg}]
        if history:
            for turn in history[-8:]:
                role = "assistant" if turn.get("role") == "agent" else "user"
                messages.append({"role": role, "content": turn.get("text", "")})
        messages.append({"role": "user", "content": f"RAW TRANSCRIPT: {raw}"})
        try:
            fixed = await self._chat(messages, max_tokens=600)
            return fixed if fixed else raw  # empty content (reasoning ate the budget) -> keep raw
        except Exception:
            return raw  # never fail the turn on the cleanup pass

    async def reply(self, transcript: str, session_id: str = "",
                    history: list[dict] | None = None) -> str:
        if not self.api_key:
            return (
                "The voice gateway is up, but I don't have a language model "
                "configured yet. Ask Scott to set KIMI_API_KEY."
            )
        return await self._chat(self._with_history(transcript, history))


class EchoAdapter:
    """Deterministic adapter for the conformance suite — echoes the transcript."""

    async def reply(self, transcript: str, session_id: str = "") -> str:
        await asyncio.sleep(0.01)
        return f"echo: {transcript}"


@dataclass
class AgentRegistry:
    agents: dict = field(default_factory=dict)

    @classmethod
    def build(cls, use_mock: bool = False) -> "AgentRegistry":
        r = cls()
        if use_mock:
            r.register(Agent("hermes", "Hermes", "en_US-lessac-medium", EchoAdapter()))
        else:
            r.register(Agent("hermes", "Hermes", "en_US-lessac-medium", HermesAdapter()))
            # Later phases: Stan, Loretta, Woger, Home Assistant — same shape.
        return r

    def register(self, agent: Agent) -> None:
        self.agents[agent.agent_id] = agent

    def get(self, agent_id: str) -> Agent:
        return self.agents.get(agent_id)

    def roster(self) -> list[dict]:
        return [
            {"id": a.agent_id, "display_name": a.display_name, "voice": a.voice}
            for a in self.agents.values()
        ]
