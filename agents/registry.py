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

    async def reply(self, transcript: str, session_id: str = "") -> str:
        if not self.api_key:
            return (
                "The voice gateway is up, but I don't have a language model "
                "configured yet. Ask Scott to set KIMI_API_KEY."
            )
        import httpx
        async with httpx.AsyncClient(timeout=60) as client:
            r = await client.post(
                f"{self.base_url}/chat/completions",
                headers={"Authorization": f"Bearer {self.api_key}"},
                json={
                    "model": self.model,
                    "messages": [
                        {"role": "system", "content": self.system},
                        {"role": "user", "content": transcript},
                    ],
                    "temperature": 1,
                    "max_tokens": 300,
                },
            )
            r.raise_for_status()
            data = r.json()
            return data["choices"][0]["message"]["content"].strip()


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
