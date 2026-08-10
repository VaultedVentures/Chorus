"""Predictor unit checks — run from /opt/chorus: ./venv/bin/python -m tests.predictor"""

import sys, os
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from core.predictor import classify
from core.state import Complete

PASS = FAIL = 0

def check(cond, label):
    global PASS, FAIL
    if cond: PASS += 1; print(f"  ok  {label}")
    else: FAIL += 1; print(f"  FAIL {label}")

# complete utterances (final punctuation) -> LIKELY
check(classify("the build failed because of the tests.") is Complete.LIKELY, "sentence with period -> LIKELY")
check(classify("when does it finish?") is Complete.LIKELY, "question -> LIKELY")
check(classify("yes.") is Complete.LIKELY, "short answer with period -> LIKELY")

# trailing fragments -> THINKING
check(classify("the build failed because") is Complete.THINKING, "trailing 'because' -> THINKING")
check(classify("and then we should") is Complete.THINKING, "trailing 'should' -> THINKING")
check(classify("um actually i think") is Complete.THINKING, "filled pause tail -> THINKING")
check(classify("we need to") is Complete.THINKING, "trailing 'to' -> THINKING")

# fragments without punctuation -> UNCERTAIN (or LIKELY after question)
check(classify("the tests") is Complete.UNCERTAIN, "bare fragment -> UNCERTAIN")
check(classify("the tests", agent_asked_question=True) is Complete.LIKELY, "fragment after question -> LIKELY")

print(f"\n=== predictor: {PASS} passed, {FAIL} failed ===")
sys.exit(1 if FAIL else 0)
