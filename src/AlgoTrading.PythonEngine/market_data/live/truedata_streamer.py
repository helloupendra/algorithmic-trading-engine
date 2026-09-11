"""
The TrueData live feed, by its earlier name. Runs the shared entry point for
TrueData; the adapter is market_data/live/vendors/truedata.py.
"""

import os
import sys

sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..")))

from market_data.live.run_feed import main  # noqa: E402

if __name__ == "__main__":
    main(["--vendor", "truedata"])
