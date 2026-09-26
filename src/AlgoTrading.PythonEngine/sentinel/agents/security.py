"""
The security agent: who got in, what they changed, and what is left open.

Every five minutes it reads the platform's own activity log and the machine:

* ``login-failures`` (HIGH): more than 15 failed sign-ins in 15 minutes from
  one address, or more than 10 spread over at least three addresses. Source:
  the activity log (GET /api/ActivityLog, action ``create:userauth-login``,
  ``succeeded=false``). A wrong password is recorded there with the caller's
  real address (the API trusts X-Forwarded-For from the tunnel). Attempts the
  per-address limiter refuses with 429 are *not* recorded — the limiter runs
  before the activity-log middleware — but it lets ten a minute through, so a
  script crosses 15 in under two minutes while a person mistyping rarely
  does. IPv6 callers are grouped by /64, because one household or attacker
  holds a whole /64. An address that signed in successfully before its
  failures began is someone on the desk with an old password: LOW, not an
  attack. Sign-ins from this machine that keep failing (more than 5) are a
  runner with a wrong password. Once crossed, the incident is held open for
  30 minutes after the last failure, so a guess that pauses and resumes stays
  one incident instead of a new alarm on every crossing.
* ``privileged-change`` (MEDIUM): an account created or deleted, a role or
  grant changed, a password reset, an invite issued or used, a broker account
  issued or its credentials revealed, a kill switch pulled, connector
  credentials replaced — one notice per activity-log entry.
* ``ssh-login``: decided by the key, not the address — the owner's one key
  comes from a different home address most days. Source: the sshd lines in
  /var/log/auth.log (readable by the ``adm`` group), which see every session,
  a key used to run a single command included. MEDIUM for a key never seen
  before; HIGH for a login by password or keyboard-interactive (this server
  should accept keys only). A known key from a new address says nothing. The
  first check learns the keys already in use from all of auth.log's history
  (rotated and gzipped files included) without reporting them. Keys in
  SENTINEL_SSH_KEYS (SHA256 fingerprints, comma separated) and logins from
  SENTINEL_SSH_ALLOWED (addresses or CIDR ranges) or from EC2 Instance
  Connect (13.233.177.0/29, a fresh key per session) are known. A log that
  exists but cannot be read is one LOW finding: SSH logins are unwatched.
* ``open-port`` (HIGH): a TCP listener, or a Docker-published port, on a
  public address and a port not in SENTINEL_PUBLIC_PORTS (default 22).
* ``secret-file-permissions`` (HIGH): .env or an appsettings.Local.json that
  other users on the machine can open.
* ``secret-in-git`` (CRITICAL, once a day): a private key, AWS key, Telegram
  bot token, JWT or literal password in a tracked file. The evidence names
  file:line only — the matched text is read, judged and dropped. Tests and
  docs are scanned too — a vendor access token pasted into a doc example or a
  fixture is exactly the leak to catch, and docs/ is published — except for
  the password pattern, which there is mostly prose and sample config. A
  fixture that needs a token-shaped string carries ``pragma: allowlist
  secret`` on its line, or is listed in SENTINEL_SECRET_ALLOW (``path:line``
  or ``path``, comma separated).
* ``vulnerable-dependency`` (MEDIUM, HIGH when critical; once a week, outside
  market hours): ``dotnet list package --vulnerable`` for the API and
  ``npm audit`` for the console, one finding per vulnerable package.
* ``fail2ban`` (LOW): more than 20 new sshd bans between two checks, when the
  jail is readable at all (it needs root; without it the rule is silent).

Resolution: this agent resolves after one clean check. The notices
(privileged-change, the ssh-login events, fail2ban) are reported once, carry
``extra={"notice": True}`` and close on the next check — the engine should
close those without a "resolved" message, since nothing was ever open. The
standing conditions are reported on every check while they last — the slow
scans replay their last result until the next scan, login-failures holds for
30 minutes — so one clean check really does mean the condition is gone. When a
source cannot be read (the API is down, ``ss`` failed), the previous findings
of that rule are carried forward instead: "could not look" is not "fixed".
"""
from __future__ import annotations

import gzip
import ipaddress
import json
import os
import re
import stat
import traceback
import zlib
from dataclasses import asdict, dataclass, replace
from datetime import datetime, time, timedelta, timezone
from pathlib import Path
from typing import Any, Callable, Iterable, Optional
from urllib.parse import urlencode

from sentinel.agents.base import Agent
from sentinel.clock import IST, ist_date, to_ist
from sentinel.context import SentinelContext
from sentinel.model import Finding, Severity

AGENT = "security"

FIRST_LOOKBACK = timedelta(minutes=15)   # how far back the very first check looks

LOGIN_WINDOW = timedelta(minutes=15)
# More than this many from one address (or IPv6 /64) in the window. The limiter lets a script through at ten a
# minute, so it crosses this in under two; a person mistyping (7 in a minute on 23 Sep, the owner) does not.
LOGIN_PER_ADDRESS_LIMIT = 15
LOGIN_TOTAL_LIMIT = 10          # more than this many, spread over addresses that are each below the limit…
SPREAD_MIN_ADDRESSES = 3        # …and over at least this many of them
LOOPBACK_LIMIT = 5              # this machine: never an attacker, so a smaller number already means a bad password
LOGIN_SUCCESS_LOOKBACK = timedelta(hours=24)   # a success this recent, before the failures, makes an address known
LOGIN_HOLD = timedelta(minutes=30)             # an incident stays open this long after the last failure over the bar
NOTICE = {"notice": True}       # Finding.extra of a one-off event: nothing stays open, so nothing "resolves"

ACTIVITY_MODULES = ("users", "auth", "risk", "connectors", "other")
ACTIVITY_PAGE = 500
ACTIVITY_MAX_PAGES = 4
ACTIVITY_MARGIN = timedelta(minutes=2)    # re-read a little of the previous window; ids dedupe
ACTIVITY_MAX_WINDOW = timedelta(hours=24)
PRIVILEGED_PER_CHECK = 15                 # beyond this, one roll-up notice instead of a flood

SECRET_FILES = (
    ".env",
    "src/AlgoTrading.Api/appsettings.Local.json",
    "src/AlgoTrading.Worker.MarketData/appsettings.Local.json",
)

DEFAULT_AUTH_LOGS = ("/var/log/auth.log", "/var/log/auth.log.1", "/var/log/secure")
AUTH_LOG_TAIL_BYTES = 1024 * 1024
AUTH_LOG_MARGIN = timedelta(minutes=2)   # re-read a little of the last window; syslog can lag a line
EC2_INSTANCE_CONNECT = "13.233.177.0/29"  # ap-south-1's EC2 Instance Connect: a fresh key on every session
KNOWN_KEYS_MAX = 1000

GIT_SCAN_MAX_FILES = 10
DEPENDENCY_SCAN_EVERY = timedelta(days=7)
DEPENDENCY_RETRY_AFTER = timedelta(hours=6)
MARKET_WINDOW = (time(9, 0), time(15, 45))

FAIL2BAN_JUMP = 20


# ─── small helpers ────────────────────────────────────────────────────────────

def _iso(moment: datetime) -> str:
    return moment.astimezone(timezone.utc).isoformat()


def _utc_param(moment: datetime) -> str:
    """The form the API binds as a UTC DateTime."""
    return moment.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _parse_time(value: Any) -> Optional[datetime]:
    if not isinstance(value, str) or not value.strip():
        return None
    text = value.strip().replace("Z", "+00:00")
    try:
        moment = datetime.fromisoformat(text)
    except ValueError:
        return None
    return moment if moment.tzinfo else moment.replace(tzinfo=timezone.utc)


def _ist(moment: datetime) -> str:
    return to_ist(moment).strftime("%d %b %H:%M IST").lstrip("0")


def _dump(finding: Finding) -> dict:
    data = asdict(finding)
    data["severity"] = finding.severity.value
    return data


def _load(data: dict) -> Optional[Finding]:
    try:
        return Finding(**{**data, "severity": Severity(data["severity"])})
    except (TypeError, ValueError, KeyError):
        return None


def _carry(st: dict, key: str) -> list[Finding]:
    """The last findings of a rule whose source could not be read this time."""
    return [f for f in (_load(d) for d in st.get(key, []) if isinstance(d, dict)) if f is not None]


def _remember(st: dict, key: str, findings: list[Finding]) -> list[Finding]:
    st[key] = [_dump(f) for f in findings]
    return findings


def _ip(text: str) -> Optional[ipaddress.IPv4Address | ipaddress.IPv6Address]:
    """An address as written by ss, last, sshd or the API — brackets, zone and IPv4 mapping removed."""
    try:
        ip = ipaddress.ip_address(text.strip().strip("[]").split("%", 1)[0])
    except ValueError:
        return None
    if ip.version == 6 and ip.ipv4_mapped is not None:
        return ip.ipv4_mapped
    return ip


def _address_group(raw: Any) -> tuple[str, bool]:
    """(label, is_this_machine) — IPv6 grouped by /64, loopback named for what it is."""
    if not isinstance(raw, str) or not raw.strip():
        return "an unknown address", False
    ip = _ip(raw)
    if ip is None:
        return raw.strip()[:64], False
    if ip.is_loopback:
        return "this machine (loopback)", True
    if ip.version == 6:
        return str(ipaddress.ip_network(f"{ip}/64", strict=False)), False
    return str(ip), False


def _networks(spec: str) -> list[ipaddress.IPv4Network | ipaddress.IPv6Network]:
    out = []
    for item in (spec or "").split(","):
        item = item.strip()
        if not item:
            continue
        try:
            out.append(ipaddress.ip_network(item, strict=False))
        except ValueError:
            continue
    return out


def _in_networks(address: str, networks: list) -> bool:
    ip = _ip(address)
    return ip is not None and any(ip.version == n.version and ip in n for n in networks)


def _ports(spec: str) -> set[int]:
    return {int(p) for p in re.split(r"[,\s]+", spec or "") if p.isdigit()}


# ─── privileged changes: what counts, and how to say it ───────────────────────

@dataclass(frozen=True)
class _Change:
    kind: str
    method: str
    pattern: re.Pattern
    title: str               # formatted with {actor} and {m} (the path's captured part)
    shown_path: str = ""     # replaces the path in the evidence when the path carries a secret
    suggestion: str = ""


_IF_NOT_YOU = ("Informational — nothing to do if this was you or a colleague. If nobody on the desk did it: "
               "sign that account out everywhere (Users → revoke sessions), reset the admin password, and read "
               "the Activity log around this time.")

_CHANGES = (
    _Change("account-created", "POST", re.compile(r"^/api/userauth/register/?$", re.I),
            "{actor} created a new account"),
    _Change("invite-accepted", "POST", re.compile(r"^/api/invites/[^/]+/accept/?$", re.I),
            "An invitation was used to create a new account", shown_path="/api/Invites/…/accept"),
    _Change("account-deleted", "DELETE",
            re.compile(r"^/api/userauth/(?!login$|register$|refresh$|logout$)([^/]+)/?$", re.I),
            "{actor} deleted the account {m}"),
    _Change("account-changed", "PATCH", re.compile(r"^/api/users/(\d+)/?$", re.I),
            "{actor} changed account #{m} (role, active, capital or run limit)"),
    _Change("grants-changed", "PUT", re.compile(r"^/api/users/(\d+)/grants/?$", re.I),
            "{actor} changed the module grants of account #{m}"),
    _Change("password-reset", "POST", re.compile(r"^/api/users/(\d+)/password/?$", re.I),
            "{actor} reset the password of account #{m}"),
    _Change("own-password-changed", "POST", re.compile(r"^/api/users/me/password/?$", re.I),
            "{actor} changed their own password"),
    _Change("invite-created", "POST", re.compile(r"^/api/invites/?$", re.I),
            "{actor} issued an invitation to create an account"),
    _Change("broker-account-issued", "POST", re.compile(r"^/api/simbroker/accounts/(\d+)/?$", re.I),
            "{actor} issued a simulated-broker account to account #{m}"),
    _Change("broker-credentials-revealed", "POST", re.compile(r"^/api/simbroker/accounts/(\d+)/credentials/?$", re.I),
            "{actor} revealed the broker credentials of account #{m}"),
    _Change("broker-kill-switch", "POST", re.compile(r"^/api/simbroker/accounts/(\d+)/kill-switch/?$", re.I),
            "{actor} switched the broker kill switch of account #{m}",
            suggestion="If this was not deliberate: that trader's working orders were cancelled — check the "
                       "account on the Users page before the next order."),
    _Change("kill-switch", "POST", re.compile(r"^/api/risk/killswitch/(activate|deactivate)/?$", re.I),
            "{actor} {m}d the global kill switch",
            suggestion="If this was not deliberate: activating pauses every strategy and flattens every position — "
                       "read the Risk page before restarting runs."),
    _Change("connector-credentials", "PUT", re.compile(r"^/api/providers/([^/]+)/credentials/?$", re.I),
            "{actor} replaced the {m} connector's credentials"),
    _Change("broker-config", "PUT", re.compile(r"^/api/auth/broker-config/?$", re.I),
            "{actor} changed the broker configuration"),
)


def classify_change(method: str, path: str) -> Optional[tuple[_Change, str]]:
    for change in _CHANGES:
        if method.upper() != change.method:
            continue
        match = change.pattern.match(path or "")
        if match:
            return change, (match.group(1) if match.groups() else "")
    return None


# ─── ssh logins ───────────────────────────────────────────────────────────────

@dataclass(frozen=True)
class _Login:
    when: datetime
    user: str
    address: str
    method: str          # "publickey", "password", "keyboard-interactive/pam", …
    key: str = ""        # "SHA256:…" for a key login
    key_type: str = ""   # "RSA", "ED25519", …

    @property
    def by_password(self) -> bool:
        return self.method == "password" or self.method.startswith("keyboard-interactive")

    def describe(self) -> str:
        how = f"{self.key_type} key {self.key}" if self.key else self.method
        return f"{self.user} from {self.address} at {_ist(self.when)} — {how}"


_MONTHS = {m: i for i, m in enumerate(("Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct",
                                        "Nov", "Dec"), start=1)}

_AUTH_ACCEPTED = re.compile(
    r"^(?:(?P<iso>\d{4}-\d{2}-\d{2}T\S+)|(?P<mon>\w{3})\s+(?P<day>\d{1,2})\s+(?P<hms>\d{2}:\d{2}:\d{2}))\s+\S+\s+"
    r"sshd(?:-session)?\[\d+\]:\s+Accepted (?P<how>\S+) for (?P<user>\S+) from (?P<addr>\S+) port \d+"
    r"(?:\s+ssh2)?(?::\s+(?P<keytype>\S+)\s+(?P<fp>SHA256:\S+))?")


def _classic_time(mon: str, day: str, hms: str, now: datetime) -> Optional[datetime]:
    """A syslog time without a year, read as IST, in the year that keeps it out of the future."""
    month = _MONTHS.get(mon[:3].title())
    if month is None:
        return None
    parts = [int(p) for p in hms.split(":")]
    year = to_ist(now).year
    try:
        moment = datetime(year, month, int(day), *parts, tzinfo=IST)
        if moment > now + timedelta(days=1):
            moment = moment.replace(year=year - 1)
    except ValueError:
        return None
    return moment


def _is_remote_ip(host: str) -> bool:
    ip = _ip(host)
    return ip is not None and not (ip.is_unspecified or ip.is_loopback)


def parse_auth_log(lines: Iterable[str], now: datetime) -> list[_Login]:
    """Every accepted sshd login from a real address, with the key that opened it."""
    logins = []
    for line in lines:
        m = _AUTH_ACCEPTED.match(line) if "Accepted " in line else None
        if not m:
            continue
        when = _parse_time(m.group("iso")) if m.group("iso") else _classic_time(
            m.group("mon"), m.group("day"), m.group("hms"), now)
        if when is None or not _is_remote_ip(m.group("addr")):
            continue
        logins.append(_Login(when, m.group("user"), m.group("addr"), m.group("how"),
                             m.group("fp") or "", m.group("keytype") or ""))
    return logins


def _tail_lines(path: Path, max_bytes: int = AUTH_LOG_TAIL_BYTES) -> list[str]:
    """The end of a log. Raises OSError (PermissionError when it exists but is not ours to read)."""
    with open(path, "rb") as fh:
        fh.seek(0, os.SEEK_END)
        size = fh.tell()
        fh.seek(max(0, size - max_bytes))
        data = fh.read()
    lines = data.decode("utf-8", "replace").splitlines()
    return lines[1:] if size > max_bytes else lines


def _accepted_lines(path: Path) -> list[str]:
    """Every 'Accepted' line of a whole log, gzipped or not — for learning the keys already in use."""
    opener = gzip.open if path.suffix == ".gz" else open
    with opener(path, "rb") as fh:
        return [raw.decode("utf-8", "replace") for raw in fh if b"Accepted " in raw]


def _key_id(fingerprint: str) -> str:
    """'SHA256:RYSLVz…' → 'RYSLVz…': the part a person pastes, with or without the prefix."""
    return fingerprint.strip().split(":", 1)[-1] if fingerprint.strip().upper().startswith("SHA256:") \
        else fingerprint.strip()


def _key_listed(fingerprint: str, listed: list[str]) -> bool:
    key = _key_id(fingerprint)
    return any(len(item) >= 8 and key.startswith(item) for item in listed)


def _learn_key(known: dict[str, str], login: _Login, instance_connect: list) -> None:
    """Remember a key and when it was last used. EC2 Instance Connect's one-session keys are not worth keeping."""
    if not login.key or _in_networks(login.address, instance_connect):
        return
    stamp = _iso(login.when)
    if known.get(login.key, "") < stamp:
        known[login.key] = stamp


# ─── listeners ────────────────────────────────────────────────────────────────

_DOCKER_PORT = re.compile(r"(?P<ip>\[?[0-9A-Fa-f:.]*\]?):(?P<p1>\d+)(?:-(?P<p2>\d+))?->(?P<target>[\d-]+)/tcp")


def _public_bind(host: str) -> bool:
    """Whether a listener's local address is reachable from outside this machine."""
    ip = _ip(host)
    return ip is None or not ip.is_loopback   # "*", "0.0.0.0", "::" — and a name we cannot read — count


def parse_ss(text: str) -> list[tuple[int, str]]:
    """(port, local address) for every public TCP listener in ``ss -tlnH``."""
    out = []
    for line in text.splitlines():
        parts = line.split()
        if len(parts) < 4:
            continue
        local = parts[3] if parts[0].isalpha() else parts[2]
        if ":" not in local:
            continue
        host, _, port = local.rpartition(":")
        if port.isdigit() and _public_bind(host):
            out.append((int(port), local))
    return out


def parse_docker_ports(text: str) -> list[tuple[int, str]]:
    """(host port, 'container publishes …') for every public published TCP port in ``docker ps``."""
    out = []
    for line in text.splitlines():
        name, _, ports = line.partition("\t")
        for m in _DOCKER_PORT.finditer(ports):
            if not _public_bind(m.group("ip")):
                continue
            first = int(m.group("p1"))
            last = int(m.group("p2") or first)
            for port in range(first, min(last, first + 50) + 1):
                out.append((port, f"container {name.strip()} publishes {m.group(0)}"))
    return out


# ─── secrets in git ───────────────────────────────────────────────────────────

@dataclass(frozen=True)
class _SecretPattern:
    kind: str
    label: str
    ere: str
    ignore_case: bool = False
    skip_tests_and_docs: bool = False


GIT_PATTERNS = (
    _SecretPattern("private-key", "private key", r"-----BEGIN ([A-Z0-9]+ )*PRIVATE KEY-----"),
    _SecretPattern("aws-key", "AWS access key", r"AKIA[0-9A-Z]{16}"),
    # Tokens are scanned in tests and docs too: a vendor's JWT pasted into a doc example or a fixture is the likeliest
    # leak, and docs/ is published at openfno.com/docs. A fixture that needs one is marked (ALLOW_MARKER) or listed.
    _SecretPattern("telegram-token", "Telegram bot token",
                   r"(^|[^0-9A-Za-z])[0-9]{8,10}:[A-Za-z0-9_-]{35}([^A-Za-z0-9_-]|$)"),
    _SecretPattern("jwt", "JSON web token",
                   r"eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}"),
    _SecretPattern("password", "literal password",
                   r"password[\"']?[[:space:]]*[=:][[:space:]]*[\"']?[^[:space:]\"']{6,}",
                   ignore_case=True, skip_tests_and_docs=True),
)

# The detect-secrets convention: a line that says so about itself is a deliberate fake.
ALLOW_MARKER = "pragma: allowlist secret"


def parse_secret_allowlist(spec: str) -> tuple[set[tuple[str, int]], set[str]]:
    """SENTINEL_SECRET_ALLOW → ({(path, line)}, {path}): 'tests/x.py:12, docs/y.md'."""
    lines: set[tuple[str, int]] = set()
    files: set[str] = set()
    for item in re.split(r"[,\s]+", spec or ""):
        item = item[2:] if item.startswith("./") else item   # git grep prints paths without it
        if not item:
            continue
        path, sep, number = item.rpartition(":")
        if sep and number.isdigit() and path:
            lines.add((path, int(number)))
        else:
            files.add(item)
    return lines, files

_CONFIG_SUFFIXES = (".env", ".json", ".yml", ".yaml", ".ini", ".conf", ".cfg", ".toml", ".properties",
                    ".config", ".xml", ".sh", ".ps1")
_PLACEHOLDER_WORDS = ("change", "your", "example", "placeholder", "redact", "dummy", "sample", "xxx", "***",
                      "password", "secret_here", "unchanged", "none", "null", "todo")
_PASSWORD_ASSIGNMENT = re.compile(
    r"(?i)(?P<pre>[;\s\"'{(,]|^)[\w.\-]*password[\w]*[\"']?\s*[=:]\s*"
    r"(?:(?P<q>[\"'])(?P<quoted>[^\"']*)(?P=q)|(?P<bare>[^\s\"';,)}]+))")


def is_test_or_doc(path: str) -> bool:
    lower = path.lower()
    parts = lower.split("/")
    name = parts[-1]
    if any(p in ("test", "tests", "__tests__", "spec", "specs", "docs", "doc", "fixtures", "testdata", "examples")
           or p.endswith((".tests", ".unittests", ".integrationtests")) for p in parts[:-1]):
        return True
    if name.startswith("test_") or re.search(r"(_test\.py|\.test\.[jt]sx?|\.spec\.[jt]sx?|tests?\.cs)$", name):
        return True
    if name.endswith((".md", ".rst", ".txt", ".adoc", ".html")):
        return True
    return any(marker in name for marker in (".example", ".sample", ".template", ".dist"))


def _placeholder(value: str) -> bool:
    lower = value.lower()
    return (value[:1] in "<[({$%" or "${" in value or "{" in value or "$" in value
            or any(word in lower for word in _PLACEHOLDER_WORDS))


def is_literal_password(path: str, text: str) -> bool:
    """
    Whether a ``password = …`` line holds an actual value rather than code
    that fetches one. Quoted literals count anywhere; a bare value counts in a
    config file or inside a connection string (``;Password=…``).
    """
    config = path.lower().endswith(_CONFIG_SUFFIXES) or "/.env" in f"/{path.lower()}"
    for m in _PASSWORD_ASSIGNMENT.finditer(text):
        if m.group("q"):
            value = m.group("quoted")
        else:
            value = m.group("bare") or ""
            if not (config or m.group("pre") == ";"):
                continue
        if len(value) >= 6 and " " not in value and not _placeholder(value):
            return True
    return False


# ─── dependency audits ────────────────────────────────────────────────────────

_NUGET_RANK = {"low": 0, "moderate": 1, "high": 2, "critical": 3}


def parse_dotnet_json(text: str) -> Optional[dict[str, dict]]:
    """``dotnet list package --vulnerable --format json`` → {package: info}; None when it did not run."""
    try:
        body = json.loads(text)
    except ValueError:
        return None
    if not isinstance(body, dict) or not isinstance(body.get("projects"), list):
        return None
    if any(isinstance(p, dict) and str(p.get("level", "")).lower() == "error" for p in body.get("problems") or []):
        return None
    found: dict[str, dict] = {}
    for project in body["projects"]:
        if not isinstance(project, dict):
            continue
        for framework in project.get("frameworks") or []:
            for key, direct in (("topLevelPackages", True), ("transitivePackages", False)):
                for pkg in framework.get(key) or []:
                    for vuln in pkg.get("vulnerabilities") or []:
                        _add_nuget(found, pkg.get("id", "?"), pkg.get("resolvedVersion", ""), direct,
                                   vuln.get("severity", ""), vuln.get("advisoryurl", ""))
    return found


def parse_dotnet_table(text: str) -> Optional[dict[str, dict]]:
    """The console table the same command prints; None when it is not that table."""
    if "vulnerable packages" not in text:   # "has the following …" or "has no …"
        return None
    found: dict[str, dict] = {}
    current: Optional[tuple[str, str, bool]] = None
    direct = True
    for line in text.splitlines():
        if "Top-level Package" in line:
            direct = True
            continue
        if "Transitive Package" in line:
            direct = False
            continue
        m = re.match(r"^\s*>\s+(\S+)\s+(.*)$", line)
        if m:
            cols = m.group(2).split()
            url = cols[-1] if cols and cols[-1].startswith("http") else ""
            sev = cols[-2] if url and len(cols) >= 2 else (cols[-1] if cols else "")
            version = cols[-3] if url and len(cols) >= 3 else ""
            current = (m.group(1), version, direct)
            _add_nuget(found, m.group(1), version, direct, sev, url)
            continue
        m = re.match(r"^\s+(Low|Moderate|High|Critical)\s+(https?://\S+)\s*$", line)
        if m and current:
            _add_nuget(found, current[0], current[1], current[2], m.group(1), m.group(2))
        elif not line.strip():
            current = None
    return found


def _add_nuget(found: dict, name: str, version: str, direct: bool, severity: str, url: str) -> None:
    entry = found.setdefault(name, {"version": version, "direct": direct, "severity": "low", "advisories": []})
    entry["direct"] = entry["direct"] or direct
    if _NUGET_RANK.get(severity.lower(), -1) > _NUGET_RANK.get(entry["severity"], -1):
        entry["severity"] = severity.lower()
    item = f"{severity} {url}".strip()
    if item not in entry["advisories"]:
        entry["advisories"].append(item)


def parse_npm_audit(text: str) -> Optional[dict[str, dict]]:
    """
    ``npm audit --json`` → {package: info} for the packages that carry an
    advisory of their own. A package listed only because it depends on one of
    those is folded into that package's "pulled in by", not reported again.
    """
    try:
        body = json.loads(text)
    except ValueError:
        return None
    if not isinstance(body, dict) or "error" in body or not isinstance(body.get("vulnerabilities"), dict):
        return None
    vulns = body["vulnerabilities"]

    def reaches(start: str, target: str) -> bool:
        seen, stack = set(), [start]
        while stack:
            name = stack.pop()
            if name == target:
                return True
            if name in seen:
                continue
            seen.add(name)
            stack.extend(v for v in (vulns.get(name) or {}).get("via") or [] if isinstance(v, str))
        return False

    found: dict[str, dict] = {}
    for name, info in vulns.items():
        if not isinstance(info, dict):
            continue
        own = [v for v in info.get("via") or [] if isinstance(v, dict)]
        if not own:
            continue
        direct_parents = sorted(n for n, i in vulns.items()
                                if isinstance(i, dict) and i.get("isDirect") and n != name and reaches(n, name))
        found[name] = {
            "severity": str(info.get("severity", "")).lower(),
            "direct": bool(info.get("isDirect")),
            "range": info.get("range", ""),
            "via": direct_parents,
            "fix": info.get("fixAvailable"),
            "advisories": [f"{v.get('severity', '')}: {v.get('title', '')[:90]} {v.get('url', '')}".strip()
                           for v in own],
        }
    return found


# ─── the agent ────────────────────────────────────────────────────────────────

class SecurityAgent(Agent):
    name = AGENT
    interval_seconds = 300
    resolve_after = 1

    def __init__(self, auth_logs: Optional[Iterable[str | Path]] = None) -> None:
        self._auth_logs = tuple(Path(p) for p in (DEFAULT_AUTH_LOGS if auth_logs is None else auth_logs))

    def check(self, ctx: SentinelContext) -> list[Finding]:
        state = ctx.state(self.name)
        st = state.data
        now = ctx.now()
        findings: list[Finding] = []
        rules: tuple[tuple[str, Callable[[SentinelContext, dict, datetime], list[Finding]]], ...] = (
            ("login-failures", self._login_failures),
            ("privileged-change", self._privileged_changes),
            ("ssh-login", self._ssh_logins),
            ("open-port", self._open_ports),
            ("secret-file-permissions", self._secret_file_permissions),
            ("secret-in-git", self._secret_in_git),
            ("vulnerable-dependency", self._vulnerable_dependencies),
            ("fail2ban", self._fail2ban),
        )
        for rule, run in rules:
            try:
                findings += run(ctx, st, now)
            except Exception as exc:   # one broken rule must not blind the other seven
                findings.append(Finding(
                    agent=self.name, rule="rule-crashed", severity=Severity.MEDIUM,
                    title=f"Sentinel's security rule {rule} failed to run",
                    summary=f"{type(exc).__name__}: {str(exc)[:300]}",
                    fingerprint=f"{self.name}:rule-crashed:{rule}:{type(exc).__name__}",
                    where="sentinel/agents/security.py",
                    evidence=traceback.format_exc().strip().splitlines()[-4:],
                    suggestion="Read the traceback; the agent's other rules still ran.",
                ))
        state.save()
        return findings

    # ── login-failures ──

    def _login_failures(self, ctx: SentinelContext, st: dict, now: datetime) -> list[Finding]:
        since = now - LOGIN_WINDOW
        query = {"action": "create:userauth-login", "succeeded": "false", "fromUtc": _utc_param(since),
                 "limit": ACTIVITY_PAGE}
        try:
            body = ctx.api_get("/api/ActivityLog?" + urlencode(query))
            rows = [r for r in body["rows"] if isinstance(r, dict) and r.get("succeeded") is not True]
            total = int(body.get("total", len(rows)))
        except Exception:
            return _carry(st, "login_failures")

        recent = [r for r in rows if (_parse_time(r.get("occurredUtc")) or now) >= since]
        count = total if len(rows) >= ACTIVITY_PAGE else len(recent)

        groups: dict[str, dict] = {}
        for row in recent:
            label, local = _address_group(row.get("ipAddress"))
            g = groups.setdefault(label, {"n": 0, "local": local, "names": set(), "times": []})
            g["n"] += 1
            if row.get("targetId"):
                g["names"].add(str(row["targetId"]).lower())
            t = _parse_time(row.get("occurredUtc"))
            if t:
                g["times"].append(t)

        ranked = sorted(groups.items(), key=lambda kv: -kv[1]["n"])
        per_address = [f"{label}: {g['n']} failed, {len(g['names'])} name(s) tried" for label, g in ranked[:5]]
        if len(ranked) > 5:
            per_address.append(f"… and {len(ranked) - 5} more address(es)")

        # Whether an address had signed in before its failures began: that is someone on the desk with an old
        # password (23 Sep: the owner's household, 7 failures between two good sign-ins), not a guess. Asked only
        # when something is over a bar, so the ordinary check is still one request.
        remote = [(label, g) for label, g in ranked if not g["local"]]
        over = {label for label, g in remote if g["n"] > LOGIN_PER_ADDRESS_LIMIT}
        below = [g for label, g in remote if label not in over]
        maybe_spread = sum(g["n"] for g in below) > LOGIN_TOTAL_LIMIT and len(below) >= SPREAD_MIN_ADDRESSES
        successes = self._successful_sign_ins(ctx, now) if over or maybe_spread else {}
        for label, g in remote:
            first = min(g["times"]) if g["times"] else now
            ok = successes.get(label, [])
            g["known_since"] = max((t for t in ok if t < first), default=None)
            g["then_ok"] = min((t for t in ok if t >= first), default=None)

        current: list[tuple[Finding, datetime]] = []
        for label, g in ranked:
            span = f"{_ist(min(g['times']))} – {to_ist(max(g['times'])).strftime('%H:%M IST')}" if g["times"] else ""
            said = (f"{g['n']} failed sign-ins in the last 15 minutes from {label}" + (f" ({span})" if span else "")
                    + f", {len(g['names'])} different name(s) tried; {count} failed sign-ins in all.")
            evidence = list(per_address)
            severity = Severity.HIGH
            if g["local"]:
                if g["n"] <= LOOPBACK_LIMIT:
                    continue
                title = "Sign-ins from this machine keep failing — a runner or script has a wrong password"
                suggestion = ("A program on the server is signing in with credentials the API refuses. Compare "
                              "ADMIN_USERNAME/ADMIN_PASSWORD and ENGINE_SERVICE_PASSWORD in .env with the accounts; "
                              "a runner that cannot sign in exits at boot and its run never trades (24 Sep).")
            elif label not in over:
                continue
            elif g["known_since"]:
                severity = Severity.LOW
                title = f"Repeated failed sign-ins from an address that also signed in successfully ({label})"
                said += (f" The same address signed in successfully at {_ist(g['known_since'])}, before the "
                         f"failures began — most likely someone on the desk, or a script at their end, with an old "
                         f"password.")
                evidence.insert(0, f"{label}: last good sign-in {_ist(g['known_since'])}, before the failures")
                suggestion = ("Ask who was signing in from there. A password saved in a browser, or a script on "
                              "their machine still using an old one, keeps failing like this. If nobody was, treat "
                              "it as a guess: reset the passwords of the names tried (Activity log → auth, failures "
                              "only).")
            elif g["then_ok"]:
                severity = Severity.CRITICAL
                title = f"Someone guessed passwords from {label}, then signed in"
                said += (f" A sign-in from the same address then succeeded at {_ist(g['then_ok'])}, and that "
                         f"address had not signed in during the day before.")
                evidence.insert(0, f"{label}: sign-in succeeded at {_ist(g['then_ok'])}, after the failures")
                suggestion = ("Find the account that signed in (Activity log → auth, around that time). Unless it "
                              "was someone on the desk who had forgotten their password: revoke that account's "
                              "sessions (Users → revoke sessions), reset its password, block the address in the AWS "
                              "security group or Cloudflare, and read what the account did after signing in.")
            else:
                title = f"Someone is guessing passwords from {label}"
                suggestion = ("If this is not someone on the desk mistyping: block the address in the AWS security "
                              "group or Cloudflare, and make sure every account has a long password. The limiter "
                              "already holds each address to 10 tries a minute. The names tried are in "
                              "Activity log → auth, failures only.")
            current.append((Finding(
                agent=self.name, rule="login-failures", severity=severity, title=title, summary=said,
                fingerprint=f"{self.name}:login-failures:{'loopback' if g['local'] else label}",
                where="POST /api/UserAuth/login", evidence=evidence[:6], suggestion=suggestion,
            ), max(g["times"]) if g["times"] else now))

        spread = [g for label, g in remote if label not in over and not g["known_since"]]
        spread_n = sum(g["n"] for g in spread)
        if spread_n > LOGIN_TOTAL_LIMIT and len(spread) >= SPREAD_MIN_ADDRESSES:
            times = [t for g in spread for t in g["times"]]
            current.append((Finding(
                agent=self.name, rule="login-failures", severity=Severity.HIGH,
                title="Many failed sign-ins, spread across addresses",
                summary=(f"{spread_n} failed sign-ins in the last 15 minutes from {len(spread)} address(es), none "
                         f"above {LOGIN_PER_ADDRESS_LIMIT} on its own and none that had signed in before — the "
                         f"pattern of a distributed guess."),
                fingerprint=f"{self.name}:login-failures:spread",
                where="POST /api/UserAuth/login", evidence=per_address,
                suggestion=("A per-address limiter does not stop a guess spread over many addresses. Turn on "
                            "Cloudflare's bot protection for the sign-in path and check that no account still has "
                            "a short password."),
            ), max(times) if times else now))

        return _remember(st, "login_failures", self._hold_login_findings(st, current, now))

    def _successful_sign_ins(self, ctx: SentinelContext, now: datetime) -> dict[str, list[datetime]]:
        """Address group → times of its successful sign-ins in the last day. Unreadable: none, so nothing is excused."""
        found: dict[str, list[datetime]] = {}
        try:
            for page in range(ACTIVITY_MAX_PAGES):
                body = ctx.api_get("/api/ActivityLog?" + urlencode({
                    "action": "create:userauth-login", "succeeded": "true",
                    "fromUtc": _utc_param(now - LOGIN_SUCCESS_LOOKBACK),
                    "limit": ACTIVITY_PAGE, "offset": page * ACTIVITY_PAGE}))
                batch = body["rows"]
                for row in batch:
                    if not isinstance(row, dict) or row.get("succeeded") is not True:
                        continue
                    label, local = _address_group(row.get("ipAddress"))
                    when = _parse_time(row.get("occurredUtc"))
                    if when is not None and not local:
                        found.setdefault(label, []).append(when)
                if len(batch) < ACTIVITY_PAGE:
                    break
        except Exception:
            pass
        return found

    def _hold_login_findings(self, st: dict, current: list[tuple[Finding, datetime]],
                             now: datetime) -> list[Finding]:
        """
        Keep a crossed fingerprint open until LOGIN_HOLD after its last failure over the bar, so a guess that
        hovers around the threshold is one incident, not a fresh alarm on every crossing.
        """
        hold = st.get("login_hold") if isinstance(st.get("login_hold"), dict) else {}
        out = []
        for finding, last_bad in current:
            hold[finding.fingerprint] = {"until": _iso(last_bad + LOGIN_HOLD), "finding": _dump(finding)}
            out.append(finding)
        reported = {f.fingerprint for f in out}
        for fingerprint, held in list(hold.items()):
            until = _parse_time(held.get("until")) if isinstance(held, dict) else None
            finding = _load(held.get("finding") or {}) if isinstance(held, dict) else None
            if until is None or until <= now or finding is None:
                hold.pop(fingerprint, None)
                continue
            if fingerprint not in reported:
                out.append(replace(finding, summary=finding.summary + (
                    f" Below the bar now; held open until {to_ist(until).strftime('%H:%M IST')} in case it resumes.")))
        st["login_hold"] = hold
        return out

    # ── privileged-change ──

    def _privileged_changes(self, ctx: SentinelContext, st: dict, now: datetime) -> list[Finding]:
        memo = st.setdefault("activity", {})
        memo.setdefault("since", _iso(now - FIRST_LOOKBACK))   # kept even if this first read fails
        since = _parse_time(memo.get("since")) or (now - FIRST_LOOKBACK)
        since = max(since, now - ACTIVITY_MAX_WINDOW)
        reported = [int(i) for i in memo.get("reported_ids", []) if isinstance(i, int)]
        seen = set(reported)

        rows: list[dict] = []
        try:
            for module in ACTIVITY_MODULES:
                for page in range(ACTIVITY_MAX_PAGES):
                    body = ctx.api_get("/api/ActivityLog?" + urlencode({
                        "module": module, "succeeded": "true", "fromUtc": _utc_param(since),
                        "limit": ACTIVITY_PAGE, "offset": page * ACTIVITY_PAGE}))
                    batch = body["rows"]
                    rows.extend(r for r in batch if isinstance(r, dict))
                    if len(batch) < ACTIVITY_PAGE:
                        break
        except Exception:
            return []   # the window is kept; the next check that can read the log reports it

        changes = []
        for row in rows:
            entry_id = row.get("id")
            if not isinstance(entry_id, int) or entry_id in seen or row.get("succeeded") is False:
                continue
            hit = classify_change(str(row.get("method", "")), str(row.get("path", "")))
            if hit is None:
                continue
            seen.add(entry_id)
            changes.append((entry_id, row, *hit))
        changes.sort(key=lambda c: c[0])

        findings = [self._change_finding(entry_id, row, change, m)
                    for entry_id, row, change, m in changes[:PRIVILEGED_PER_CHECK]]
        if len(changes) > PRIVILEGED_PER_CHECK:
            rest = changes[PRIVILEGED_PER_CHECK:]
            kinds: dict[str, int] = {}
            for _, _, change, _ in rest:
                kinds[change.kind] = kinds.get(change.kind, 0) + 1
            findings.append(Finding(
                agent=self.name, rule="privileged-change", severity=Severity.MEDIUM,
                title=f"{len(rest)} more privileged changes in five minutes",
                summary=(f"{len(changes)} privileged changes since {_ist(since)}; the first "
                         f"{PRIVILEGED_PER_CHECK} were sent one by one, the rest are rolled up here."),
                fingerprint=f"{self.name}:privileged-change:burst:{rest[0][0]}",
                where=f"Activity log #{rest[0][0]}–#{rest[-1][0]}",
                evidence=[f"{kind}: {n}" for kind, n in sorted(kinds.items(), key=lambda kv: -kv[1])][:6],
                suggestion="A burst like this is a script, not a person at the console — find out whose.",
                extra=dict(NOTICE),
            ))

        memo["since"] = _iso(now - ACTIVITY_MARGIN)
        memo["reported_ids"] = sorted(seen)[-2000:]
        return findings

    def _change_finding(self, entry_id: int, row: dict, change: _Change, m: str) -> Finding:
        actor = str(row.get("userName") or "someone")
        if change.kind == "kill-switch":
            m = "activate" if m.lower() == "activate" else "release"
        title = change.title.format(actor=actor, m=m)
        when = _parse_time(row.get("occurredUtc"))
        address, _ = _address_group(row.get("ipAddress"))
        path = change.shown_path or str(row.get("path", ""))
        evidence = [f"{row.get('method', '')} {path} → HTTP {row.get('statusCode', '?')}",
                    f"by {actor}" + (f" ({row['role']})" if row.get("role") else "") + f" from {address}"]
        if when:
            evidence.append(f"at {_ist(when)}")
        if row.get("summary") and change.kind != "invite-accepted":
            evidence.append(str(row["summary"])[:200])
        return Finding(
            agent=self.name, rule="privileged-change", severity=Severity.MEDIUM, title=title,
            summary=(f"{title}" + (f" at {_ist(when)}" if when else "") + f", from {address}. "
                     f"Recorded as activity-log entry #{entry_id}."),
            fingerprint=f"{self.name}:privileged-change:{entry_id}",
            where=f"Activity log #{entry_id} ({change.kind})",
            evidence=evidence,
            suggestion=change.suggestion or _IF_NOT_YOU,
            extra=dict(NOTICE),
        )

    # ── ssh-login ──

    def _ssh_logins(self, ctx: SentinelContext, st: dict, now: datetime) -> list[Finding]:
        memo = st.setdefault("ssh", {})
        raw_known = memo.get("known_keys")
        known: dict[str, str] = ({k: v for k, v in raw_known.items() if isinstance(k, str) and isinstance(v, str)}
                                 if isinstance(raw_known, dict) else {})
        instance_connect = _networks(EC2_INSTANCE_CONNECT)
        trusted = instance_connect + _networks(ctx.env.get("SENTINEL_SSH_ALLOWED", ""))
        listed = [_key_id(k) for k in re.split(r"[,\s]+", ctx.env.get("SENTINEL_SSH_KEYS", "")) if k.strip()]

        # The first check learns every key already in use from the whole history, and reports none of them: the
        # owner's own session is what starts Sentinel, and a watchman that opens by crying wolf gets muted.
        seeding = not memo.get("seeded")
        since = _parse_time(memo.get("since")) or (now - FIRST_LOOKBACK)
        lines, denied = self._read_auth_logs(None if seeding else since - AUTH_LOG_MARGIN)
        unreadable = self._ssh_unreadable(denied)   # one log we may not read is a blind spot, whatever else we can
        if lines is None:
            return unreadable   # the window is kept for the check that can read it
        logins = sorted(parse_auth_log(lines, now), key=lambda x: x.when)
        if seeding:
            for login in logins:
                _learn_key(known, login, instance_connect)
            memo["seeded"] = _iso(now)

        seen_events = [s for s in memo.get("events_seen", []) if isinstance(s, str)]
        seen = set(seen_events)
        new_keys: dict[str, list[_Login]] = {}
        other: dict[tuple[str, str, str], list[_Login]] = {}
        for login in logins:
            if login.when < since - AUTH_LOG_MARGIN:
                continue
            if login.method == "publickey":
                if not login.key:
                    continue   # OpenSSH names the key on every publickey line; nothing to judge without it
                if login.key in known or _key_listed(login.key, listed) or _in_networks(login.address, trusted):
                    _learn_key(known, login, instance_connect)   # a known key from a new address says nothing
                else:
                    new_keys.setdefault(login.key, []).append(login)
                continue
            event = f"{login.when.isoformat()}|{login.user}|{login.address}|{login.method}"
            if event in seen:
                continue   # re-read in the overlap with the previous check
            seen.add(event)
            seen_events.append(event)
            kind = "password" if login.by_password else (re.sub(r"[^a-z0-9-]+", "-", login.method.lower()) or "other")
            other.setdefault((kind, login.user, _address_group(login.address)[0]), []).append(login)

        findings = unreadable + [self._ssh_new_key(key, items, len(known)) for key, items in new_keys.items()]
        findings += [self._ssh_not_by_key(kind, user, where, items) for (kind, user, where), items in other.items()]
        for items in new_keys.values():
            for login in items:
                _learn_key(known, login, instance_connect)

        if len(known) > KNOWN_KEYS_MAX:   # the least recently used go first; a key in use is refreshed every login
            known = dict(sorted(known.items(), key=lambda kv: kv[1])[-KNOWN_KEYS_MAX:])
        memo["known_keys"] = known
        memo["events_seen"] = seen_events[-200:]
        memo["since"] = _iso(now)
        return findings

    def _read_auth_logs(self, newer_than: Optional[datetime]) -> tuple[Optional[list[str]], list[str]]:
        """
        (lines, paths that exist but may not be read). With ``newer_than`` the tail of each log written since
        then; without it, the whole history, rotated and gzipped files included. Lines are None when a log is
        there and nothing could be read.
        """
        paths = list(self._auth_logs)
        if newer_than is None:
            paths += [gz for p in self._auth_logs if not p.suffix[1:].isdigit()
                      for gz in sorted(p.parent.glob(p.name + ".*.gz"))]
        lines: list[str] = []
        denied: list[str] = []
        read_any = False
        for path in paths:
            try:
                if newer_than is not None:
                    if datetime.fromtimestamp(path.stat().st_mtime, timezone.utc) < newer_than:
                        continue   # a rotated log that ended before the window
                    lines += _tail_lines(path)
                else:
                    lines += _accepted_lines(path)
                read_any = True
            except PermissionError:
                if path in self._auth_logs:   # an old archive we may not open only thins the history
                    denied.append(str(path))
            except (OSError, EOFError, zlib.error):
                continue   # not there (this machine logs elsewhere), or a damaged archive
        return (None if denied and not read_any else lines), denied

    def _ssh_unreadable(self, denied: list[str]) -> list[Finding]:
        if not denied:
            return []   # no such log on this machine: nothing to watch, nothing to say
        return [Finding(
            agent=self.name, rule="ssh-login", severity=Severity.LOW,
            title="Sentinel cannot read the SSH log, so nobody is watching who logs in to the server",
            summary=(f"{', '.join(denied)} exist(s), but the user Sentinel runs as may not read it. SSH logins — "
                     f"new keys, password logins — go unchecked until it can."),
            fingerprint=f"{self.name}:ssh-login:unreadable",
            where=denied[0], evidence=[f"{p}: permission denied" for p in denied[:6]],
            suggestion=("The log belongs to the adm group: add the user Sentinel runs as to it "
                        "(sudo usermod -aG adm <user>) and restart Sentinel. The ubuntu user is in adm already."),
        )]

    def _ssh_new_key(self, key: str, items: list[_Login], known_count: int) -> Finding:
        first = items[0]
        return Finding(
            agent=self.name, rule="ssh-login", severity=Severity.MEDIUM,
            title=f"SSH login to the server with a key it has not seen before ({first.key_type} key, {first.user})",
            summary=(f"{first.user} signed in over SSH from {first.address} at {_ist(first.when)} with the "
                     f"{first.key_type} key {key}" + (f" ({len(items)} sessions)" if len(items) > 1 else "")
                     + f". No earlier login in the server's log used this key; Sentinel knows {known_count} "
                       f"other key(s)."),
            fingerprint=f"{self.name}:ssh-login:key:SHA256:{_key_id(key)[:16]}",
            where="sshd on the server", evidence=[x.describe() for x in items[:5]],
            suggestion=("If it is a new key of yours or a colleague's, nothing to do — it is known from now on "
                        "(list it in SENTINEL_SSH_KEYS to say so up front). If not: remove it from "
                        "~/.ssh/authorized_keys (ssh-keygen -lf ~/.ssh/authorized_keys lists the fingerprints), "
                        "rotate the instance key pair, and narrow port 22 in the AWS security group."),
            extra=dict(NOTICE),
        )

    def _ssh_not_by_key(self, kind: str, user: str, where: str, items: list[_Login]) -> Finding:
        items.sort(key=lambda x: x.when)
        first = items[0]
        how = "a password" if kind == "password" else first.method
        return Finding(
            agent=self.name, rule="ssh-login", severity=Severity.HIGH,
            title=f"SSH login to the server with {how}: {user} from {where}",
            summary=(f"{user} signed in over SSH from {first.address} at {_ist(first.when)} using {first.method}"
                     + (f" ({len(items)} sessions)" if len(items) > 1 else "")
                     + ". This server should accept keys only, so password logins are switched on and someone "
                       "knows that account's password."),
            fingerprint=f"{self.name}:ssh-login:{kind}:{user}:{where}"[:200],
            where="sshd on the server", evidence=[x.describe() for x in items[:5]],
            suggestion=("If it was not you: change that account's password now, then set PasswordAuthentication no "
                        "and KbdInteractiveAuthentication no in /etc/ssh/sshd_config and in any file under "
                        "/etc/ssh/sshd_config.d/ (cloud images switch it on there), and reload ssh. Check "
                        "~/.ssh/authorized_keys for keys you do not know."),
            extra=dict(NOTICE),
        )

    # ── open-port ──

    def _open_ports(self, ctx: SentinelContext, st: dict, now: datetime) -> list[Finding]:
        allowed = _ports(ctx.env.get("SENTINEL_PUBLIC_PORTS") or "22")
        rc, out = ctx.run(["ss", "-tlnH"])
        if rc != 0:
            return _carry(st, "open_ports")
        exposed: dict[int, list[str]] = {}
        for port, local in parse_ss(out):
            exposed.setdefault(port, []).append(f"listening on {local}")
        rc, out = ctx.run(["docker", "ps", "--format", "{{.Names}}\t{{.Ports}}"])
        if rc == 0:
            for port, line in parse_docker_ports(out):
                exposed.setdefault(port, []).append(line)

        findings = []
        for port in sorted(p for p in exposed if p not in allowed):
            evidence = list(dict.fromkeys(exposed[port]))[:6]
            docker = any(e.startswith("container ") for e in evidence)
            findings.append(Finding(
                agent=self.name, rule="open-port", severity=Severity.HIGH,
                title=f"Port {port} is open to the internet on the server",
                summary=(f"TCP port {port} is bound to a public address, and {port} is not in "
                         f"SENTINEL_PUBLIC_PORTS ({', '.join(map(str, sorted(allowed))) or 'none'}). Only the AWS "
                         f"security group stands between it and the internet."),
                fingerprint=f"{self.name}:open-port:tcp/{port}",
                where=f"tcp/{port}", evidence=evidence,
                suggestion=(("Publish it on loopback only — \"127.0.0.1:{p}:{p}\" in docker-compose, as the "
                             "database, Redis and Grafana already are — then recreate the container.")
                            if docker else
                            "Bind the service to 127.0.0.1 (everything public goes through the Cloudflare "
                            "tunnel), or add the port to SENTINEL_PUBLIC_PORTS if it is meant to be public."
                            ).format(p=port),
            ))
        return _remember(st, "open_ports", findings)

    # ── secret-file-permissions ──

    def _secret_file_permissions(self, ctx: SentinelContext, st: dict, now: datetime) -> list[Finding]:
        findings = []
        for rel in SECRET_FILES:
            try:
                info = os.stat(ctx.repo_root / rel)
            except OSError:
                continue
            mode = stat.S_IMODE(info.st_mode)
            if not mode & 0o077:
                continue
            who = [text for bit, text in ((0o040, "group can read"), (0o020, "group can write"),
                                          (0o004, "others can read"), (0o002, "others can write")) if mode & bit]
            findings.append(Finding(
                agent=self.name, rule="secret-file-permissions", severity=Severity.HIGH,
                title=f"{rel} can be opened by other users on the server",
                summary=(f"{rel} holds the desk's passwords and broker keys, and its mode is {mode:04o} "
                         f"({', '.join(who) or 'group/others have access'}). It should be 0600."),
                fingerprint=f"{self.name}:secret-file-permissions:{rel}",
                where=rel, evidence=[f"mode {mode:04o}", f"owner uid {info.st_uid}, group gid {info.st_gid}"],
                suggestion=(f"chmod 600 {rel}. " + (
                    "scripts/_gen_local_settings.py (run by desk.sh on every start) keeps the mode of an existing "
                    "file but creates a new one with the default umask — so a deleted-and-regenerated file comes "
                    "back readable." if rel.endswith("appsettings.Local.json") else
                    "scripts/setup.sh sets 0600 when it creates the file; an editor or a copy can reset it.")),
            ))
        return findings

    # ── secret-in-git ──

    def _secret_in_git(self, ctx: SentinelContext, st: dict, now: datetime) -> list[Finding]:
        memo = st.setdefault("git", {})
        today = ist_date(now)
        if memo.get("day") == today:
            return _carry(memo, "findings")

        allowed_lines, allowed_files = parse_secret_allowlist(ctx.env.get("SENTINEL_SECRET_ALLOW", ""))
        hits: dict[tuple[str, str], list[int]] = {}
        for pattern in GIT_PATTERNS:
            args = ["git", "grep", "-z", "-n", "-I", "-E"] + (["-i"] if pattern.ignore_case else []) \
                + ["-e", pattern.ere]
            rc, out = ctx.run(args, timeout=120)
            if rc not in (0, 1):
                return _carry(memo, "findings")   # git could not run; try again next check
            for record in out.split("\n"):
                parts = record.split("\0", 2)
                if len(parts) < 3 or not parts[1].isdigit():
                    continue
                path, line, text = parts
                if pattern.skip_tests_and_docs and is_test_or_doc(path):
                    continue
                if ALLOW_MARKER in text or path in allowed_files or (path, int(line)) in allowed_lines:
                    continue
                if pattern.kind == "password" and not is_literal_password(path, text):
                    continue
                hits.setdefault((pattern.kind, path), []).append(int(line))   # the text is judged, never kept

        labels = {p.kind: p.label for p in GIT_PATTERNS}
        order = {p.kind: i for i, p in enumerate(GIT_PATTERNS)}
        findings = []
        for (kind, path), lines in sorted(hits.items(), key=lambda kv: (order[kv[0][0]], kv[0][1])):
            if len(findings) == GIT_SCAN_MAX_FILES:
                break
            lines = sorted(set(lines))
            evidence = [f"{path}:{n}" for n in lines[:5]]
            if len(lines) > 5:
                evidence.append(f"… {len(lines) - 5} more line(s)")
            findings.append(Finding(
                agent=self.name, rule="secret-in-git", severity=Severity.CRITICAL,
                title=f"A {labels[kind]} is committed to git in {path}",
                summary=(f"git grep found what looks like a {labels[kind]} on {len(lines)} line(s) of {path}, a "
                         f"tracked file — anyone who can read the repository, GitHub included, can read it."),
                fingerprint=f"{self.name}:secret-in-git:{kind}:{path[-150:]}",
                where=path, evidence=evidence,
                suggestion=("Rotate the credential first — if the repository is public it is already out. Then move "
                            "it to .env (or appsettings.Local.json, which is generated from it) and delete the "
                            "line; deleting does not remove it from git history."),
            ))
        if len(hits) > GIT_SCAN_MAX_FILES:
            findings[-1] = replace(findings[-1], summary=findings[-1].summary + (
                f" {len(hits)} file/pattern pairs matched in all; only {GIT_SCAN_MAX_FILES} are listed."))
        memo["day"] = today
        _remember(memo, "findings", findings)
        return findings

    # ── vulnerable-dependency ──

    def _vulnerable_dependencies(self, ctx: SentinelContext, st: dict, now: datetime) -> list[Finding]:
        memo = st.setdefault("deps", {})
        last_ok = _parse_time(memo.get("last_success"))
        last_try = _parse_time(memo.get("last_attempt"))
        due = (last_ok is None or now - last_ok >= DEPENDENCY_SCAN_EVERY) and \
              (last_try is None or now - last_try >= DEPENDENCY_RETRY_AFTER)
        if not due or self._in_market_window(ctx, now):
            return _carry(memo, "findings")
        memo["last_attempt"] = _iso(now)

        nuget = self._dotnet_audit(ctx)
        npm = self._npm_audit(ctx)
        if nuget is None and npm is None:
            return _carry(memo, "findings")   # neither ran; not a finding, and the old result stands

        previous = _carry(memo, "findings")
        findings: list[Finding] = []
        findings += (self._nuget_findings(nuget) if nuget is not None
                     else [f for f in previous if ":nuget:" in f.fingerprint])
        findings += (self._npm_findings(npm) if npm is not None
                     else [f for f in previous if ":npm:" in f.fingerprint])
        if nuget is not None and npm is not None:
            memo["last_success"] = _iso(now)
        _remember(memo, "findings", findings)
        return findings

    def _in_market_window(self, ctx: SentinelContext, now: datetime) -> bool:
        t = to_ist(now).time()
        if not (MARKET_WINDOW[0] <= t < MARKET_WINDOW[1]):
            return False
        try:
            return ctx.session().trading_day
        except Exception:
            return to_ist(now).weekday() < 5

    def _dotnet_audit(self, ctx: SentinelContext) -> Optional[dict[str, dict]]:
        project = ctx.repo_root / "src" / "AlgoTrading.Api"
        if not project.is_dir():
            return {}   # nothing to audit is a clean audit, not a failed one
        # --no-restore: newer SDKs restore before listing, which writes obj/ — not a watchman's business.
        base = ["dotnet", "list", "package", "--vulnerable", "--include-transitive", "--no-restore"]
        _, out = ctx.run(base + ["--format", "json"], timeout=300, cwd=project)
        if out.lstrip().startswith("{"):
            return parse_dotnet_json(out)   # None when it reported a problem (no assets file, no network)
        rc, out = ctx.run(base, timeout=300, cwd=project)   # an SDK without --format json: read the table
        return parse_dotnet_table(out) if rc == 0 else None

    def _npm_audit(self, ctx: SentinelContext) -> Optional[dict[str, dict]]:
        web = ctx.repo_root / "web"
        if not (web / "package-lock.json").is_file():
            return {}
        _, out = ctx.run(["npm", "audit", "--omit=dev", "--json"], timeout=180, cwd=web)   # exits 1 when it finds any
        return parse_npm_audit(out)

    def _nuget_findings(self, found: dict[str, dict]) -> list[Finding]:
        findings = []
        for name, info in sorted(found.items()):
            if _NUGET_RANK.get(info["severity"], 0) < _NUGET_RANK["moderate"]:
                continue
            critical = info["severity"] == "critical"
            findings.append(Finding(
                agent=self.name, rule="vulnerable-dependency",
                severity=Severity.HIGH if critical else Severity.MEDIUM,
                title=f"{name} {info['version']} in the API has a known {info['severity']} vulnerability".replace(
                    "  ", " "),
                summary=(f"dotnet list package --vulnerable reports {len(info['advisories'])} advisory(ies) against "
                         f"{name} {info['version']}, "
                         + ("referenced directly by the API." if info["direct"] else
                            "pulled in by another package the API uses.")),
                fingerprint=f"{self.name}:vulnerable-dependency:nuget:{name}",
                where="src/AlgoTrading.Api (NuGet)", evidence=info["advisories"][:6],
                suggestion=(("Raise its version in the .csproj that references it to a patched release, build, and "
                             "deploy outside market hours.") if info["direct"] else
                            ("Update the package that pulls it in, or pin a patched version with a direct "
                             "PackageReference; build and deploy outside market hours.")),
            ))
        return findings

    def _npm_findings(self, found: dict[str, dict]) -> list[Finding]:
        findings = []
        for name, info in sorted(found.items()):
            if info["severity"] not in ("moderate", "high", "critical"):
                continue
            critical = info["severity"] == "critical"
            carriers = "a direct dependency" if info["direct"] else (
                "pulled in by " + ", ".join(info["via"][:3]) if info["via"] else "a transitive dependency")
            fix = info["fix"]
            if isinstance(fix, dict):
                advice = (f"npm audit's fix is {fix.get('name')}@{fix.get('version')}"
                          + (" — a major version, so test the pages that use it" if fix.get("isSemVerMajor") else "")
                          + "; rebuild the console after.")
            elif fix:
                advice = ("`npm audit fix --omit=dev` in web/ fixes it without a major upgrade; rebuild the "
                          "console after.")
            else:
                advice = ("No fix is published yet; judge whether the vulnerable code is reachable from the console "
                          "(it runs in the trader's browser).")
            findings.append(Finding(
                agent=self.name, rule="vulnerable-dependency",
                severity=Severity.HIGH if critical else Severity.MEDIUM,
                title=f"{name} in the web console has a known {info['severity']} vulnerability",
                summary=(f"npm audit reports {len(info['advisories'])} advisory(ies) against {name} "
                         f"({info['range']}), {carriers}."),
                fingerprint=f"{self.name}:vulnerable-dependency:npm:{name}",
                where="web/ (npm, production dependencies)", evidence=info["advisories"][:6],
                suggestion=advice,
            ))
        return findings

    # ── fail2ban ──

    def _fail2ban(self, ctx: SentinelContext, st: dict, now: datetime) -> list[Finding]:
        rc, out = ctx.run(["fail2ban-client", "status", "sshd"])
        total = re.search(r"Total banned:\s*(\d+)", out or "")
        if rc != 0 or total is None:
            return []   # not installed, or not readable without root: nothing to say
        memo = st.setdefault("fail2ban", {})
        now_total = int(total.group(1))
        before = memo.get("total_banned")
        memo["total_banned"] = now_total
        if not isinstance(before, int) or now_total < before or now_total - before <= FAIL2BAN_JUMP:
            return []
        current = re.search(r"Currently banned:\s*(\d+)", out)
        listed = re.search(r"Banned IP list:\s*(.*)", out)
        evidence = [f"total bans {before} → {now_total}"]
        if current:
            evidence.append(f"currently banned: {current.group(1)}")
        if listed and listed.group(1).strip():
            ips = listed.group(1).split()
            evidence.append("latest: " + ", ".join(ips[-5:]))
        return [Finding(
            agent=self.name, rule="fail2ban", severity=Severity.LOW,
            title="A burst of SSH password guessing was banned",
            summary=(f"fail2ban banned {now_total - before} addresses for sshd in five minutes "
                     f"({now_total} in all). The bans worked; this is a heads-up that the server is being tried."),
            fingerprint=f"{self.name}:fail2ban:sshd",
            where="fail2ban jail sshd", evidence=evidence,
            suggestion=("Nothing to do while the bans hold. If it keeps up, narrow port 22 in the AWS security group "
                        "to known addresses."),
            extra=dict(NOTICE),
        )]
