"""What a checkup finds: items, each with a state and, when it needs a person, an action."""
from __future__ import annotations

from dataclasses import asdict, dataclass, field
from datetime import datetime
from enum import Enum
from typing import Optional


class State(str, Enum):
    """
    ``fail``: something is wrong and a person has to act. ``warn``: worth a
    look, the day can go on. ``info``: a fact the operator should have in mind
    (what was carried overnight, today's result). ``skip``: this check could
    not be made — never shown as good, because it was not looked at.
    """

    OK = "ok"
    WARN = "warn"
    FAIL = "fail"
    INFO = "info"
    SKIP = "skip"


class Verdict(str, Enum):
    OK = "ok"
    ATTENTION = "attention"
    ACTION = "action"


@dataclass(frozen=True)
class Item:
    """
    One line of the checklist. ``detail`` says what was found, in numbers where
    there are numbers; ``action`` says what to do, and is empty when nothing
    is. ``link`` is a console path that shows more.
    """

    key: str
    area: str
    title: str
    state: State
    detail: str
    action: str = ""
    link: str = ""

    def to_json(self) -> dict:
        d = asdict(self)
        d["state"] = self.state.value
        return d


@dataclass
class Report:
    slot: str
    started_utc: datetime
    items: list[Item] = field(default_factory=list)
    completed_utc: Optional[datetime] = None
    host: str = ""

    @property
    def verdict(self) -> Verdict:
        states = {i.state for i in self.items}
        if State.FAIL in states:
            return Verdict.ACTION
        if State.WARN in states:
            return Verdict.ATTENTION
        return Verdict.OK

    def count(self, state: State) -> int:
        return sum(1 for i in self.items if i.state is state)

    @property
    def to_do(self) -> list[Item]:
        """What needs a person, the failures first, in the order they were checked."""
        return [i for i in self.items if i.state is State.FAIL] + [i for i in self.items if i.state is State.WARN]

    def headline(self, when: str) -> str:
        """One sentence, e.g. "2 things to do before the open" or "All clear after the close"."""
        fail, warn = self.count(State.FAIL), self.count(State.WARN)
        checked = sum(1 for i in self.items if i.state is not State.SKIP)
        if fail:
            head = f"{fail} thing{'s' if fail != 1 else ''} to do {when}"
            return head + (f", {warn} more worth a look" if warn else "")
        if warn:
            return f"{warn} thing{'s' if warn != 1 else ''} worth a look {when}"
        skipped = self.count(State.SKIP)
        tail = f", {skipped} not checked" if skipped else ""
        return f"All clear {when}: {checked} check{'s' if checked != 1 else ''} fine{tail}"
