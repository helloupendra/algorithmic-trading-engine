"""
The FYERS live feed, by its old name.

Everything it did is now split in two: the FYERS adapter in
market_data/live/vendors/fyers.py, and the runner every vendor shares in
core/live/feed_runner.py. This file stays so anything that launches it by path
keeps working; it simply runs the shared entry point for FYERS.
"""

import os
import sys

sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..")))

from market_data.live.run_feed import main  # noqa: E402

if __name__ == "__main__":
    main(["--vendor", "fyers"])
