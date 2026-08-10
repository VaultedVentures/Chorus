"""CHORUS Turn-Completion Predictor v1 — lexical heuristic.

Decides done-vs-thinking on end-of-speech, per the design doc §3.6.
v1 = lexical only (from the final/partial transcript). Prosodic signals and
the LLM judge are later phases; the interface is shaped so they slot in.

Verdicts:
  LIKELY     — sentence-final punctuation / complete clause; reply soon
  THINKING   — trailing fragment, article, conjunction, filled pause,
               restart marker; wait long
  UNCERTAIN  — ambiguous; medium wait, safety net at cap
"""

from __future__ import annotations

import re

from .state import Complete

# trailing words that strongly indicate continuation ("…the", "…and then", "…because")
_TRAILING_WORDS = {
    "the", "a", "an", "and", "but", "or", "so", "because", "if", "when",
    "while", "although", "though", "since", "until", "after", "before",
    "of", "to", "for", "with", "without", "at", "on", "in", "by", "from",
    "as", "than", "that", "which", "who", "whom", "whose", "what", "whether",
    "is", "was", "were", "are", "am", "be", "been", "being", "will", "would",
    "can", "could", "shall", "should", "may", "might", "must", "do", "does",
    "did", "have", "has", "had", "just", "about", "going", "want", "wanna",
    "need", "gonna", "got", "let", "please", "also", "then", "still", "yeah",
    "yes", "no", "ok", "okay", "well", "um", "uh", "hmm", "like", "actually",
    "wait", "sorry", "but", "so",
}

_FILLED_PAUSES = {"um", "uh", "hmm", "er", "ah", "like", "you", "know"}

# sentence-final punctuation that marks a complete utterance
_FINAL_PUNCT = {".", "!", "?", "…"}


def _normalize(text: str) -> str:
    return re.sub(r"\s+", " ", text).strip().lower()


def _last_word(text: str) -> str:
    words = re.findall(r"[a-z']+", _normalize(text))
    return words[-1] if words else ""


def _strips_final_punct(text: str) -> bool:
    return text.strip().endswith(tuple(_FINAL_PUNCT))


def _is_fragment(text: str) -> bool:
    """Short utterance with no verb — likely complete only after a question."""
    words = re.findall(r"[a-z']+", _normalize(text))
    if not words:
        return True
    verbs = {"is", "are", "was", "were", "am", "do", "does", "did", "have",
             "has", "had", "will", "would", "can", "could", "should", "shall",
             "be", "been", "being", "need", "want", "go", "going", "get",
             "make", "take", "put", "see", "look", "know", "think", "say"}
    return not any(w in verbs for w in words)


def classify(text: str, *, agent_asked_question: bool = False) -> Complete:
    """v1 lexical verdict for the completed utterance text."""
    t = text.strip()
    if not t:
        return Complete.THINKING  # silence with nothing — user gathering thoughts

    if _strips_final_punct(t):
        # "yes." / "no." / "done." are complete even as fragments
        return Complete.LIKELY

    lw = _last_word(t)
    if lw in _TRAILING_WORDS:
        return Complete.THINKING

    # filled pauses / restarts anywhere in the tail
    tail = _normalize(t)[-40:]
    for fp in _FILLED_PAUSES:
        if fp in tail:
            return Complete.THINKING

    if _is_fragment(t):
        # "Tuesday" after "when is the meeting?" is complete; mid-task list is not
        return Complete.LIKELY if agent_asked_question else Complete.UNCERTAIN

    # a full clause without final punctuation: probably done, but not certain
    return Complete.UNCERTAIN
