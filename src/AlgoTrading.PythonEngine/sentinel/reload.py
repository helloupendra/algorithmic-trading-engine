"""
Noticing that Sentinel's own code has changed under it.

The desk deploys from GitHub by itself (scripts/desk.sh pulls within about 24
seconds of a push), and a Python process keeps running the code it imported
at start. A watchman that kept last week's rules after a fix was deployed
would be this desk's old trap in a new place: on 6 Sep a whole debugging pass
went into why the chain poller wrote no PriceChange, and the poller had simply
been started 88 seconds before the edit that added it.

So the watch loop asks, about once a minute, whether any ``sentinel/**/*.py``
is newer than the newest one it started with, and if so ends cleanly between
rounds; systemd starts it again on the new code (scripts/install-sentinel.sh,
Restart=always). The morning plan needs none of this:
config/morning-plan.txt is read afresh on every check.

The repository's ``.env`` counts too. It is read once, at start: the database
password, the admin sign-in, the Telegram bot token. Rotate one of them and a
Sentinel that kept the old one would fail every write and every message — the
silent watchman again. An edit to .env (any change of its modification time)
ends the process the same way, and the restart reads the new values.

Only its own files and .env, not every commit. Sentinel imports nothing from
the repository outside its package, so no other change can alter what it
runs. And a deploy is the moment it is most likely to have something to say
(the API restarts, a feed reconnects); a watchman that restarted on every pull
would blink for a minute exactly then.
"""
from __future__ import annotations

import time
from pathlib import Path
from typing import Callable, Optional

#: How often the files are looked at: a stat of two dozen files.
CHECK_EVERY_SECONDS = 60.0


def newest_source(package_dir: Path) -> Optional[float]:
    """The newest modification time among the package's .py files; None when none can be read."""
    newest: Optional[float] = None
    try:
        paths = list(package_dir.rglob("*.py"))
    except OSError:
        return None
    for path in paths:
        try:
            mtime = path.stat().st_mtime
        except OSError:
            continue  # removed between the listing and the stat: the next look sees the rest
        if newest is None or mtime > newest:
            newest = mtime
    return newest


def _mtime(path: Path) -> Optional[float]:
    try:
        return path.stat().st_mtime
    except OSError:
        return None


class CodeWatch:
    """
    Whether the code, or the .env, has changed since this process started.
    The baselines are the file times seen at construction — compared with
    themselves, not with the clock, so a file stamped in the future (a clock
    that moved) does not restart Sentinel in a loop.
    """

    def __init__(self, package_dir: Path,
                 monotonic: Callable[[], float] = time.monotonic, every: float = CHECK_EVERY_SECONDS,
                 newest: Optional[Callable[[], Optional[float]]] = None,
                 env_file: Optional[Path] = None,
                 env_mtime: Optional[Callable[[], Optional[float]]] = None) -> None:
        self._newest = newest or (lambda: newest_source(package_dir))
        if env_mtime is None:
            env_mtime = (lambda: _mtime(env_file)) if env_file is not None else (lambda: None)
        self._env_mtime = env_mtime
        self._monotonic = monotonic
        self._every = every
        self._started_newest = self._newest()
        self._started_env = self._env_mtime()
        self._next = monotonic() + every

    def changed(self) -> Optional[str]:
        """What changed, when it is time to look and something has; None otherwise."""
        now = self._monotonic()
        if now < self._next:
            return None
        self._next = now + self._every
        newest = self._newest()
        if newest is not None and self._started_newest is not None and newest > self._started_newest:
            return "code changed: a sentinel/*.py file is newer than the code this process started with"
        env = self._env_mtime()
        # Any other time, earlier included (a .env put back from a copy), is
        # an edit. One that appears where there was none is new settings too;
        # one that disappears is not something a restart would fix.
        if env is not None and env != self._started_env:
            return "settings changed: the repository's .env was edited after this process read it"
        return None
