"""
tests/_bootstrap.py

Puts the engine directory on sys.path so the tests import the packages the
same way the runners do (`core.*`, `strategies.*`, `backtest.*`). Import it
first in every test module:

    import _bootstrap  # noqa: F401
"""

import os
import sys

ENGINE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)
