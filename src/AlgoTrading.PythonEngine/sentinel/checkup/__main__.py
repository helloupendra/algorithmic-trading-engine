"""
Run a checkup now and print it. Stores nothing and sends nothing: the service
does that on its schedule, and the console's "Run a checkup now" asks it to.

    python -m sentinel.checkup                  the checks that mean something at any hour
    python -m sentinel.checkup --slot morning   one scheduled checkup's checks (morning, close, night, weekly)
"""
from __future__ import annotations

import argparse
import logging
import sys

from sentinel.__main__ import load_env, make_context
from sentinel.checkup.agent import CheckupAgent
from sentinel.checkup.report import format_full
from sentinel.checkup.slots import BY_NAME, ON_REQUEST
from sentinel.checkup.store import MemoryCheckupStore
from sentinel.notify import LogNotifier


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="sentinel.checkup", description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--slot", default=ON_REQUEST.name, choices=sorted(BY_NAME))
    args = parser.parse_args(argv)
    logging.basicConfig(level=logging.WARNING, format="%(levelname)s %(name)s: %(message)s")

    ctx = make_context(load_env(), dry_run=True)
    slot = BY_NAME[args.slot]
    agent = CheckupAgent(store=MemoryCheckupStore(), notifier=LogNotifier())
    agent._wire(ctx)   # the database reads, from the same .env the service uses
    print(format_full(agent.run(ctx, slot, checkup_id=None), slot))
    return 0


if __name__ == "__main__":
    sys.exit(main())
