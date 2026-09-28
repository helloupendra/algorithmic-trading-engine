"""
OpenFNO pager: wakes the owner with a Telegram phone call when the live desk is in trouble.

Runs on the server beside the desk, outside the deploy path (a deploy is gated while runs are
live, and the pager has to work on the day it is needed). Once a minute it reads the local API
as the admin — GETs only — and asks five questions. A problem must hold for its whole grace
period before anyone is called, so one slow answer never rings a phone:

  api-down      the API has not answered /health for 3 minutes (08:40 to the day's last close)
  no-broker     from 09:00 to the NSE close neither Dhan nor FYERS is signed in: no feed can run
  no-ticks      while NSE is open (from 09:17) no index price has moved for 3 minutes
  runs-not-up   09:30–15:00: the morning plan has runs, and none is live (the morning job deploys
                them only after the 09:15 open, once it has seen prices move)
  runs-dropped  09:20–15:10: fewer than half of the day's most live runs are still live

A page is a Telegram text to the Desk System channel plus a CallMeBot call. While a problem
lasts it calls again every 10 minutes, at most 3 calls per problem per day, then texts every
30 minutes; when it clears, one "resolved" text. Nothing here writes to the desk.

Config (pager.env beside this file, never the repo's .env, which it only reads for the admin
sign-in and the Telegram bot): PAGER_CALLMEBOT_USER=@username. Unset, it texts but cannot call.

  python pager.py            run forever
  python pager.py --once     one pass, print what it sees, page nothing
  python pager.py --test     send one test text and one test call
"""
from __future__ import annotations

import json
import logging
import os
import sys
import time
import urllib.parse
from dataclasses import dataclass, field
from datetime import datetime, time as dtime, timedelta, timezone
from pathlib import Path
from typing import Any, Callable, Optional

import requests

HERE = Path(__file__).resolve().parent
REPO = Path(os.environ.get("OPENFNO_REPO", Path.home() / "algorithmic-trading-engine"))
sys.path.insert(0, str(REPO / "src" / "AlgoTrading.PythonEngine"))
from sentinel.clock import IST, ask_calendar, session_from_answers  # noqa: E402

log = logging.getLogger("pager")

INDEX_SYMBOLS = ("NSE:NIFTY50-INDEX", "NSE:NIFTYBANK-INDEX", "BSE:SENSEX-INDEX")
CHECK_EVERY = 60
GRACE = {"api-down": 180, "no-broker": 120, "no-ticks": 180, "runs-not-up": 120, "runs-dropped": 120}
RECALL_EVERY = timedelta(minutes=10)
TEXT_EVERY = timedelta(minutes=30)
MAX_CALLS = 3
RUNS_DROPPED_ON = False

WORDS = {
    "api-down": "OpenFNO alert. The desk API is not answering.",
    "no-broker": "OpenFNO alert. Dhan and FYERS are both signed out. No live feed.",
    "no-ticks": "OpenFNO alert. No live prices for three minutes. The feed is down.",
    "runs-not-up": "OpenFNO alert. The market is open and no strategy run is live.",
    "runs-dropped": "OpenFNO alert. More than half of the live runs have stopped.",
}


def load_env(path: Path) -> dict[str, str]:
    env: dict[str, str] = {}
    try:
        lines = path.read_text().splitlines()
    except OSError:
        return env
    for line in lines:
        line = line.strip()
        if line and not line.startswith("#") and "=" in line:
            key, value = line.split("=", 1)
            env[key.strip()] = value.strip().strip('"').strip("'")
    return env


def parse_utc(value: Any) -> Optional[datetime]:
    if not isinstance(value, str) or not value:
        return None
    try:
        moment = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None
    return moment if moment.tzinfo else moment.replace(tzinfo=timezone.utc)


# ------------------------------------------------------------------ what it sees

@dataclass
class Seen:
    """One minute's reading of the desk. None means "could not tell", which never pages by itself."""

    now: datetime
    api_up: bool
    trading_day: bool = False
    nse_open: bool = False
    last_close: Optional[datetime] = None
    dhan: Optional[bool] = None
    fyers: Optional[bool] = None
    newest_index_tick: Optional[datetime] = None
    planned: Optional[int] = None
    plan_live: Optional[int] = None   # planned runs whose runner is live; the manual book never counts
    live: Optional[int] = None


def problems(seen: Seen, day_max_live: int) -> dict[str, str]:
    """The problems this reading shows, each with a line of detail. Pure: the rules live here."""
    ist = seen.now.astimezone(IST)
    t = ist.time()
    out: dict[str, str] = {}
    # The desk does not trade at weekends even when the calendar has a special session
    # (Muhurat, a Budget Sunday), unless someone started runs by hand (Sentinel's health.py rule).
    if not seen.trading_day or (ist.weekday() >= 5 and not seen.plan_live):
        return out
    end = seen.last_close.astimezone(IST).time() if seen.last_close else dtime(15, 30)
    if not seen.api_up:
        if dtime(8, 40) <= t < end:
            out["api-down"] = "GET /health did not answer 200."
        return out   # nothing else can be read
    if dtime(9, 0) <= t < dtime(15, 30) and seen.dhan is False and seen.fyers is False:
        out["no-broker"] = "Dhan and FYERS both signed out."
    if seen.nse_open and t >= dtime(9, 17) and t < dtime(15, 29):
        tick = seen.newest_index_tick
        age = (seen.now - tick).total_seconds() if tick else None
        if age is None or age > 180:
            out["no-ticks"] = ("no index price at all" if age is None
                               else f"newest index price is {int(age // 60)} min {int(age % 60)} s old")
    if dtime(9, 30) <= t < dtime(15, 0) and seen.planned and seen.plan_live == 0:
        out["runs-not-up"] = f"{seen.planned} runs planned, 0 live."
    # Off until it can tell a runner that died from one the risk guard or a person stopped on purpose.
    if RUNS_DROPPED_ON and dtime(9, 20) <= t < dtime(15, 10) and seen.live is not None and day_max_live >= 4 \
            and seen.live < day_max_live / 2:
        out["runs-dropped"] = f"{seen.live} live now, {day_max_live} earlier today."
    return out


class Api:
    def __init__(self, base: str, user: str, password: str) -> None:
        self.base, self.user, self.password = base.rstrip("/"), user, password
        self.s = requests.Session()
        self.token: Optional[str] = None

    def health(self) -> bool:
        try:
            return self.s.get(f"{self.base}/health", timeout=10).status_code == 200
        except requests.RequestException:
            return False

    def get(self, path: str) -> Any:
        for attempt in (1, 2):
            if self.token is None:
                r = self.s.post(f"{self.base}/api/UserAuth/login", timeout=15,
                                json={"userNameOrEmail": self.user, "password": self.password})
                r.raise_for_status()
                self.token = r.json()["accessToken"]
            r = self.s.get(f"{self.base}{path}", timeout=15, headers={"Authorization": f"Bearer {self.token}"})
            if r.status_code == 401 and attempt == 1:
                self.token = None
                continue
            r.raise_for_status()
            return r.json()
        raise RuntimeError("unreachable")


def read_desk(api: Api, now: datetime) -> Seen:
    if not api.health():
        seen = Seen(now=now, api_up=False)
        # The calendar comes from the API too; without it, the weekday rule decides.
        session = session_from_answers(now, {})
        seen.trading_day = session.trading_day
        return seen
    seen = Seen(now=now, api_up=True)
    session = session_from_answers(now, ask_calendar(api.get))
    seen.trading_day, seen.nse_open = session.trading_day, session.nse_open
    seen.last_close = session.mcx_close
    try:
        for p in api.get("/api/Providers"):
            sess = p.get("session") or {}
            # isConnected already applies each broker's real expiry; needsReconnect only means
            # "saved on an earlier IST date", which is wrong for Dhan's 24-hour token.
            signed = bool(sess.get("isConnected"))
            expires = parse_utc(sess.get("expiresUtc"))
            if expires is not None and expires <= now:
                signed = False
            if p.get("key") == "dhan":
                seen.dhan = signed
            elif p.get("key") == "fyers":
                seen.fyers = signed
    except Exception as exc:   # an unreadable answer is "could not tell", never a page
        log.warning("providers unreadable: %s", type(exc).__name__)
    try:
        quotes = api.get("/api/LiveData/latest/all")
        stamps = [parse_utc(q.get("updatedUtc")) for q in quotes if q.get("symbol") in INDEX_SYMBOLS]
        stamps = [s for s in stamps if s is not None]
        seen.newest_index_tick = max(stamps) if stamps else None
    except Exception as exc:
        log.warning("quotes unreadable: %s", type(exc).__name__)
        seen.newest_index_tick = now   # unknown must not read as "no ticks"
    try:
        plan = api.get("/api/Desk/plan")
        seen.planned = int(plan.get("planned") or 0)
        seen.plan_live = int(plan.get("live") or 0)
    except Exception as exc:
        log.warning("plan unreadable: %s", type(exc).__name__)
    try:
        body = api.get("/api/Strategy/runs?status=Running&take=500")
        items = body.get("items", body) if isinstance(body, dict) else body
        seen.live = sum(1 for r in items if r.get("isActive", True)) if isinstance(items, list) else None
    except Exception as exc:
        log.warning("runs unreadable: %s", type(exc).__name__)
    return seen


# ------------------------------------------------------------------ who it tells

@dataclass
class Problem:
    since: str
    paged: bool = False
    calls: int = 0
    last_call: Optional[str] = None
    last_text: Optional[str] = None


@dataclass
class State:
    day: str = ""
    day_max_live: int = 0
    # The day's last close as the calendar last said it, for when the API (and so the calendar) is down.
    last_close: Optional[str] = None
    calls_today: dict[str, int] = field(default_factory=dict)
    open: dict[str, Problem] = field(default_factory=dict)

    @classmethod
    def load(cls, path: Path) -> "State":
        try:
            raw = json.loads(path.read_text())
            st = cls(day=raw.get("day", ""), day_max_live=raw.get("day_max_live", 0),
                     last_close=raw.get("last_close"), calls_today=raw.get("calls_today", {}))
            st.open = {k: Problem(**v) for k, v in raw.get("open", {}).items()}
            return st
        except (OSError, ValueError, TypeError):
            return cls()

    def save(self, path: Path) -> None:
        tmp = path.with_suffix(".tmp")
        tmp.write_text(json.dumps({"day": self.day, "day_max_live": self.day_max_live, "last_close": self.last_close,
                                   "calls_today": self.calls_today,
                                   "open": {k: vars(v) for k, v in self.open.items()}}, indent=1))
        tmp.replace(path)


def step(state: State, seen: Seen, found: dict[str, str],
         text: Callable[[str], bool], call: Callable[[str], bool]) -> None:
    """Advance every problem by one reading: page after its grace, re-call, text, resolve."""
    now = seen.now
    day = now.astimezone(IST).date().isoformat()
    if state.day != day:
        state.day, state.day_max_live, state.calls_today = day, 0, {}
    if seen.live is not None:
        state.day_max_live = max(state.day_max_live, seen.live)
    stamp = now.isoformat()
    for key, detail in found.items():
        p = state.open.setdefault(key, Problem(since=stamp))
        held = (now - datetime.fromisoformat(p.since)).total_seconds()
        if held < GRACE[key]:
            continue
        ist = now.astimezone(IST).strftime("%H:%M")
        if not p.paged:
            p.paged = True
            p.last_text = stamp
            text(f"🚨 PAGE {ist} — {key}: {detail} (for {int(held // 60)} min). Calling the owner.")
        calls = state.calls_today.get(key, 0)
        due = p.last_call is None or now - datetime.fromisoformat(p.last_call) >= RECALL_EVERY
        if calls < MAX_CALLS and due:
            if call(f"{WORDS[key]} {detail}"):
                state.calls_today[key] = calls + 1
                p.calls += 1
            p.last_call = stamp
        elif calls >= MAX_CALLS and now - datetime.fromisoformat(p.last_text) >= TEXT_EVERY:
            p.last_text = stamp
            text(f"🚨 STILL {ist} — {key}: {detail} (for {int(held // 60)} min; {MAX_CALLS} calls made today).")
    for key in [k for k in state.open if k not in found]:
        p = state.open.pop(key)
        if p.paged:
            text(f"✅ RESOLVED {now.astimezone(IST).strftime('%H:%M')} — {key} (paged {p.calls} call(s)).")


def telegram_sender(env: dict[str, str]) -> Callable[[str], bool]:
    token = env.get("TELEGRAM_BOT_TOKEN")
    chat = env.get("TELEGRAM_SYSTEM_CHAT_ID") or env.get("TELEGRAM_CHAT_ID")

    def send(message: str) -> bool:
        log.info("TEXT %s", message)
        if not token or not chat:
            return False
        try:
            r = requests.post(f"https://api.telegram.org/bot{token}/sendMessage", timeout=15,
                              json={"chat_id": chat, "text": message, "disable_web_page_preview": True})
            return r.ok
        except requests.RequestException as exc:
            log.warning("telegram failed: %s", type(exc).__name__)   # never the URL: it holds the token
            return False
    return send


def call_sender(user: Optional[str]) -> Callable[[str], bool]:
    def call(words: str) -> bool:
        log.info("CALL %s", words)
        if not user:
            log.warning("PAGER_CALLMEBOT_USER not set: cannot call")
            return False
        url = "https://api.callmebot.com/start.php?" + urllib.parse.urlencode(
            {"user": user, "text": words[:250], "rpt": "3", "cc": "yes"})
        try:
            r = requests.get(url, timeout=60)
            ok = r.ok and "error" not in r.text.lower()[:400]
            if not ok:
                log.warning("callmebot answered %s: %s", r.status_code, " ".join(r.text.split())[:200])
            return ok
        except requests.RequestException as exc:
            log.warning("callmebot failed: %s", type(exc).__name__)
            return False
    return call


def main(argv: list[str]) -> int:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s",
                        handlers=[logging.FileHandler(HERE / "pager.log"), logging.StreamHandler()])
    env = load_env(REPO / ".env")
    cfg = load_env(HERE / "pager.env")
    api = Api(env.get("API_BASE_URL", "http://localhost:5025"), env.get("ADMIN_USERNAME", ""),
              env.get("ADMIN_PASSWORD", ""))
    text, call = telegram_sender(env), call_sender(cfg.get("PAGER_CALLMEBOT_USER"))

    if "--test" in argv:
        t_ok = text("🔔 Pager test: this is the channel a real page would use.")
        c_ok = call("OpenFNO pager test. If you hear or see this call, the wake up alarm works.")
        print(f"text sent: {t_ok}; call placed: {c_ok}")
        return 0 if (t_ok and c_ok) else 1

    state_path = HERE / "state.json"
    while True:
        now = datetime.now(timezone.utc)
        state = State.load(state_path)
        try:
            seen = read_desk(api, now)
            if seen.last_close is not None:
                state.last_close = seen.last_close.isoformat()
            elif not seen.api_up and state.last_close and state.day == now.astimezone(IST).date().isoformat():
                seen.last_close = datetime.fromisoformat(state.last_close)
            found = problems(seen, max(state.day_max_live, seen.live or 0))
        except Exception as exc:
            log.warning("pass failed: %s: %s", type(exc).__name__, str(exc)[:160])
            seen, found = Seen(now=now, api_up=True), {}
        if "--once" in argv:
            print(json.dumps({k: (v.isoformat() if isinstance(v, datetime) else v) for k, v in vars(seen).items()}))
            print("problems:", found or "none")
            return 0
        step(state, seen, found, text, call)
        state.save(state_path)
        log.info("pass: api=%s dhan=%s fyers=%s live=%s/%s planned=%s problems=%s", seen.api_up, seen.dhan,
                 seen.fyers, seen.live, state.day_max_live, seen.planned, ",".join(found) or "-")
        time.sleep(CHECK_EVERY)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
