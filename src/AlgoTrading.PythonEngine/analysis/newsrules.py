"""
analysis/newsrules.py

A headline's importance (0-3) and topic tags, by keyword and source: a first
version, kept small enough to read in one go, so every score can be traced to
the rule that gave it.

Importance, on the market_events scale:
    0  noise or paperwork (trading-window closures, lost share certificates)
    1  worth knowing: routine company news, a market wrap, oil, the rupee
    2  moves a stock or a sector: results, M&A, rating changes, block deals,
       a SEBI order, inflation prints, FII flows, geopolitics
    3  can move the whole market: an RBI or Fed decision, the Budget, a default

An item's importance is the highest of its topics', plus one (to at most 3)
when it names a NIFTY-50 company in a topic that is not paperwork: an index
forecast cares more about HDFC Bank's results than a small-cap's. NSE
announcements are read by their Subject line first, the words the exchange
files them under.

No rule reads anything but the item's own text, source and subject; nothing
in a headline can change what the rules do.
"""

from __future__ import annotations

import re
from typing import Iterable, Pattern, Sequence, Tuple

from analysis.context import NIFTY50

RULES_VERSION = "rules-v1"

#: (topic, importance, patterns). Case-insensitive; the first match per topic is enough.
TOPIC_RULES: Tuple[Tuple[str, int, Tuple[str, ...]], ...] = (
    ("rbi-policy", 3, (r"\brbi\b.{0,60}\b(repo|policy|mpc|rate cut|rate hike|rates)\b",
                       r"\bmonetary policy committee\b", r"\bmpc\b")),
    ("fed", 3, (r"\bfomc\b", r"\bfederal reserve\b", r"\bfed\b.{0,60}\b(rate|rates|hike|cut|policy|powell)\b")),
    ("budget", 3, (r"\bunion budget\b", r"\binterim budget\b", r"\bbudget 20\d\d\b")),
    ("default", 3, (r"\bdefault(s|ed)?\b", r"\binsolvency\b", r"\bnclt\b", r"\bbankrupt(cy)?\b")),
    ("inflation", 2, (r"\b(cpi|wpi|inflation)\b",)),
    ("results", 2, (r"\bq[1-4]\b.{0,40}\b(results?|profit|loss|earnings|revenue)\b",
                    r"\b(quarterly|financial) results?\b", r"\bnet profit\b", r"\bearnings\b")),
    ("guidance", 2, (r"\bguidance\b", r"\boutlook\b")),
    ("m&a", 2, (r"\bacqui(re|res|red|sition|sitions)\b", r"\bmerger\b", r"\bamalgamation\b", r"\btakeover\b",
                r"\bopen offer\b", r"\bdemerger\b")),
    ("block-deal", 2, (r"\bblock deals?\b", r"\bbulk deals?\b")),
    ("sebi-order", 2, (r"\bsebi\b.{0,60}\b(order|orders|bans?|bars|barred|penalty|penalises|show[- ]cause|probe)\b",)),
    ("rating", 2, (r"\b(upgrade|upgrades|upgraded|downgrade|downgrades|downgraded)\b", r"\bcredit rating\b",
                   r"\b(crisil|icra|moody's|moodys|fitch)\b")),
    ("fii-flows", 2, (r"\bf[ip]is?\b.{0,40}\b(sell|sold|buy|bought|outflows?|inflows?|net)\b",
                      r"\bforeign (portfolio )?investors?\b")),
    # Trade tariffs, not a telecom's "tariff hike".
    ("geopolitics", 2, (r"\bwar\b", r"\b(import|trade|reciprocal|retaliatory) tariffs?\b", r"\btariffs? on\b",
                        r"\bsanctions?\b", r"\bceasefire\b", r"\bmissile\b")),
    ("buyback", 2, (r"\bbuy-?back\b",)),
    ("dividend", 1, (r"\bdividend\b",)),
    ("fundraise", 1, (r"\bqip\b", r"\brights issue\b", r"\bpreferential (issue|allotment)\b", r"\bfund ?rais")),
    ("order-win", 1, (r"\b(bags?|wins?|secures?)\b.{0,40}\b(order|contract|deal)s?\b",)),
    ("management", 1, (r"\b(ceo|cfo|md|chairman|chairperson|director)\b.{0,40}\b(resigns?|resignation|appointed|"
                       r"appointment|steps down|quits)\b",)),
    ("ipo", 1, (r"\bipo\b",)),
    ("oil", 1, (r"\b(crude|brent)\b", r"\boil prices?\b")),
    ("currency", 1, (r"\brupee\b", r"\busd/?inr\b", r"\bdollar index\b")),
    ("market-move", 1, (r"\b(sensex|nifty)\b",)),
)

#: NSE announcement subjects, matched on the Subject line: (topic, importance, substrings).
SUBJECT_RULES: Tuple[Tuple[str, int, Tuple[str, ...]], ...] = (
    ("paperwork", 0, ("trading window", "loss of share certificate", "duplicate share certificate",
                      "newspaper publication", "reg. 74 (5)", "regulation 74(5)", "74(5)", "compliance certificate",
                      "investor complaints", "certificate under sebi")),
    ("results", 2, ("financial result",)),
    ("m&a", 2, ("acquisition", "amalgamation", "scheme of arrangement", "merger", "demerger", "open offer")),
    ("buyback", 2, ("buyback", "buy back")),
    ("rating", 1, ("credit rating",)),
    ("dividend", 1, ("dividend",)),
    ("board-meeting", 1, ("board meeting", "outcome of board")),
    ("management", 1, ("resignation", "appointment", "cessation", "change in director", "change in management")),
    ("fundraise", 1, ("allotment", "qip", "rights issue", "preferential issue", "fund raising")),
    ("investor-meet", 0, ("analysts/institutional investor meet", "investor meet", "con. call")),
)

#: Official sources whose every item is at least this important, by a substring of news_items.Source.
SOURCE_FLOORS: Tuple[Tuple[str, str, int], ...] = (
    ("rbi", "rbi", 1),
    ("sebi", "sebi", 1),
)

TOPICS = frozenset({t for t, _, _ in TOPIC_RULES} | {t for t, _, _ in SUBJECT_RULES}
                   | {t for _, t, _ in SOURCE_FLOORS})

_COMPILED: Tuple[Tuple[str, int, Tuple[Pattern, ...]], ...] = tuple(
    (topic, level, tuple(re.compile(p, re.IGNORECASE) for p in patterns)) for topic, level, patterns in TOPIC_RULES)
_NIFTY50 = frozenset(NIFTY50)


def classify(text: str, source: str = "", subject: str = "",
             symbols: Iterable[str] = ()) -> Tuple[int, Tuple[str, ...]]:
    """(importance 0-3, topics sorted) for one item."""
    hits = {}
    subject_l = (subject or "").lower()
    for topic, level, needles in SUBJECT_RULES:
        if any(n in subject_l for n in needles):
            hits[topic] = max(hits.get(topic, 0), level)
    for topic, level, patterns in _COMPILED:
        if any(p.search(text or "") for p in patterns):
            hits[topic] = max(hits.get(topic, 0), level)
    source_l = (source or "").lower()
    for needle, topic, floor in SOURCE_FLOORS:
        if needle in source_l:
            hits[topic] = max(hits.get(topic, 0), floor)
    if "paperwork" in hits:
        # A trading-window notice that happens to say "results" is still a trading-window notice.
        return 0, ("paperwork",)
    importance = max(hits.values(), default=0)
    if importance >= 1 and any(s in _NIFTY50 for s in symbols):
        importance = min(3, importance + 1)
    return importance, tuple(sorted(hits))


def valid(importance: int, topics: Sequence[str]) -> bool:
    return isinstance(importance, int) and 0 <= importance <= 3 and all(t in TOPICS for t in topics)
