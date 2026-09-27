"""The checkup as a Telegram message: what to do first, then what to know, then how much was fine."""
from __future__ import annotations

from sentinel.checkup.model import Report, State, Verdict
from sentinel.checkup.slots import Slot
from sentinel.clock import to_ist
from sentinel.notify import MESSAGE_CHARS, redact

_HEAD = {Verdict.OK: "✅", Verdict.ATTENTION: "🟡", Verdict.ACTION: "🔴"}
_MARK = {State.FAIL: "✗", State.WARN: "!", State.INFO: "·"}


def format_report(report: Report, slot: Slot, console_url: str = "") -> str:
    when = to_ist(report.completed_utc or report.started_utc).strftime("%a %-d %b, %H:%M")
    lines = [f"{_HEAD[report.verdict]} Desk checkup · {slot.label} · {when}", report.headline(slot.when)]

    for item in report.to_do:
        lines += ["", f"{_MARK[item.state]} {item.title} — {item.detail}"]
        if item.action:
            lines.append(f"  → {item.action}")

    info = [i for i in report.items if i.state is State.INFO]
    if info:
        lines.append("")
        for item in info:
            lines.append(f"{_MARK[State.INFO]} {item.title} — {item.detail}")

    fine = [i.title for i in report.items if i.state is State.OK]
    skipped = [i for i in report.items if i.state is State.SKIP]
    if fine or skipped:
        lines.append("")
    if fine:
        lines.append(f"✓ Fine: {', '.join(fine)}.")
    if skipped:
        lines.append("Not checked: " + "; ".join(f"{i.title} ({i.detail.removeprefix('Not checked: ').rstrip('.')})"
                                                 for i in skipped) + ".")
    if console_url:
        lines += ["", f"{console_url.rstrip('/')}/admin/checkup"]

    text = redact("\n".join(lines))
    if len(text) > MESSAGE_CHARS:
        text = text[:MESSAGE_CHARS - 40].rstrip() + "\n… the rest is on System → Checkup."
    return text


_WORD = {State.FAIL: "TO DO", State.WARN: "LOOK", State.INFO: "NOTE", State.OK: "ok", State.SKIP: "not checked"}


def format_full(report: Report, slot: Slot) -> str:
    """Every item with what was found — for a person at the terminal (python -m sentinel.checkup)."""
    when = to_ist(report.completed_utc or report.started_utc).strftime("%a %-d %b, %H:%M IST")
    lines = [f"Desk checkup · {slot.label} · {when}", report.headline(slot.when), ""]
    order = {State.FAIL: 0, State.WARN: 1, State.INFO: 2, State.OK: 3, State.SKIP: 4}
    for item in sorted(report.items, key=lambda i: order[i.state]):
        lines.append(f"[{_WORD[item.state]:>11}] {item.area} · {item.title} — {item.detail}")
        if item.action:
            lines.append(f"{'':14}→ {item.action}")
    return redact("\n".join(lines))
