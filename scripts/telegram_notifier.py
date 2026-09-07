#!/usr/bin/env python
"""
Telegram notifier — a standalone sidecar that needs nothing restarted.

WHY THIS EXISTS
---------------
The platform already has an alert path:

    Redis pub/sub "alerts:new"  ->  AlertSubscriberService (in the API)
                                ->  Telegram  +  the alert_events table

Two things stop it from actually reaching Telegram today:

  1. POST /api/Strategy/{id}/start — the endpoint the console uses to start a
     run — never calls ISystemNotifier.NotifyAsync, so a run starting publishes
     nothing at all. (Only /deploy and the stop endpoints notify.)
  2. The running API has an empty Telegram:BotToken, because
     appsettings.Local.json carries no "Telegram" section. So even a published
     event is recorded to the database and never sent.

Both real fixes are source changes to the API, and the API cannot be rebuilt or
restarted while strategies are live. This process closes the gap from the
outside instead:

  * FORWARDER — subscribes to "alerts:new" and sends every event to Telegram.
    That covers fix (2) for events the backend already publishes (the strategy
    alerter, risk/kill-switch, deploy and stop).
  * POLLER — reads the API read-only, diffs successive snapshots, and publishes
    the events nobody emits today: run started, position opened, position
    closed, run stopped, market data started/stopped. That covers fix (1).

Publishing rather than sending directly is deliberate: an event still travels
the platform's own path, so it is recorded in alert_events and shows up in the
console's alert stream exactly like a native one.

SAFETY
------
Against the API this process only ever issues GETs; the single write it makes is
a Redis PUBLISH. It cannot affect a running strategy. It is safe to start and
stop at any time, including mid-session.

On its first tick it takes a silent baseline and alerts on nothing, so starting
it while seven runs are already live does not blast seven start alerts. It sends
one summary of what it found instead.

USAGE
-----
    .venv/Scripts/python.exe scripts/telegram_notifier.py

    --once          take one baseline, send the startup summary, exit
    --dry-run       log the Telegram messages instead of sending them
    --interval N    seconds between polls (default 5)
    --no-forward    do not forward events published by others
    --quiet-start   skip the startup summary message

Configuration is read from .env: TELEGRAM_BOT_TOKEN, TELEGRAM_CHAT_ID,
REDIS_HOST/REDIS_PORT/REDIS_DB, ADMIN_USERNAME/ADMIN_PASSWORD, API_BASE_URL.
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import re
import signal
import sys
import threading
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any

import redis
import requests

# The API stamps every timestamp in UTC; the desk reads IST. Fixed offset rather
# than a tz database lookup: India has no DST, and this must not depend on the
# tzdata package being installed.
IST = timezone(timedelta(hours=5, minutes=30), "IST")

REPO_ROOT = Path(__file__).resolve().parent.parent
ALERT_CHANNEL = "alerts:new"

log = logging.getLogger("notifier")


# --------------------------------------------------------------------------- #
# configuration
# --------------------------------------------------------------------------- #
def load_env(path: Path) -> dict[str, str]:
    """Read a .env file into a dict. Absent file is not an error."""
    env: dict[str, str] = {}
    if not path.exists():
        return env
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, value = line.partition("=")
        env[key.strip()] = value.strip()
    return env


class Config:
    def __init__(self, env: dict[str, str]) -> None:
        get = lambda k, d="": os.environ.get(k) or env.get(k, d)  # noqa: E731

        self.api_base = (get("API_BASE_URL", "http://localhost:5025")).rstrip("/")
        self.admin_user = get("ADMIN_USERNAME", "admin")
        self.admin_pass = get("ADMIN_PASSWORD", "admin")

        self.redis_host = get("REDIS_HOST", "localhost")
        self.redis_port = int(get("REDIS_PORT", "6379") or 6379)
        self.redis_db = int(get("REDIS_DB", "0") or 0)
        self.redis_password = get("REDIS_PASSWORD", "") or None

        self.bot_token = get("TELEGRAM_BOT_TOKEN", "")
        self.chat_id = get("TELEGRAM_CHAT_ID", "")

    @property
    def telegram_configured(self) -> bool:
        return bool(self.bot_token and self.chat_id)


# --------------------------------------------------------------------------- #
# formatting helpers
# --------------------------------------------------------------------------- #
def esc(value: Any) -> str:
    """Escape for Telegram parse_mode=HTML. Only these three matter."""
    return (
        str(value)
        .replace("&", "&amp;")
        .replace("<", "&lt;")
        .replace(">", "&gt;")
    )


def ist(utc_iso: str | None) -> str:
    """Render an API UTC timestamp as IST clock time."""
    if not utc_iso:
        return "—"
    text = utc_iso.replace("Z", "+00:00")
    try:
        moment = datetime.fromisoformat(text)
    except ValueError:
        return utc_iso
    if moment.tzinfo is None:
        moment = moment.replace(tzinfo=timezone.utc)
    return moment.astimezone(IST).strftime("%d %b, %H:%M:%S IST")


def money(value: Any) -> str:
    """Signed rupees, two decimals, thousands separated."""
    try:
        amount = float(value or 0)
    except (TypeError, ValueError):
        return "₹0.00"
    sign = "+" if amount > 0 else ("-" if amount < 0 else "")
    return f"{sign}₹{abs(amount):,.2f}"


def plain(value: Any) -> str:
    try:
        return f"₹{float(value or 0):,.2f}"
    except (TypeError, ValueError):
        return "₹0.00"


def duration(seconds: Any) -> str:
    try:
        total = int(float(seconds or 0))
    except (TypeError, ValueError):
        return "—"
    hours, rem = divmod(total, 3600)
    minutes, secs = divmod(rem, 60)
    if hours:
        return f"{hours}h {minutes}m {secs}s"
    if minutes:
        return f"{minutes}m {secs}s"
    return f"{secs}s"


def qty_line(lots: Any, lot_size: Any, quantity: Any = None) -> str:
    """The platform's own convention: lots x lot size, never a bare count."""
    try:
        computed = int(lots or 0) * int(lot_size or 0)
    except (TypeError, ValueError):
        computed = quantity or 0
    return f"{lots} lot × {lot_size} = {quantity if quantity is not None else computed}"


# --------------------------------------------------------------------------- #
# API access — strictly read-only
# --------------------------------------------------------------------------- #
class ApiClient:
    """Signs in as the admin and re-authenticates whenever the token lapses."""

    def __init__(self, config: Config) -> None:
        self._config = config
        self._session = requests.Session()
        self._token: str | None = None

    def login(self) -> None:
        response = self._session.post(
            f"{self._config.api_base}/api/UserAuth/login",
            json={
                "userNameOrEmail": self._config.admin_user,
                "password": self._config.admin_pass,
            },
            timeout=20,
        )
        response.raise_for_status()
        self._token = response.json()["accessToken"]
        log.info("signed in to the API as %s", self._config.admin_user)

    def get(self, path: str) -> Any:
        """GET with one automatic re-login on 401. Returns None on failure."""
        for attempt in (1, 2):
            if self._token is None:
                self.login()
            try:
                response = self._session.get(
                    f"{self._config.api_base}{path}",
                    headers={"Authorization": f"Bearer {self._token}"},
                    timeout=20,
                )
            except requests.RequestException as exc:
                log.warning("GET %s failed: %s", path, exc)
                return None

            if response.status_code == 401 and attempt == 1:
                # The access token lives an hour; this is the expiry, not a
                # credential problem. Get a new one and repeat once.
                self._token = None
                continue
            if not response.ok:
                log.warning("GET %s -> HTTP %s", path, response.status_code)
                return None
            try:
                return response.json()
            except ValueError:
                log.warning("GET %s returned a non-JSON body", path)
                return None
        return None


# --------------------------------------------------------------------------- #
# Telegram
# --------------------------------------------------------------------------- #
class Telegram:
    def __init__(self, config: Config, dry_run: bool = False) -> None:
        self._config = config
        self._dry_run = dry_run
        self._session = requests.Session()
        self._lock = threading.Lock()
        self._last_sent = 0.0

    def send(self, text: str) -> bool:
        if self._dry_run:
            log.info("[dry-run] would send:\n%s", text)
            return True
        if not self._config.telegram_configured:
            log.warning("Telegram is not configured; dropping a message")
            return False

        # Telegram throttles a group at roughly 20 messages a minute. Runs open
        # several legs at once, so space the sends rather than risk a 429.
        with self._lock:
            gap = time.monotonic() - self._last_sent
            if gap < 1.1:
                time.sleep(1.1 - gap)
            self._last_sent = time.monotonic()

        url = f"https://api.telegram.org/bot{self._config.bot_token}/sendMessage"
        body = {
            "chat_id": self._config.chat_id,
            "text": text,
            "parse_mode": "HTML",
            "disable_web_page_preview": True,
        }
        for attempt in (1, 2, 3):
            try:
                response = self._session.post(url, json=body, timeout=20)
                if response.ok:
                    return True
                if response.status_code == 429:
                    retry_after = 3
                    try:
                        retry_after = int(
                            response.json().get("parameters", {}).get("retry_after", 3)
                        )
                    except Exception:  # noqa: BLE001 - body shape is not guaranteed
                        pass
                    log.warning("Telegram rate limited; waiting %ss", retry_after)
                    time.sleep(retry_after)
                    continue
                log.warning(
                    "Telegram refused the message: HTTP %s %s",
                    response.status_code,
                    response.text[:200],
                )
                return False
            except requests.RequestException as exc:
                log.warning("Telegram send failed (attempt %s): %s", attempt, exc)
                time.sleep(2 * attempt)
        return False


# --------------------------------------------------------------------------- #
# publishing onto the platform's own alert path
# --------------------------------------------------------------------------- #
class AlertPublisher:
    """
    Publishes to "alerts:new" in the shape AlertSubscriberService expects.

    The keys MUST be PascalCase. The subscriber calls
    JsonSerializer.Deserialize<AlertEventPayload> with no options, so binding is
    case-sensitive — lowercase keys bind to nothing and the row is written with
    every field at its fallback ("Alert" / "system" / "UNKNOWN").
    """

    def __init__(self, client: redis.Redis) -> None:
        self._redis = client

    def publish(
        self,
        *,
        title: str,
        message: str,
        source: str,
        severity: str,
        underlying: str | None = None,
        symbol: str | None = None,
        run_id: int | None = None,
    ) -> None:
        payload = {
            "Title": title,
            "Message": message,
            "Source": source,
            "Severity": severity,
            "Underlying": underlying,
            "Symbol": symbol,
            "SimulationRunId": run_id,
        }
        try:
            self._redis.publish(ALERT_CHANNEL, json.dumps(payload))
        except redis.RedisError as exc:
            # Reporting must never be louder than the thing it reports on.
            log.warning("could not publish '%s': %s", title, exc)


# --------------------------------------------------------------------------- #
# the poller
# --------------------------------------------------------------------------- #
SEVERITY_ICON = {
    "info": "ℹ️",
    "success": "✅",
    "warning": "⚠️",
    "error": "🔴",
}


class Watcher:
    """
    Diffs successive API snapshots and publishes what changed.

    Two endpoints, deliberately used differently:

      /api/Strategy/runs        — LiveRunHistoryBuilder, entirely AsNoTracking,
                                  no writes and no locks. Cheap; polled every tick.
      /api/Strategy/runs/{}/live — reaches PaperTradingService.GetPaperPositionsAsync,
                                  which takes the run's SimulationRunLocks gate — the
                                  same mutex the order/fill path uses — marks the open
                                  legs to market and SaveChangesAsync()es them.

    The second one is what carries per-leg detail, so it cannot be avoided, but it
    can be made rare: the cheap list already reports openPositions and trades per
    run, and a leg cannot open or close without moving one of those two counters.
    So the gate is touched only when a run's counters actually change — plus a slow
    reconcile so a missed transition cannot go unnoticed forever.

    For scale: the web console polls this same endpoint every 2s per live run, so
    even the reconcile is a small fraction of the load the platform already carries.
    """

    def __init__(
        self,
        api: ApiClient,
        publisher: AlertPublisher,
        reconcile_seconds: float = 120.0,
    ) -> None:
        self._api = api
        self._publisher = publisher
        self._reconcile_seconds = reconcile_seconds

        # runId -> the run object as last seen
        self._runs: dict[int, dict[str, Any]] = {}
        # runId -> {positionId: status}
        self._positions: dict[int, dict[int, str]] = {}
        # runId -> (openPositions, trades) as last seen on the cheap list
        self._counters: dict[int, tuple[int, int]] = {}
        # runId -> monotonic time of the last /live call
        self._last_detail: dict[int, float] = {}
        self._ingestor_running: bool | None = None
        self._baselined = False
        self.detail_calls = 0

    @staticmethod
    def _counter_of(run: dict[str, Any]) -> tuple[int, int]:
        def as_int(value: Any) -> int:
            try:
                return int(value or 0)
            except (TypeError, ValueError):
                return 0

        return as_int(run.get("openPositions")), as_int(run.get("trades"))

    # -- snapshots ---------------------------------------------------------- #
    def _fetch_runs(self) -> dict[int, dict[str, Any]] | None:
        runs = self._api.get("/api/Strategy/runs")
        if not isinstance(runs, list):
            return None
        return {int(r["runId"]): r for r in runs if r.get("runId") is not None}

    def _fetch_live(self, run_id: int) -> dict[str, Any] | None:
        """The only call that touches the run's gate. Kept deliberately rare."""
        self.detail_calls += 1
        self._last_detail[run_id] = time.monotonic()
        live = self._api.get(f"/api/Strategy/runs/{run_id}/live")
        return live if isinstance(live, dict) else None

    def _needs_detail(self, run_id: int, run: dict[str, Any]) -> bool:
        """
        True when the cheap list says something about this run's book moved, or
        when it has been long enough that a reconcile is due.
        """
        counter = self._counter_of(run)
        if self._counters.get(run_id) != counter:
            return True
        last = self._last_detail.get(run_id)
        return last is None or (time.monotonic() - last) >= self._reconcile_seconds

    def _fetch_ingestor(self) -> bool | None:
        status = self._api.get("/api/Ingestor/status")
        if not isinstance(status, dict) or "isRunning" not in status:
            return None
        return bool(status["isRunning"])

    # -- baseline ----------------------------------------------------------- #
    def baseline(self) -> dict[str, Any]:
        """Record the world as it is, alerting on nothing."""
        runs = self._fetch_runs() or {}
        self._runs = runs
        for run_id, run in runs.items():
            self._counters[run_id] = self._counter_of(run)
            if run.get("isActive"):
                live = self._fetch_live(run_id)
                self._positions[run_id] = self._position_states(live)
        self._ingestor_running = self._fetch_ingestor()
        self._baselined = True

        active = [r for r in runs.values() if r.get("isActive")]
        return {
            "runs": len(runs),
            "active": len(active),
            "active_runs": active,
            # Legs are tracked whatever their status, so that a leg already
            # closed is not re-announced; only the open ones are worth counting.
            "open_positions": sum(
                sum(1 for status in legs.values() if status.lower() == "open")
                for legs in self._positions.values()
            ),
            "ingestor": self._ingestor_running,
        }

    @staticmethod
    def _position_states(live: dict[str, Any] | None) -> dict[int, str]:
        if not live:
            return {}
        states: dict[int, str] = {}
        for position in live.get("positions") or []:
            pid = position.get("id")
            if pid is None:
                continue
            states[int(pid)] = str(position.get("status") or "Unknown")
        return states

    # -- the tick ----------------------------------------------------------- #
    def tick(self) -> None:
        if not self._baselined:
            self.baseline()
            return

        runs = self._fetch_runs()
        if runs is None:
            return  # a transient API problem; try again next tick

        self._diff_runs(runs)
        self._diff_ingestor()
        self._runs = runs

    # -- runs --------------------------------------------------------------- #
    def _diff_runs(self, runs: dict[int, dict[str, Any]]) -> None:
        for run_id, run in runs.items():
            before = self._runs.get(run_id)
            was_active = bool(before and before.get("isActive"))
            is_active = bool(run.get("isActive"))

            if is_active and not was_active:
                self._alert_run_started(run)
                # Seed the leg map so the opening legs are reported as they
                # appear rather than swallowed by a baseline.
                self._positions.setdefault(run_id, {})

            if is_active and self._needs_detail(run_id, run):
                self._diff_positions(run_id, run)

            if was_active and not is_active:
                # One last look before the run leaves the active set, so a leg
                # closed by the stop itself is still reported.
                if self._needs_detail(run_id, run):
                    self._diff_positions(run_id, run)
                self._alert_run_stopped(run)
                self._positions.pop(run_id, None)
                self._last_detail.pop(run_id, None)

            self._counters[run_id] = self._counter_of(run)

    def _alert_run_started(self, run: dict[str, Any]) -> None:
        name = run.get("strategyName", "?")
        underlying = run.get("underlying", "?")
        lines = [
            f"▶️ <b>Strategy started</b> — <b>{esc(name)}</b>",
            "",
            f"Underlying: <b>{esc(underlying)}</b>  ({esc(run.get('spotSymbol', '—'))})",
            f"Quantity:   {esc(qty_line(run.get('lots'), run.get('lotSize')))}",
            f"Started:    {esc(ist(run.get('startedUtc')))}",
            f"By:         {esc(run.get('userName', '—'))}   ·   run #{run.get('runId')}",
        ]
        risk = (run.get("risk") or {}).get("overall") or {}
        if risk.get("stopLoss") or risk.get("target"):
            stop = plain(risk["stopLoss"]) if risk.get("stopLoss") else "—"
            target = plain(risk["target"]) if risk.get("target") else "—"
            lines.append(f"Risk:       SL {stop}  ·  target {target}")

        self._publisher.publish(
            title=f"Strategy started · {name} · {underlying}",
            message="\n".join(lines),
            source="strategyrun",
            severity="success",
            underlying=underlying,
            symbol=run.get("spotSymbol"),
            run_id=run.get("runId"),
        )

    def _alert_run_stopped(self, run: dict[str, Any]) -> None:
        name = run.get("strategyName", "?")
        underlying = run.get("underlying", "?")
        net = float(run.get("netPnl") or 0)
        verdict = "profit" if net > 0 else ("loss" if net < 0 else "flat")

        # LiveRunHistoryBuilder sets NetPnl = realized exactly — it never folds in
        # unrealized — so printing both as separate lines would just say the same
        # number twice. Unrealized is forced to 0 once a run is inactive, and
        # CapitalUsed goes null; neither is worth a line then.
        lines = [
            f"⏹️ <b>Strategy stopped</b> — <b>{esc(name)}</b> · {esc(underlying)}",
            "",
            f"Net P&amp;L:   <b>{esc(money(net))}</b>  ({verdict}, realized)",
        ]
        unrealized = float(run.get("unrealizedPnl") or 0)
        if unrealized:
            lines.append(f"Unrealized: {esc(money(unrealized))}  (legs left open)")
        lines += [
            f"Trades:     {run.get('trades', 0)} closed   ·   still open: {run.get('openPositions', 0)}",
            f"Quantity:   {esc(qty_line(run.get('lots'), run.get('lotSize')))}",
        ]
        if run.get("capitalUsed") is not None:
            lines.append(f"Capital:    {esc(plain(run['capitalUsed']))}")
        lines += [
            "",
            f"Ran for:    {esc(duration(run.get('durationSeconds')))}",
            f"Started:    {esc(ist(run.get('startedUtc')))}",
            f"Stopped:    {esc(ist(run.get('stoppedUtc')))}",
        ]
        if run.get("stopReason"):
            lines.append(f"Reason:     {esc(run['stopReason'])}")
        if run.get("stoppedBy"):
            lines.append(f"Stopped by: {esc(run['stoppedBy'])}")
        lines.append(f"Run:        #{run.get('runId')}")

        self._publisher.publish(
            title=f"Strategy stopped · {name} · {underlying} · {money(net)}",
            message="\n".join(lines),
            source="strategyrun",
            severity="success" if net > 0 else ("warning" if net < 0 else "info"),
            underlying=underlying,
            symbol=run.get("spotSymbol"),
            run_id=run.get("runId"),
        )

    # -- positions ----------------------------------------------------------- #
    def _diff_positions(self, run_id: int, run: dict[str, Any]) -> None:
        live = self._fetch_live(run_id)
        if live is None:
            return

        known = self._positions.setdefault(run_id, {})
        seen: dict[int, str] = {}

        for position in live.get("positions") or []:
            pid = position.get("id")
            if pid is None:
                continue
            pid = int(pid)
            status = str(position.get("status") or "Unknown")
            seen[pid] = status
            previous = known.get(pid)

            if previous is None:
                self._alert_position_opened(run, position)
            elif previous != status and status.lower() != "open":
                self._alert_position_closed(run, position)

        # A leg that vanished from the payload entirely counts as closed, but we
        # have no closing numbers for it, so say only what is true.
        for pid, previous in known.items():
            if pid not in seen and previous.lower() == "open":
                self._alert_position_vanished(run, pid)

        self._positions[run_id] = seen

    @staticmethod
    def _contract(position: dict[str, Any]) -> str:
        contract = position.get("contract") or {}
        return str(contract.get("label") or position.get("symbol") or "?")

    def _alert_position_opened(self, run: dict[str, Any], position: dict[str, Any]) -> None:
        label = self._contract(position)
        side = str(position.get("side") or "?").upper()
        icon = "🟢" if side == "BUY" else "🔴"

        lines = [
            f"{icon} <b>Position opened</b> — {esc(side)} <b>{esc(label)}</b>",
            "",
            f"Strategy: {esc(run.get('strategyName', '?'))} · {esc(run.get('underlying', '?'))}",
            f"Quantity: {esc(qty_line(position.get('lots'), position.get('lotSize'), position.get('quantity')))}",
            f"Entry:    <b>{esc(position.get('entryPrice'))}</b>",
            f"LTP:      {esc(position.get('ltp'))}",
            f"Value:    {esc(plain(position.get('entryValue')))}",
            f"Opened:   {esc(ist(position.get('openedUtc')))}",
            f"Run:      #{run.get('runId')}",
        ]

        self._publisher.publish(
            title=f"Position opened · {side} {label}",
            message="\n".join(lines),
            source="strategyrun",
            severity="info",
            underlying=run.get("underlying"),
            symbol=position.get("symbol"),
            run_id=run.get("runId"),
        )

    def _alert_position_closed(self, run: dict[str, Any], position: dict[str, Any]) -> None:
        label = self._contract(position)
        side = str(position.get("side") or "?").upper()
        pnl = float(position.get("pnl") or 0)
        icon = "✅" if pnl > 0 else ("🔻" if pnl < 0 else "⚪")

        lines = [
            f"{icon} <b>Position closed</b> — {esc(side)} <b>{esc(label)}</b>",
            "",
            f"Strategy: {esc(run.get('strategyName', '?'))} · {esc(run.get('underlying', '?'))}",
            f"Quantity: {esc(qty_line(position.get('lots'), position.get('lotSize'), position.get('quantity')))}",
            f"Entry:    {esc(position.get('entryPrice'))}",
            f"Exit:     <b>{esc(position.get('ltp'))}</b>",
            f"P&amp;L:      <b>{esc(money(pnl))}</b>"
            f"   ({esc(position.get('pnlPoints'))} pts · {esc(position.get('pnlPercent'))}%)",
            f"Closed:   {esc(ist(position.get('closedUtc') or position.get('ltpUpdatedUtc')))}",
            f"Run:      #{run.get('runId')}",
        ]

        self._publisher.publish(
            title=f"Position closed · {side} {label} · {money(pnl)}",
            message="\n".join(lines),
            source="strategyrun",
            severity="success" if pnl > 0 else ("warning" if pnl < 0 else "info"),
            underlying=run.get("underlying"),
            symbol=position.get("symbol"),
            run_id=run.get("runId"),
        )

    def _alert_position_vanished(self, run: dict[str, Any], position_id: int) -> None:
        self._publisher.publish(
            title="Position closed",
            message=(
                f"⚪ <b>Position closed</b> — leg #{position_id} is no longer on the run.\n\n"
                f"Strategy: {esc(run.get('strategyName', '?'))} · {esc(run.get('underlying', '?'))}\n"
                f"Run:      #{run.get('runId')}\n\n"
                "<i>The leg left the book between polls, so no closing price is available.</i>"
            ),
            source="strategyrun",
            severity="info",
            underlying=run.get("underlying"),
            run_id=run.get("runId"),
        )

    # -- market data --------------------------------------------------------- #
    def _diff_ingestor(self) -> None:
        running = self._fetch_ingestor()
        if running is None or running == self._ingestor_running:
            return
        was = self._ingestor_running
        self._ingestor_running = running

        if was is None:
            return  # first reading is the baseline, not a transition

        if running:
            title = "Market data started"
            body = (
                "📡 <b>Market data started</b>\n\n"
                "The tick ingestor is running and feeding Redis."
            )
            severity = "success"
        else:
            title = "Market data stopped"
            body = (
                "🛑 <b>Market data stopped</b>\n\n"
                "The tick ingestor is no longer running. Live quotes and any "
                "running strategy will go stale until it is started again."
            )
            severity = "warning"

        self._publisher.publish(
            title=title,
            message=f"{body}\nAt: {ist(datetime.now(timezone.utc).isoformat())}",
            source="process",
            severity=severity,
        )


# --------------------------------------------------------------------------- #
# the forwarder
# --------------------------------------------------------------------------- #
# The API itself notifies on a run stopping (StrategyController's stop endpoint)
# and on a deploy, with a one-line message. The poller in this process reports
# the same two moments with the whole picture — net P&L, realized, trades,
# duration, reason — so forwarding both puts two messages in the group for one
# event. Drop the terse one and keep ours.
#
# Deliberately narrow: it must not swallow "Feed stalled — X on Y", "Feed
# recovered — X on Y" or the kill-switch alerts, which nothing here duplicates.
_SUPERSEDED_BY_POLLER = re.compile(r"\b(started|stopped) on \b")


def is_superseded(payload: dict[str, Any]) -> bool:
    source = str(payload.get("Source") or payload.get("source") or "").lower()
    if source != "strategyrun":
        return False
    title = str(payload.get("Title") or payload.get("title") or "")
    if title.startswith(("Strategy started", "Strategy stopped")):
        return False  # one of ours
    return bool(_SUPERSEDED_BY_POLLER.search(title))


def render_for_telegram(payload: dict[str, Any]) -> str:
    """
    Turn an alerts:new payload into a Telegram message.

    Messages this process publishes are already formatted HTML, so they pass
    through as-is. Anything published by the backend is plain text and gets a
    heading built from its title and severity.
    """
    title = payload.get("Title") or payload.get("title") or "Alert"
    message = payload.get("Message") or payload.get("message") or ""
    severity = str(payload.get("Severity") or payload.get("severity") or "info").lower()

    # Our own messages carry their own heading and markup.
    if message.lstrip().startswith(("▶️", "⏹️", "🟢", "🔴", "✅", "🔻", "⚪", "📡", "🛑")):
        return message

    icon = SEVERITY_ICON.get(severity, "ℹ️")
    parts = [f"{icon} <b>{esc(title)}</b>"]
    if message:
        parts += ["", esc(message)]

    context = []
    underlying = payload.get("Underlying") or payload.get("underlying")
    symbol = payload.get("Symbol") or payload.get("symbol")
    run_id = payload.get("SimulationRunId") or payload.get("simulationRunId")
    if underlying and underlying != "UNKNOWN":
        context.append(f"Underlying: {esc(underlying)}")
    if symbol:
        context.append(f"Symbol: {esc(symbol)}")
    if run_id:
        context.append(f"Run: #{run_id}")
    if context:
        parts += ["", "  ·  ".join(context)]

    return "\n".join(parts)


def forwarder_loop(
    config: Config,
    telegram: Telegram,
    stop: threading.Event,
) -> None:
    """Subscribe to alerts:new and send everything that arrives to Telegram."""
    while not stop.is_set():
        try:
            client = redis.Redis(
                host=config.redis_host,
                port=config.redis_port,
                db=config.redis_db,
                password=config.redis_password,
                decode_responses=True,
            )
            pubsub = client.pubsub(ignore_subscribe_messages=True)
            pubsub.subscribe(ALERT_CHANNEL)
            log.info("forwarder subscribed to %s", ALERT_CHANNEL)

            while not stop.is_set():
                message = pubsub.get_message(timeout=1.0)
                if not message or message.get("type") != "message":
                    continue
                try:
                    payload = json.loads(message["data"])
                except (ValueError, TypeError):
                    log.warning("ignoring a malformed alert payload")
                    continue
                if not isinstance(payload, dict):
                    continue
                title = payload.get("Title") or payload.get("title") or "Alert"
                if is_superseded(payload):
                    log.debug("suppressed (the poller reports this in full): %s", title)
                    continue
                if telegram.send(render_for_telegram(payload)):
                    log.info("delivered to Telegram: %s", title)
                else:
                    log.error("NOT delivered to Telegram: %s", title)
        except redis.RedisError as exc:
            if stop.is_set():
                return
            log.warning("forwarder lost Redis (%s); reconnecting in 5s", exc)
            stop.wait(5)
        except Exception:  # noqa: BLE001 - the forwarder must never die quietly
            log.exception("forwarder hit an unexpected error; restarting in 5s")
            stop.wait(5)


# --------------------------------------------------------------------------- #
# entry point
# --------------------------------------------------------------------------- #
def startup_summary(snapshot: dict[str, Any]) -> str:
    lines = [
        "🔔 <b>Notifier online</b>",
        "",
        "Watching for: strategy start/stop, position open/close, market data start/stop.",
        "",
        f"Live runs now: <b>{snapshot['active']}</b>   ·   open positions: <b>{snapshot['open_positions']}</b>",
    ]
    ingestor = snapshot.get("ingestor")
    if ingestor is not None:
        lines.append(f"Market data: {'running' if ingestor else 'stopped'}")

    for run in snapshot.get("active_runs", []):
        lines.append(
            f"  · #{run.get('runId')} {esc(run.get('strategyName'))} "
            f"· {esc(run.get('underlying'))} · {esc(money(run.get('netPnl')))}"
        )

    lines += ["", "<i>These were already running, so no start alerts were sent for them.</i>"]
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--interval", type=float, default=5.0, help="seconds between polls")
    parser.add_argument(
        "--reconcile",
        type=float,
        default=120.0,
        help="seconds before a live run's legs are re-read even if its counters did not move",
    )
    parser.add_argument("--once", action="store_true", help="baseline, summarise, exit")
    parser.add_argument("--dry-run", action="store_true", help="log instead of sending")
    parser.add_argument("--no-forward", action="store_true", help="do not forward others' events")
    parser.add_argument("--quiet-start", action="store_true", help="skip the startup summary")
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args(argv)

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s  %(levelname)-7s %(message)s",
        datefmt="%H:%M:%S",
    )
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8")
        except Exception:  # noqa: BLE001 - a plain console is not a failure
            pass

    config = Config(load_env(REPO_ROOT / ".env"))
    if not config.telegram_configured and not args.dry_run:
        log.error(
            "TELEGRAM_BOT_TOKEN / TELEGRAM_CHAT_ID are not set in .env — "
            "nothing could be delivered. Use --dry-run to test without them."
        )
        return 2

    redis_client = redis.Redis(
        host=config.redis_host,
        port=config.redis_port,
        db=config.redis_db,
        password=config.redis_password,
        decode_responses=True,
    )
    try:
        redis_client.ping()
    except redis.RedisError as exc:
        log.error("cannot reach Redis at %s:%s — %s", config.redis_host, config.redis_port, exc)
        return 2

    api = ApiClient(config)
    try:
        api.login()
    except Exception as exc:  # noqa: BLE001 - report clearly and stop
        log.error("cannot sign in to the API at %s — %s", config.api_base, exc)
        return 2

    telegram = Telegram(config, dry_run=args.dry_run)
    watcher = Watcher(api, AlertPublisher(redis_client), reconcile_seconds=args.reconcile)

    stop = threading.Event()

    def handle_signal(*_: Any) -> None:
        log.info("stopping…")
        stop.set()

    signal.signal(signal.SIGINT, handle_signal)
    try:
        signal.signal(signal.SIGTERM, handle_signal)
    except (AttributeError, ValueError):
        pass  # SIGTERM is not settable everywhere on Windows

    forwarder: threading.Thread | None = None
    if not args.no_forward:
        forwarder = threading.Thread(
            target=forwarder_loop, args=(config, telegram, stop), daemon=True
        )
        forwarder.start()
        # Let the subscription land before anything is published, so the
        # startup summary is not missed.
        time.sleep(1.0)

    snapshot = watcher.baseline()
    log.info(
        "baseline: %s runs (%s live), %s open positions, ingestor=%s",
        snapshot["runs"],
        snapshot["active"],
        snapshot["open_positions"],
        snapshot["ingestor"],
    )

    if not args.quiet_start:
        if args.no_forward:
            telegram.send(startup_summary(snapshot))
        else:
            AlertPublisher(redis_client).publish(
                title="Notifier online",
                message=startup_summary(snapshot),
                source="process",
                severity="info",
            )

    if args.once:
        # Give the forwarder room to pick the summary off the channel and get it
        # through Telegram's rate-limit gap before the process goes away.
        time.sleep(6)
        stop.set()
        if forwarder is not None:
            forwarder.join(timeout=5)
        return 0

    log.info(
        "polling every %.1fs, reconciling legs every %.0fs — Ctrl+C to stop",
        args.interval,
        args.reconcile,
    )
    ticks = 0
    began = time.monotonic()
    while not stop.is_set():
        started = time.monotonic()
        try:
            watcher.tick()
        except Exception:  # noqa: BLE001 - a bad tick must not end the process
            log.exception("poll failed; continuing")
        ticks += 1
        if ticks % 120 == 0:
            minutes = (time.monotonic() - began) / 60
            log.info(
                "%d polls in %.1f min · %d detail reads (the only calls that touch a run's gate)",
                ticks,
                minutes,
                watcher.detail_calls,
            )
        elapsed = time.monotonic() - started
        stop.wait(max(0.5, args.interval - elapsed))

    if forwarder is not None:
        forwarder.join(timeout=3)
    log.info("stopped")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
