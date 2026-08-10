"""CHORUS wake-word reference DSP — the CONTRACT both sides implement.

This module is the Python side of the cross-language contract:
  - tests/wakeword_templates.py  (generates the packaged .mfc templates)
  - tests/wakeword_pipeline_check.py (end-to-end detection + latency check)
  - client/src/Chorus.Core/WakeWord/ (the C# runtime implementation)

The C# MFCC + online-DTW pipeline MUST match this module exactly
(parameters, window, filterbank normalization, DCT scaling, L2 norm,
recurrence and trigger rule). The contract is enforced in the C# unit
tests: the MFCC of the canonical PCM must match the fixture dumped here,
and the packaged templates must be detected on the canonical PCM.

Everything is 16 kHz mono 16-bit PCM, the mic pipeline's format.
"""

from __future__ import annotations

import math
import struct
from collections import deque
from dataclasses import dataclass
from pathlib import Path

import numpy as np

# ---------------------------------------------------------------------------
# Fixed parameters (shared with the C# side — do not change one side only)
# ---------------------------------------------------------------------------

FS = 16000
N_FFT = 512
WIN_LEN = 400        # 25 ms
HOP = 160            # 10 ms
N_MELS = 26
FMIN = 300.0
FMAX = 8000.0        # Nyquist at 16 kHz
N_MFCC = 13          # c1..c13 (c0 energy coefficient dropped)
MFCC_DIM = N_MFCC

# Energy gate: hops quieter than this RMS are not match-checked (cheap
# false-positive suppression + CPU saving; DTW cost stays high anyway).
ENERGY_FLOOR = 150.0

# A hop louder than this counts as audible for onset detection. Kept well
# below ENERGY_FLOOR so the very start of a phrase counts — the onset reset
# must not amputate the phrase's first frames.
ONSET_FLOOR = 60.0

# A new utterance starts only after this many consecutive quiet hops
# (25 x 10 ms = 250 ms). Shorter dips — e.g. the natural pause between
# "hey" and "chorus" — stay inside the current alignment.
MIN_SILENCE_HOPS = 25

# Freshness window: a match is only accepted if the best alignment ends
# within the last FRESHNESS_HOPS hops (bounds trigger latency after the
# phrase ends: 40 hops x 10 ms = 400 ms <= 500 ms acceptance).
FRESHNESS_HOPS = 40

# Sakoe-Chiba band for the online DTW (hops either side of the diagonal).
# Constrains rate variation to ~band*10ms so a PARTIAL phrase cannot stretch
# to fit a whole template (the mid-utterance false-trigger fix). 15 hops =
# phrases up to ~150 ms shorter/longer than the template still match.
DTW_BAND = 15

# DTW threshold mapping: sensitivity s in [0,1] -> threshold = LOW + s*(HIGH-LOW).
# Calibrated on synthesized utterances (see tests/wakeword_calibrate2.py):
#   s=0 (least sensitive) -> 0.30  same-voice, clear speech only
#   s=1 (most sensitive)  -> 0.40  every measured true positive, some near-misses
#   s=0.5 (default)       -> 0.35  all true <=0.281, near-misses >=0.356
THRESH_LOW = 0.30
THRESH_HIGH = 0.40


def hz_to_mel(f: float) -> float:
    return 2595.0 * math.log10(1.0 + f / 700.0)


def mel_to_hz(m: float) -> float:
    return 700.0 * (10.0 ** (m / 2595.0) - 1.0)


def sensitivity_to_threshold(sensitivity: float) -> float:
    s = float(np.clip(sensitivity, 0.0, 1.0))
    return THRESH_LOW + s * (THRESH_HIGH - THRESH_LOW)


def mel_filterbank() -> np.ndarray:
    """(N_MELS, N_FFT//2+1) triangular filters on the mel scale, sum-normalized.

    Each row sums to 1. Both languages compute this from the same formula.
    """
    n_freqs = N_FFT // 2 + 1
    fft_freqs = np.linspace(0.0, FS / 2.0, n_freqs)
    mel_pts = np.linspace(hz_to_mel(FMIN), hz_to_mel(FMAX), N_MELS + 2)
    hz_pts = [mel_to_hz(m) for m in mel_pts]
    fb = np.zeros((N_MELS, n_freqs))
    for i in range(N_MELS):
        lo, mid, hi = hz_pts[i], hz_pts[i + 1], hz_pts[i + 2]
        ramp_up = (fft_freqs - lo) / (mid - lo)
        ramp_dn = (hi - fft_freqs) / (hi - mid)
        fb[i] = np.maximum(0.0, np.minimum(ramp_up, ramp_dn))
    row_sums = fb.sum(axis=1, keepdims=True)
    row_sums[row_sums == 0] = 1.0
    fb /= row_sums
    return fb


_HAMMING = np.hamming(WIN_LEN)
_FILTERBANK = mel_filterbank()


def hamming_window() -> np.ndarray:
    return _HAMMING


def filterbank() -> np.ndarray:
    return _FILTERBANK


def dct_ortho(x: np.ndarray) -> np.ndarray:
    """Orthonormal DCT-II (scipy dct type 2, norm='ortho')."""
    n = len(x)
    k = np.arange(n)[:, None]
    basis = np.cos(np.pi * k * (2 * np.arange(n)[None, :] + 1) / (2 * n))
    out = basis @ x
    out[0] *= math.sqrt(1.0 / n)
    out[1:] *= math.sqrt(2.0 / n)
    return out


def mfcc_frame(frame: np.ndarray) -> np.ndarray:
    """One 400-sample window -> (MFCC_DIM,) L2-normalized c1..c13 vector."""
    assert len(frame) == WIN_LEN, f"window must be {WIN_LEN} samples"
    x = frame.astype(np.float64) * _HAMMING
    spec = np.fft.rfft(x, N_FFT)
    power = np.abs(spec) ** 2
    mel = _FILTERBANK @ power
    logmel = np.log(mel + 1e-10)
    c = dct_ortho(logmel)
    mfcc = c[1 : 1 + N_MFCC]
    n = np.linalg.norm(mfcc)
    if n > 1e-8:
        mfcc = mfcc / n
    return mfcc


def mfcc_stream(pcm: np.ndarray) -> np.ndarray:
    """16k int16 PCM -> (hops, MFCC_DIM) matrix."""
    pcm = pcm.astype(np.float64)
    n = len(pcm)
    n_hops = max(0, (n - WIN_LEN) // HOP + 1)
    if n_hops == 0:
        return np.zeros((0, MFCC_DIM))
    frames = np.stack(
        [pcm[i * HOP : i * HOP + WIN_LEN] for i in range(n_hops)]
    )
    return np.stack([mfcc_frame(f) for f in frames])


def hop_rms(pcm: np.ndarray) -> np.ndarray:
    """RMS per hop window (same windows as mfcc_stream)."""
    n = len(pcm)
    n_hops = max(0, (n - WIN_LEN) // HOP + 1)
    if n_hops == 0:
        return np.zeros(0)
    frames = np.stack(
        [pcm[i * HOP : i * HOP + WIN_LEN].astype(np.float64) for i in range(n_hops)]
    )
    return np.sqrt(np.mean(frames**2, axis=1))


# ---------------------------------------------------------------------------
# Online DTW (the runtime matching engine)
# ---------------------------------------------------------------------------


def dtw_distance(a: np.ndarray, b: np.ndarray) -> float:
    """Euclidean distance between two L2-normalized MFCC vectors (0..2)."""
    return float(np.linalg.norm(a - b))


class OnlineDtw:
    """Incremental banded DTW of one template against a stream.

    One call to `step(x)` per hop. `end_cost()` is the cost of the best
    alignment of the full template ENDING at the current hop, normalized by
    template length — +inf when the current hop is outside the Sakoe-Chiba
    band (the template cannot have finished yet, or the rate difference is
    implausible). The band is what stops a PARTIAL phrase from stretching to
    fit the whole template and false-triggering mid-utterance.

    Band = DTW_BAND hops either side of the diagonal (i == j): a phrase may
    be spoken up to ~band*10ms shorter/longer than the template.
    """

    def __init__(self, template: np.ndarray, band: int = DTW_BAND):
        self.template = template
        self.n = len(template)
        self.band = band
        self._prev: np.ndarray | None = None  # previous column
        self._j = -1
        self._end_cost = float("inf")

    def reset(self) -> None:
        self._prev = None
        self._j = -1
        self._end_cost = float("inf")

    def step(self, x: np.ndarray) -> float:
        """Advance one hop with stream vector x; returns normalized end cost."""
        d = np.array([dtw_distance(row, x) for row in self.template])
        j = self._j + 1
        lo = max(0, j - self.band)
        hi = min(self.n - 1, j + self.band)

        if lo > hi:
            # the template can no longer end within the band — the phrase, if
            # it was said, is long past. Restart fresh so a LATER occurrence
            # can still match (the all-inf columns are equivalent anyway).
            self.reset()
            return self.step(x)

        cur = np.full(self.n, float("inf"))
        if self._prev is None:
            # first stream column: only i==0 (within band) is reachable
            cur[0] = d[0]
            for i in range(1, hi + 1):
                cur[i] = d[i] + cur[i - 1]
        else:
            prev = self._prev
            for i in range(lo, hi + 1):
                best = float("inf")
                if i > 0 and prev[i - 1] < best:  # both advance
                    best = prev[i - 1]
                if prev[i] < best:                # stream waits (slow speech)
                    best = prev[i]
                if i > 0 and cur[i - 1] < best:   # template waits (fast speech)
                    best = cur[i - 1]
                cur[i] = d[i] + best

        self._prev = cur
        self._j = j
        # full template can only have ended if its last row is in-band
        self._end_cost = cur[self.n - 1] / self.n if abs((self.n - 1) - j) <= self.band else float("inf")
        return self._end_cost

    @property
    def hops(self) -> int:
        return self._j + 1


@dataclass
class Trigger:
    score: float          # normalized DTW end cost at trigger
    hop: int              # hop index where the match ended
    template_index: int   # which template matched
    latency_ms: int       # ms between phrase end and trigger (pipeline check)


class WakeWordSpotter:
    """Multi-template online-DTW spotter: feed 16k PCM, get triggers.

    Mirrors Chorus.Core.WakeWord.WakeWordEngine exactly.
    """

    def __init__(
        self,
        templates: list[np.ndarray],
        sensitivity: float = 0.5,
        cooldown_ms: int = 2000,
        freshness_hops: int = FRESHNESS_HOPS,
        energy_floor: float = ENERGY_FLOOR,
        muted: bool = False,
        band: int = DTW_BAND,
    ):
        self.templates = templates
        self.sensitivity = sensitivity
        self.cooldown_ms = cooldown_ms
        self.freshness_hops = freshness_hops
        self.energy_floor = energy_floor
        self.muted = muted
        self.band = band
        self.threshold = sensitivity_to_threshold(sensitivity)
        self._matchers = [OnlineDtw(t, band) for t in templates]
        self._samples = np.zeros(0, dtype=np.int16)
        self._recent: deque[tuple[int, float]] = deque()  # (stream hop, min end-cost)
        self._last_trigger_ms = -10**9
        self._silent_hops = 0
        self._stream_hop = 0  # stream hop counter — NEVER reset (cooldown/freshness use stream time)

    def reset(self) -> None:
        for m in self._matchers:
            m.reset()
        self._samples = np.zeros(0, dtype=np.int16)
        self._recent.clear()
        self._silent_hops = 0
        # _stream_hop deliberately NOT reset: cooldown and the freshness
        # window are measured on stream time, not alignment time

    def feed(self, pcm: np.ndarray) -> list[Trigger]:
        """Feed a chunk of 16k int16 PCM; returns triggers fired in this chunk.

        `pcm` may be any length; internal buffering aligns to the 10 ms hop.
        """
        triggers: list[Trigger] = []
        if len(pcm) == 0:
            return triggers
        self._samples = np.concatenate([self._samples, pcm.astype(np.int16)])

        # Process one hop at a time from the buffer start; the hop window
        # slides by HOP samples per iteration.
        while len(self._samples) >= WIN_LEN:
            frame = self._samples[:WIN_LEN]
            self._samples = self._samples[HOP:]
            triggers.extend(self._step(frame))
        return triggers

    def _step(self, frame: np.ndarray) -> list[Trigger]:
        x = mfcc_frame(frame)
        rms = float(np.sqrt(np.mean(frame.astype(np.float64) ** 2)))
        hop_ms = HOP * 1000 // FS
        hop = self._stream_hop
        out: list[Trigger] = []

        voiced = rms >= self.energy_floor
        if rms >= ONSET_FLOOR:
            if self._silent_hops >= MIN_SILENCE_HOPS:
                # a new utterance started after a real pause: restart the
                # alignments so the DTW never absorbs leading silence — but
                # only after >=250 ms of quiet, so the natural inter-word
                # pause inside "hey chorus" does NOT split the phrase
                for m in self._matchers:
                    m.reset()
                self._recent.clear()
            self._silent_hops = 0
        else:
            self._silent_hops += 1

        if self.muted or not voiced:
            # advance the alignment regardless (keeps state continuous);
            # no match-checking while muted or in near-silence
            for m in self._matchers:
                m.step(x)
            self._recent.append((hop, float("inf")))
        else:
            best = float("inf")
            for m in self._matchers:
                c = m.step(x)
                if c < best:
                    best = c
            self._recent.append((hop, best))

        while self._recent and self._recent[0][0] <= hop - self.freshness_hops:
            self._recent.popleft()

        # earliest hop in the freshness window whose end-cost is below the
        # sensitivity threshold = the moment the phrase finished matching
        for h, c in self._recent:
            if c < self.threshold:
                now_ms = hop * hop_ms
                if now_ms - self._last_trigger_ms >= self.cooldown_ms:
                    self._last_trigger_ms = now_ms
                    out.append(
                        Trigger(score=c, hop=h, template_index=-1, latency_ms=0)
                    )
                # whether fired or cooldown-suppressed, the phrase was said —
                # consume it so the NEXT phrase can match fresh
                self.reset()
                break
        self._stream_hop += 1
        return out


# ---------------------------------------------------------------------------
# Template file format (.mfc)
# ---------------------------------------------------------------------------

MFC_MAGIC = b"CHMFC010"  # 8 bytes, matches loader's f.read(8)


def save_template(path: str, mfcc: np.ndarray) -> None:
    """Write (hops, MFCC_DIM) float64 matrix as the compact .mfc format.

    Layout: 8-byte magic, int32 dim, int32 frames, then frames*dim float32 LE.
    """
    mfcc = mfcc.astype(np.float32)
    frames, dim = mfcc.shape
    with open(path, "wb") as f:
        f.write(MFC_MAGIC)
        f.write(struct.pack("<ii", dim, frames))
        f.write(mfcc.tobytes())


def load_template(path: Path) -> np.ndarray:
    with open(path, "rb") as f:
        magic = f.read(8)
        assert magic == MFC_MAGIC, f"bad template magic {magic!r}"
        dim, frames = struct.unpack("<ii", f.read(8))
        data = np.frombuffer(f.read(), dtype=np.float32)
        return data.reshape(frames, dim).astype(np.float64)


def resample_to_16k(pcm: np.ndarray, src_rate: int) -> np.ndarray:
    """Linear-interpolation resample (piper 22050 -> mic 16000)."""
    if src_rate == FS:
        return pcm.astype(np.int16)
    n_out = int(round(len(pcm) * FS / src_rate))
    x_old = np.linspace(0.0, 1.0, len(pcm), endpoint=False)
    x_new = np.linspace(0.0, 1.0, n_out, endpoint=False)
    return np.interp(x_new, x_old, pcm.astype(np.float64)).astype(np.int16)


def trim_silence(pcm: np.ndarray, floor: float = 200.0) -> np.ndarray:
    """Trim leading/trailing near-silence (RMS below floor) from 16k PCM."""
    rms = hop_rms(pcm)
    voiced = np.where(rms >= floor)[0]
    if len(voiced) == 0:
        return pcm
    start = max(0, voiced[0] * HOP - WIN_LEN // 2)
    end = min(len(pcm), (voiced[-1] + 1) * HOP + WIN_LEN // 2)
    return pcm[start:end]
