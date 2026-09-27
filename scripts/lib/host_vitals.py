#!/usr/bin/env python3
"""
How the host is holding up, in one line: memory still available, swap in use,
and load against the CPUs. With --oom: how many processes the kernel's OOM
killer has ended since boot (/proc/vmstat oom_kill).

Read by scripts/status.sh and by the morning tally in scripts/market-open.sh.
The server is a 2-vCPU, 8 GB box running the API, two dozen Python runners
and a build now and then; an out-of-memory kill there looks like a runner that
died for no reason.

Exit status: 0 fine, 1 tight (under 10% or 500 MB of memory available, swap
over half used, load over twice the CPUs; with --oom, any kill since boot),
2 when this machine has no /proc to read (a Mac: load only).
HOST_VITALS_PROC points it at another /proc (the tests' fixtures).
"""
from __future__ import annotations

import os
import sys
from typing import Optional

PROC = os.environ.get("HOST_VITALS_PROC", "/proc")
GB = 1024 * 1024  # /proc/meminfo counts kB


def _read(name: str) -> Optional[str]:
    try:
        with open(os.path.join(PROC, name), encoding="utf-8") as fh:
            return fh.read()
    except OSError:
        return None


def _fields(text: str) -> dict[str, int]:
    out: dict[str, int] = {}
    for line in text.splitlines():
        parts = line.replace(":", " ").split()
        if len(parts) >= 2 and parts[1].isdigit():
            out[parts[0]] = int(parts[1])
    return out


def _cpus() -> int:
    cpuinfo = _read("cpuinfo")
    if cpuinfo:
        n = sum(1 for line in cpuinfo.splitlines() if line.startswith("processor"))
        if n:
            return n
    return os.cpu_count() or 1


def vitals() -> tuple[int, str]:
    tight: list[str] = []
    parts: list[str] = []
    meminfo = _read("meminfo")
    if meminfo:
        m = _fields(meminfo)
        total, avail = m.get("MemTotal", 0), m.get("MemAvailable", 0)
        swap_total, swap_free = m.get("SwapTotal", 0), m.get("SwapFree", 0)
        pct = 100 * avail // total if total else 0
        parts.append(f"memory {avail / GB:.1f} GB available of {total / GB:.1f} GB ({pct}%)")
        if total and (pct < 10 or avail < 500 * 1024):
            tight.append("memory")
        if swap_total:
            used = swap_total - swap_free
            parts.append(f"swap {used / GB:.1f} of {swap_total / GB:.1f} GB used")
            if used * 2 > swap_total:
                tight.append("swap")
        else:
            parts.append("no swap")
    loadavg = _read("loadavg")
    if loadavg:
        loads = [float(x) for x in loadavg.split()[:3]]
    else:
        try:
            loads = list(os.getloadavg())
        except OSError:
            loads = []
    cpus = _cpus()
    if loads:
        parts.append(f"load {loads[0]:.2f} {loads[1]:.2f} {loads[2]:.2f} on {cpus} CPU(s)")
        if loads[0] > 2 * cpus:
            tight.append("load")
    if not meminfo:
        parts.append("(no /proc here: memory not shown)")
        return 2, " · ".join(parts)
    line = " · ".join(parts)
    return (1, f"{line} — tight: {', '.join(tight)}") if tight else (0, line)


def oom() -> tuple[int, str]:
    vmstat = _read("vmstat")
    if vmstat is None:
        return 2, "not known here (no /proc/vmstat)"
    kills = _fields(vmstat).get("oom_kill")
    if kills is None:
        return 2, "this kernel does not count them (no oom_kill in /proc/vmstat)"
    uptime = _read("uptime")
    since = ""
    if uptime:
        days, rest = divmod(int(float(uptime.split()[0])), 86400)
        since = f" (up {days} d {rest // 3600} h)"
    if kills:
        return 1, f"{kills} process(es) killed for memory since boot{since} — journalctl -k | grep -i 'killed process'"
    return 0, f"none since boot{since}"


def main(argv: list[str]) -> int:
    code, line = oom() if "--oom" in argv else vitals()
    print(line)
    return code


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
