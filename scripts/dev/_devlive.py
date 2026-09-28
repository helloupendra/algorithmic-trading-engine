"""
Shared by scripts/dev/replay-ticks.py and scripts/dev/seed-positions.py.

What the two agree on: where the local dev stack keeps its state (.dev-live/,
written by scripts/dev/local-live.sh up), how to sign in to its API, the small
synthetic instrument universe the replay prices and the seed trades, and the
price model behind both.

Standard library only, on purpose. It runs on any python3 without the engine's
virtualenv, and it never imports the engine: the engine loads the repo-root
.env on import, and on the owner's Mac that file holds the real broker keys.

Every call goes to a loopback address and through an opener with proxies
switched off, so neither a typo nor an HTTP_PROXY in the shell can send a fake
tick anywhere but the dev API.
"""

from __future__ import annotations

import json
import math
import os
import random
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import asdict, dataclass, field
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from typing import Any

REPO = Path(__file__).resolve().parents[2]
# LOCAL_LIVE_DIR moves the whole stack's state, for local-live.sh's own tests.
DEV_DIR = Path(os.environ.get("LOCAL_LIVE_DIR") or REPO / ".dev-live")
STATE_FILE = DEV_DIR / "state.env"
UNIVERSE_FILE = DEV_DIR / "universe.json"

IST = timezone(timedelta(hours=5, minutes=30))
LOOPBACK_HOSTS = {"127.0.0.1", "localhost", "::1"}

#: What the synthetic ticks carry as their vendor. Not a real connector's key,
#: so nothing in the console can mistake them for Dhan or FYERS.
SOURCE_KEY = "devreplay"

#: Stored as each tick's raw payload: a synthetic price says so in the table.
RAW_PAYLOAD = json.dumps({"synthetic": True, "by": "scripts/dev/replay-ticks.py"}, separators=(",", ":"))

#: Starting levels, near where the markets were in late September 2026 (the
#: middle of the strike ladders in the FYERS masters of 10 Sep). Overridable
#: with --spot on the seed's --setup.
DEFAULT_SPOTS = {
    "NIFTY": 23650.0,
    "BANKNIFTY": 54400.0,
    "SENSEX": 77600.0,
    "CRUDEOIL": 7850.0,
}

#: Annualised volatility of each random walk, and the options' implied vol.
VOLS = {"NIFTY": 0.13, "BANKNIFTY": 0.16, "SENSEX": 0.13, "CRUDEOIL": 0.35}
NIFTY_IV = 0.12

#: The lot sizes the platform uses (appsettings.json LotSizes).
LOT_SIZES = {"NIFTY": 65, "BANKNIFTY": 30, "SENSEX": 20, "CRUDEOIL": 100}

_MONTHS = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"]
_WEEKLY_MONTH_CODES = {**{m: str(m) for m in range(1, 10)}, 10: "O", 11: "N", 12: "D"}


# --------------------------------------------------------------------- state --

def load_state() -> dict[str, str]:
    """The KEY=VALUE pairs local-live.sh up wrote: ports, users, passwords."""
    if not STATE_FILE.is_file():
        raise SystemExit(
            f"No dev stack state at {STATE_FILE}. "
            "Start the stack first: scripts/dev/local-live.sh up"
        )
    values: dict[str, str] = {}
    for raw in STATE_FILE.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, value = line.partition("=")
        values[key.strip()] = value.strip()
    return values


def require_loopback(url: str) -> None:
    """Refuses any address that is not this machine."""
    host = urllib.parse.urlsplit(url).hostname or ""
    if host not in LOOPBACK_HOSTS:
        raise SystemExit(f"Refusing {url}: the dev scripts only talk to this machine (127.0.0.1).")


# ----------------------------------------------------------------------- api --

class ApiError(Exception):
    def __init__(self, status: int, body: str, path: str) -> None:
        super().__init__(f"{status} from {path}: {body[:400]}")
        self.status = status
        self.body = body


class DevApi:
    """A signed-in client for the dev API. Re-signs in once on a 401."""

    def __init__(self, base_url: str, username: str, password: str, timeout: float = 20.0) -> None:
        require_loopback(base_url)
        self.base_url = base_url.rstrip("/")
        self.username = username
        self._password = password
        self._timeout = timeout
        self._token: str | None = None
        # No proxy, whatever the shell has set: these calls stay on loopback.
        self._opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    @classmethod
    def as_role(cls, state: dict[str, str], role: str) -> "DevApi":
        """admin, trader or service, with the credentials local-live.sh generated."""
        prefix = {"admin": "ADMIN", "trader": "TRADER", "service": "SERVICE"}[role]
        return cls(state["API_URL"], state[f"{prefix}_USER"], state[f"{prefix}_PASSWORD"])

    def login(self) -> None:
        answer = self._send("POST", "/api/UserAuth/login",
                            {"userNameOrEmail": self.username, "password": self._password}, auth=False)
        self._token = answer["accessToken"]

    def _send(self, method: str, path: str, body: Any = None, *, auth: bool = True) -> Any:
        data = None if body is None else json.dumps(body).encode("utf-8")
        request = urllib.request.Request(self.base_url + path, data=data, method=method)
        request.add_header("Accept", "application/json")
        if data is not None:
            request.add_header("Content-Type", "application/json")
        if auth and self._token:
            request.add_header("Authorization", f"Bearer {self._token}")
        try:
            with self._opener.open(request, timeout=self._timeout) as response:
                raw = response.read().decode("utf-8")
        except urllib.error.HTTPError as error:
            raise ApiError(error.code, error.read().decode("utf-8", "replace"), path) from None
        return json.loads(raw) if raw.strip() else None

    def call(self, method: str, path: str, body: Any = None) -> Any:
        if self._token is None:
            self.login()
        try:
            return self._send(method, path, body)
        except ApiError as error:
            if error.status != 401:
                raise
            self.login()
            return self._send(method, path, body)

    def get(self, path: str) -> Any:
        return self.call("GET", path)

    def post(self, path: str, body: Any = None) -> Any:
        return self.call("POST", path, body if body is not None else {})

    def put(self, path: str, body: Any) -> Any:
        return self.call("PUT", path, body)


def api_is_up(base_url: str, timeout: float = 3.0) -> bool:
    require_loopback(base_url)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(base_url.rstrip("/") + "/api/Backend/status", timeout=timeout) as response:
            return response.status == 200
    except (urllib.error.URLError, OSError):
        return False


# ------------------------------------------------------------------ universe --

@dataclass
class Instrument:
    symbol: str
    kind: str                 # "index", "option" or "future"
    exchange: str             # NSE, BSE, MCX
    underlying: str           # NIFTY, BANKNIFTY, SENSEX, CRUDEOIL
    description: str
    tick: float
    start_price: float = 0.0  # indices and futures; an option is priced off its spot
    strike: float | None = None
    option_type: str | None = None
    expiry: str | None = None  # ISO date
    spot_symbol: str | None = None

    @property
    def lot_size(self) -> int:
        return LOT_SIZES.get(self.underlying, 1)

    @property
    def expiry_date(self) -> date | None:
        return date.fromisoformat(self.expiry) if self.expiry else None


def today_ist() -> date:
    return datetime.now(IST).date()


def next_nifty_expiry(today: date) -> date:
    """The first Tuesday after today (NIFTY options expire on Tuesdays).

    Strictly after, so a contract never expires under a session being tested.
    Exchange holidays that move an expiry are ignored: this is a synthetic
    master, and it only has to agree with itself.
    """
    days = (1 - today.weekday()) % 7 or 7
    return today + timedelta(days=days)


def is_monthly_expiry(expiry: date) -> bool:
    """The last Tuesday of its month, which FYERS spells as a monthly contract."""
    return (expiry + timedelta(days=7)).month != expiry.month


def nifty_option_symbol(expiry: date, strike: int, option_type: str) -> str:
    """FYERS's spelling: NSE:NIFTY26SEP23600CE (monthly), NSE:NIFTY26O0623600CE (weekly)."""
    yy = f"{expiry.year % 100:02d}"
    if is_monthly_expiry(expiry):
        return f"NSE:NIFTY{yy}{_MONTHS[expiry.month - 1]}{strike}{option_type}"
    return f"NSE:NIFTY{yy}{_WEEKLY_MONTH_CODES[expiry.month]}{expiry.day:02d}{strike}{option_type}"


def crude_future_expiry(today: date) -> date:
    """The near CRUDEOIL future: the 19th, this month or the next (MCX: 19 Oct 26, 19 Nov 26)."""
    this_month = date(today.year, today.month, 19)
    if today < this_month:
        return this_month
    year, month = (today.year + 1, 1) if today.month == 12 else (today.year, today.month + 1)
    return date(year, month, 19)


def _desc_date(d: date) -> str:
    return f"{d.day:02d} {d.strftime('%b')} {d.year % 100:02d}"


def build_universe(today: date, spots: dict[str, float] | None = None) -> list[Instrument]:
    """Three indices, NIFTY options at five strikes around the money, one crude future."""
    spots = {**DEFAULT_SPOTS, **(spots or {})}
    universe = [
        Instrument("NSE:NIFTY50-INDEX", "index", "NSE", "NIFTY", "NIFTY50-INDEX", 0.05, spots["NIFTY"]),
        Instrument("NSE:NIFTYBANK-INDEX", "index", "NSE", "BANKNIFTY", "NIFTYBANK-INDEX", 0.05, spots["BANKNIFTY"]),
        Instrument("BSE:SENSEX-INDEX", "index", "BSE", "SENSEX", "SENSEX-INDEX", 0.01, spots["SENSEX"]),
    ]

    expiry = next_nifty_expiry(today)
    atm = int(round(spots["NIFTY"] / 50.0) * 50)
    for strike in range(atm - 100, atm + 101, 50):
        for option_type in ("CE", "PE"):
            universe.append(Instrument(
                symbol=nifty_option_symbol(expiry, strike, option_type),
                kind="option", exchange="NSE", underlying="NIFTY",
                description=f"NIFTY {_desc_date(expiry)} {strike} {option_type}",
                tick=0.05, strike=float(strike), option_type=option_type,
                expiry=expiry.isoformat(), spot_symbol="NSE:NIFTY50-INDEX"))

    crude_expiry = crude_future_expiry(today)
    universe.append(Instrument(
        symbol=f"MCX:CRUDEOIL{crude_expiry.year % 100:02d}{_MONTHS[crude_expiry.month - 1]}FUT",
        kind="future", exchange="MCX", underlying="CRUDEOIL",
        description=f"CRUDEOIL {_desc_date(crude_expiry)} FUT",
        tick=1.0, start_price=spots["CRUDEOIL"], expiry=crude_expiry.isoformat()))
    return universe


def save_universe(universe: list[Instrument]) -> None:
    DEV_DIR.mkdir(parents=True, exist_ok=True)
    UNIVERSE_FILE.write_text(json.dumps([asdict(x) for x in universe], indent=2) + "\n", encoding="utf-8")


def load_universe() -> list[Instrument]:
    """What --setup loaded into the dev API; built for today if it has not run."""
    if UNIVERSE_FILE.is_file():
        return [Instrument(**row) for row in json.loads(UNIVERSE_FILE.read_text(encoding="utf-8"))]
    return build_universe(today_ist())


def atm_strike(universe: list[Instrument]) -> float:
    strikes = sorted({x.strike for x in universe if x.kind == "option" and x.strike is not None})
    return strikes[len(strikes) // 2]


def find_option(universe: list[Instrument], strike: float, option_type: str) -> Instrument:
    for x in universe:
        if x.kind == "option" and x.strike == strike and x.option_type == option_type:
            return x
    raise SystemExit(f"No NIFTY {strike:.0f} {option_type} in the dev universe.")


# ------------------------------------------------------------------- masters --

def _expiry_epoch(expiry: date, close: tuple[int, int]) -> int:
    moment = datetime(expiry.year, expiry.month, expiry.day, close[0], close[1], tzinfo=IST)
    return int(moment.timestamp())


def write_masters(universe: list[Instrument], directory: Path) -> list[Path]:
    """The universe as FYERS symbol-master CSVs, the format the API's import-local reads.

    Columns as FYERS publishes them (21 of them); the importer reads 1
    description, 3 lot size, 4 tick size, 9 symbol, 13 underlying, 15 strike
    and 16 option type, and takes the segment from the file name.
    """
    directory.mkdir(parents=True, exist_ok=True)
    updated = date.today().isoformat()
    rows: dict[str, list[str]] = {"NSE_CM": [], "BSE_CM": [], "NSE_FO": [], "MCX_COM": []}
    for n, x in enumerate(universe, start=1):
        token = f"9{n:06d}"
        name = x.symbol.split(":", 1)[1]
        if x.kind == "index":
            fields = [token, name, "10", "0", f"{x.tick}", "", "0915-1530|1815-1915:", updated, "",
                      x.symbol, "10", "10", token, x.underlying, token, "-1.0", "XX", token, "None", "0", "0.0"]
            rows["BSE_CM" if x.exchange == "BSE" else "NSE_CM"].append(",".join(fields))
        elif x.kind == "option":
            epoch = _expiry_epoch(x.expiry_date, (15, 30))
            fields = [token, x.description, "14", str(x.lot_size), f"{x.tick}", "", "0915-1540|1815-1915:", updated,
                      str(epoch), x.symbol, "10", "11", token, x.underlying, "26000", f"{x.strike:.1f}",
                      x.option_type or "XX", "101000000026000", "None", "0", "0.0"]
            rows["NSE_FO"].append(",".join(fields))
        else:
            epoch = _expiry_epoch(x.expiry_date, (23, 30))
            # The real MCX master says 1 for every lot size; the importer
            # ignores it and the API takes 100 from its LotSizes table.
            fields = [token, x.description, "30", "1", f"{x.tick}", "", "0900-2330|1815-1915:", updated,
                      str(epoch), x.symbol, "11", "20", token, x.underlying, "294", "-1.0", "XX",
                      "1120000000294", str(epoch), "0", "0.0"]
            rows["MCX_COM"].append(",".join(fields))

    written = []
    for name, lines in rows.items():
        path = directory / f"{name}.csv"
        path.write_text("\n".join(lines) + "\n", encoding="utf-8")
        written.append(path)
    return written


# --------------------------------------------------------------- price model --

_TRADING_SECONDS_PER_YEAR = 252 * 6.25 * 3600
_INDEX_CORRELATION = 0.85


def _norm_cdf(x: float) -> float:
    return 0.5 * (1.0 + math.erf(x / math.sqrt(2.0)))


def black_scholes(spot: float, strike: float, years: float, vol: float, option_type: str) -> float:
    if years <= 0 or vol <= 0:
        return max(0.0, spot - strike) if option_type == "CE" else max(0.0, strike - spot)
    root = vol * math.sqrt(years)
    d1 = (math.log(spot / strike) + 0.5 * vol * vol * years) / root
    d2 = d1 - root
    if option_type == "CE":
        return spot * _norm_cdf(d1) - strike * _norm_cdf(d2)
    return strike * _norm_cdf(-d2) - spot * _norm_cdf(-d1)


def to_tick(price: float, tick: float) -> float:
    return round(max(tick, round(price / tick) * tick), 2)


def utc_stamp(moment: datetime) -> str:
    return moment.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


@dataclass
class _Book:
    open: float | None = None
    high: float | None = None
    low: float | None = None
    prev_close: float | None = None
    volume: int = 0
    oi: int = 0
    last: float | None = None


@dataclass
class Market:
    """Correlated random walks for the indices and crude; NIFTY options priced off NIFTY.

    Volatility is real-world size per wall-clock second (NIFTY about a point a
    second), times vol_mult for a livelier screen.
    """

    universe: list[Instrument]
    vol_mult: float = 1.0
    rng: random.Random = field(default_factory=random.Random)
    spots: dict[str, float] = field(default_factory=dict)
    books: dict[str, _Book] = field(default_factory=dict)
    last_step: datetime | None = None

    def __post_init__(self) -> None:
        for x in self.universe:
            if x.kind != "option":
                self.spots.setdefault(x.underlying, x.start_price)
            self.books[x.symbol] = _Book(oi=self.rng.randrange(50_000, 400_000) * (x.lot_size if x.kind == "option" else 1))

    def start_from(self, latest: dict[str, float]) -> None:
        """Continue from prices the API already holds (a restarted replay does not jump)."""
        for x in self.universe:
            if x.kind != "option" and latest.get(x.symbol):
                self.spots[x.underlying] = float(latest[x.symbol])

    def step(self, now: datetime) -> None:
        if self.last_step is None:
            self.last_step = now
            return
        seconds = min(5.0, max(0.0, (now - self.last_step).total_seconds()))
        self.last_step = now
        if seconds == 0:
            return
        root = math.sqrt(seconds / _TRADING_SECONDS_PER_YEAR) * self.vol_mult
        common = self.rng.gauss(0.0, 1.0)
        for name in list(self.spots):
            own = self.rng.gauss(0.0, 1.0)
            shock = own if name == "CRUDEOIL" else (
                _INDEX_CORRELATION * common + math.sqrt(1 - _INDEX_CORRELATION ** 2) * own)
            vol = VOLS.get(name, 0.15)
            self.spots[name] *= math.exp(vol * root * shock - 0.5 * (vol * root) ** 2)

    def price(self, x: Instrument, now: datetime) -> float:
        if x.kind != "option":
            return to_tick(self.spots[x.underlying], x.tick)
        spot = self.spots[x.underlying]
        close = datetime.combine(x.expiry_date, datetime.min.time(), IST).replace(hour=15, minute=30)
        years = max(3600.0, (close - now).total_seconds()) / (365.0 * 86400.0)
        smile = NIFTY_IV + 0.35 * abs(math.log(x.strike / spot))
        return to_tick(black_scholes(spot, x.strike or spot, years, smile, x.option_type or "CE"), x.tick)

    def tick(self, x: Instrument, now: datetime) -> dict[str, Any]:
        """One tick as the ingestor posts it to /api/LiveData/ticks/upsert-batch."""
        ltp = self.price(x, now)
        book = self.books[x.symbol]
        if book.open is None:
            book.open = book.high = book.low = ltp
            # Yesterday's close a little away from here, so the day's change is not zero.
            book.prev_close = to_tick(ltp * (1 + self.rng.uniform(-0.006, 0.006)), x.tick)
        book.high = max(book.high or ltp, ltp)
        book.low = min(book.low or ltp, ltp)
        book.last = ltp

        tick: dict[str, Any] = {
            "symbol": x.symbol,
            "dataType": "symbolUpdate",
            "exchangeTimestampUtc": utc_stamp(now),
            "lastTradedPrice": ltp,
            "open": book.open,
            "high": book.high,
            "low": book.low,
            "prevClose": book.prev_close,
            "rawPayload": RAW_PAYLOAD,
            "sourceKey": SOURCE_KEY,
            "isReplay": False,
        }
        if x.kind != "index":
            # Indices are not traded: the real feeds send them without a book.
            half = max(x.tick, to_tick(ltp * 0.0015, x.tick)) if x.kind == "option" else x.tick
            book.volume += self.rng.randrange(1, 40) * (x.lot_size if x.kind == "option" else 1)
            book.oi = max(0, book.oi + self.rng.randrange(-5, 6) * (x.lot_size if x.kind == "option" else 1))
            tick.update({
                "bidPrice": to_tick(ltp - half, x.tick),
                "askPrice": to_tick(ltp + half, x.tick),
                "bidSize": self.rng.randrange(1, 60) * (x.lot_size if x.kind == "option" else 1),
                "askSize": self.rng.randrange(1, 60) * (x.lot_size if x.kind == "option" else 1),
                "volume": book.volume,
                "openInterest": book.oi,
            })
        return tick
