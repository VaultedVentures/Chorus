"""CHORUS speech engine registry — STT and TTS providers.

Data-driven registry (per the design: soft config, not code branches).
v1 ships: faster-whisper (STT), Piper (TTS). Cloud engines slot in later
as additional registry entries with the same interface.
"""

from __future__ import annotations

import asyncio
import io
import os
import tempfile
from dataclasses import dataclass, field


# ---------------------------------------------------------------------------
# STT
# ---------------------------------------------------------------------------

class SttProvider:
    name = "base"

    async def transcribe(self, pcm16: bytes, sample_rate: int = 16000) -> str:
        raise NotImplementedError


class MockStt(SttProvider):
    """Echo provider for the conformance suite — no model, instant, deterministic."""
    name = "mock"

    async def transcribe(self, pcm16: bytes, sample_rate: int = 16000) -> str:
        await asyncio.sleep(0.01)
        return os.environ.get("CHORUS_MOCK_STT_TEXT", "hello from the mock speech engine")


class FasterWhisperStt(SttProvider):
    """Self-hosted faster-whisper (int8) — the v1 default."""
    name = "faster-whisper"

    def __init__(self, model_size: str = "base", device: str = "cpu",
                 compute_type: str = "int8"):
        self.model_size = model_size
        self.device = device
        self.compute_type = compute_type
        self._model = None

    def _load(self):
        if self._model is None:
            from faster_whisper import WhisperModel
            self._model = WhisperModel(
                self.model_size, device=self.device, compute_type=self.compute_type
            )

    async def transcribe(self, pcm16: bytes, sample_rate: int = 16000) -> str:
        # faster-whisper is synchronous; run in a thread so the event loop stays free
        return await asyncio.to_thread(self._transcribe_sync, pcm16, sample_rate)

    def _transcribe_sync(self, pcm16: bytes, sample_rate: int) -> str:
        self._load()
        import numpy as np
        audio = np.frombuffer(pcm16, dtype=np.int16).astype(np.float32) / 32768.0
        segments, _ = self._model.transcribe(audio, beam_size=1, language="en")
        return "".join(seg.text for seg in segments).strip()


# ---------------------------------------------------------------------------
# TTS
# ---------------------------------------------------------------------------

class TtsProvider:
    name = "base"

    async def synthesize(self, text: str, voice: str = "en_US-lessac-medium",
                         sample_rate: int = 24000) -> bytes:
        """Return raw PCM16 at sample_rate."""
        raise NotImplementedError


class MockTts(TtsProvider):
    """Silence provider for the conformance suite — emits silence frames."""
    name = "mock"

    async def synthesize(self, text: str, voice: str = "mock",
                         sample_rate: int = 24000) -> bytes:
        await asyncio.sleep(0.01)
        # ~0.2s of silence at 24 kHz
        return b"\x00\x00" * int(sample_rate * 0.2)


class PiperTts(TtsProvider):
    """Self-hosted Piper — the v1 default. Lazy voice download + model load."""
    name = "piper"

    def __init__(self, voices_dir: str = "/opt/chorus/voices"):
        self.voices_dir = voices_dir
        os.makedirs(voices_dir, exist_ok=True)
        self._voice_cache: dict[str, object] = {}

    def _resolve_voice(self, voice: str) -> tuple[str, str]:
        """Return (onnx_path, json_path) for a voice name; download if missing."""
        safe = voice.replace("/", "_")
        onnx = os.path.join(self.voices_dir, safe + ".onnx")
        jsn = os.path.join(self.voices_dir, safe + ".onnx.json")
        if os.path.exists(onnx):
            return onnx, jsn
        url = f"https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/{voice}/{voice}.onnx"
        import urllib.request
        print(f"[piper] downloading {voice} ...")
        urllib.request.urlretrieve(url, onnx)
        urllib.request.urlretrieve(url + ".json", jsn)
        return onnx, jsn

    def _voice(self, voice: str):
        if voice not in self._voice_cache:
            from piper import PiperVoice
            onnx, jsn = self._resolve_voice(voice)
            self._voice_cache[voice] = PiperVoice.load(onnx, config_path=jsn)
        return self._voice_cache[voice]

    async def synthesize(self, text: str, voice: str = "en_US-lessac-medium",
                         sample_rate: int = 24000) -> bytes:
        return await asyncio.to_thread(self._synth_sync, text, voice, sample_rate)

    def _synth_sync(self, text: str, voice: str, sample_rate: int) -> bytes:
        import numpy as np
        v = self._voice(voice)
        # piper-tts 1.6 yields AudioChunk objects; audio_int16_bytes is PCM16
        chunks = [chunk.audio_int16_bytes for chunk in v.synthesize(text)]
        pcm = b"".join(chunks)
        if sample_rate != v.config.sample_rate:
            # simple resample via numpy (linear) — good enough for v1
            arr = np.frombuffer(pcm, dtype=np.int16)
            ratio = sample_rate / v.config.sample_rate
            new_len = int(len(arr) * ratio)
            idx = (np.arange(new_len) / ratio).astype(int)
            idx = np.clip(idx, 0, len(arr) - 1)
            pcm = arr[idx].astype(np.int16).tobytes()
        return pcm


# ---------------------------------------------------------------------------
# Registry
# ---------------------------------------------------------------------------

@dataclass
class SpeechRegistry:
    stt: dict = field(default_factory=dict)
    tts: dict = field(default_factory=dict)
    _default_stt: str = "faster-whisper"
    _default_tts: str = "piper"

    @classmethod
    def build(cls, use_mock: bool = False) -> "SpeechRegistry":
        r = cls()
        if use_mock:
            r.stt["mock"] = MockStt()
            r.tts["mock"] = MockTts()
            r._default_stt = "mock"
            r._default_tts = "mock"
        else:
            r.stt["faster-whisper"] = FasterWhisperStt(model_size="base")
            r.tts["piper"] = PiperTts()
        return r

    def stt_provider(self, name: str | None = None) -> SttProvider:
        return self.stt.get(name or self._default_stt) or next(iter(self.stt.values()))

    def tts_provider(self, name: str | None = None) -> TtsProvider:
        return self.tts.get(name or self._default_tts) or next(iter(self.tts.values()))
