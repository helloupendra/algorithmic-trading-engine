#!/usr/bin/env python3
"""
The morning's tally: what the plan asked for against what is actually live.

Until 27 Sep the morning job ended by printing a snapshot of the Running rows
taken seconds after the last start, compared with nothing, and exited 0. On
24 Sep it printed "=== 10 run(s) live ===" against 26 planned: sixteen runners
had died 1-5 s after starting (a 429 on sign-in), and the only trace on the
phone was eight "Strategy stopped · ₹0.00" messages mixed into a hundred others.
The first hand restarts came at 11:20.

market-open.sh builds the expected runs with its own plan parser (the one its
tests pin), one "account|strategy|underlying" per line, and hands them over
with the API's answers through the environment:

    EXPECTED   the planned runs, one per line
    USERS      GET /api/Users                           (names -> ids)
    RUNNING    GET /api/Strategy/runs?status=Running    (what is live)
    TODAY      GET /api/Strategy/runs?fromDate=<today>  (why the missing ones ended)

It prints the report, the first line being the one-line summary for Telegram,
and exits 0 when every planned run is live, 2 when some are not.
"""
from __future__ import annotations

import json
import os
import sys
from dataclasses import dataclass, field

Key = tuple[str, str, str]   # (account, strategy, underlying), compared case-insensitively

#: Missing runs named in the Telegram line; the rest are in the log.
NAMED_IN_SUMMARY = 3


@dataclass
class Tally:
    planned: list[Key]
    live: list[Key] = field(default_factory=list)
    missing: list[tuple[Key, str]] = field(default_factory=list)

    @property
    def short(self) -> bool:
        return bool(self.missing)

    def summary(self) -> str:
        head = f"{len(self.live)}/{len(self.planned)} live"
        if not self.short:
            return f"Morning plan {head}"
        named = "; ".join(f"{_label(key)}: {reason}" for key, reason in self.missing[:NAMED_IN_SUMMARY])
        more = len(self.missing) - NAMED_IN_SUMMARY
        return f"Morning plan SHORT {head} — {named}" + (f"; and {more} more" if more > 0 else "")

    def lines(self) -> list[str]:
        out = [self.summary(), f"=== {len(self.live)} of {len(self.planned)} planned live ==="]
        out += [f"  missing: {_label(key)} — {reason}" for key, reason in self.missing]
        return out


def _label(key: Key) -> str:
    account, strategy, underlying = key
    return f"{account} {strategy} {underlying}"


def _norm(key: Key) -> Key:
    return key[0].strip().lower(), key[1].strip().lower(), key[2].strip().upper()


def parse_expected(text: str) -> list[Key]:
    """'admin|Ghost|NIFTY' lines, in order, without repeats."""
    seen: set[Key] = set()
    out: list[Key] = []
    for raw in (text or "").splitlines():
        parts = [p.strip() for p in raw.split("|")]
        if len(parts) != 3 or not all(parts):
            continue
        key = (parts[0], parts[1], parts[2])
        if _norm(key) not in seen:
            seen.add(_norm(key))
            out.append(key)
    return out


def _rows(value) -> list[dict]:
    if isinstance(value, dict):
        value = value.get("items") or value.get("runs") or []
    return [r for r in value if isinstance(r, dict)] if isinstance(value, list) else []


def tally(expected: list[Key], users: list[dict], running: list[dict], today: list[dict]) -> Tally:
    """
    Which planned runs are live. A run belongs to the account it runs FOR (its
    owner, userId), not to whoever started it: the morning job starts every
    account's runs as the admin.
    """
    names = {str(u.get("id")): str(u.get("userName") or "").strip().lower() for u in users if u.get("id") is not None}

    def owner(run: dict) -> str:
        return names.get(str(run.get("userId")), str(run.get("userName") or "").strip().lower())

    def key_of(run: dict) -> Key:
        return _norm((owner(run), str(run.get("strategyName") or ""), str(run.get("underlying") or "")))

    # "Running" with no runner behind it is a row, not a run: the API marks
    # isActive from the process registry. Older answers without the field count.
    live_keys = {key_of(r) for r in running
                 if str(r.get("status") or "Running").lower() == "running" and r.get("isActive", True) is not False}

    # Newest ending first, so the reason given is the latest attempt's.
    ended: dict[Key, dict] = {}
    for run in sorted(today, key=lambda r: str(r.get("startedUtc") or ""), reverse=True):
        ended.setdefault(key_of(run), run)

    known_accounts = set(names.values())
    result = Tally(planned=expected)
    for key in expected:
        norm = _norm(key)
        if norm in live_keys:
            result.live.append(key)
            continue
        if norm[0] not in known_accounts and users:
            reason = "no such active account"
        elif norm in ended:
            run = ended[norm]
            reason = str(run.get("stopReason") or "").strip() or f"{run.get('status') or 'ended'} without a reason"
            if run.get("durationSeconds") is not None:
                reason += f" (after {int(run['durationSeconds'])} s)"
        else:
            reason = "never started (not in the catalogue, or the start was refused)"
        result.missing.append((key, reason[:160]))
    return result


def main() -> int:
    def load(name: str, empty):
        raw = os.environ.get(name) or ""
        if not raw.strip():
            return empty
        try:
            return json.loads(raw)
        except ValueError:
            return None

    # An empty running list means the API did not answer, not "nothing runs":
    # read as [], every planned run would be reported as never started.
    users, running, today = load("USERS", []), load("RUNNING", None), load("TODAY", [])
    expected = parse_expected(os.environ.get("EXPECTED", ""))
    if running is None:
        # Unknown is not "short": say so, and let the summary line carry it.
        print(f"Morning plan UNKNOWN — the running list could not be read ({len(expected)} planned)")
        return 2
    result = tally(expected, _rows(users or []), _rows(running), _rows(today or []))
    print("\n".join(result.lines()))
    return 2 if result.short else 0


if __name__ == "__main__":
    sys.exit(main())
