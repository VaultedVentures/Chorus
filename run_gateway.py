"""CHORUS gateway runner — run from /opt/chorus:
    CHORUS_MOCK=1 ./venv/bin/python run_gateway.py
    ./venv/bin/python run_gateway.py          # real engines
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from core.gateway import main

if __name__ == "__main__":
    main()
