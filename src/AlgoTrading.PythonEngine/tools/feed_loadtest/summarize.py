"""
One table from a matrix run's results: python summarize.py OUT_DIR

Per trial: frames/s sent, the socket's Recv-Q (p99/max), pong round trip
(p99/max), probe lag p99, feed CPU, stored ticks against the target, watchdog
restarts, cuts, and how many criteria passed. Then per case, the worst trial.
"""

import glob
import json
import os
import sys


def row(result):
    m = result["metrics"]
    checks = [c for c in result["checks"] if not c.get("informational")]
    failed = [c["name"] for c in checks if not c["pass"]]
    valid = next((c for c in result["checks"] if c.get("informational")), {})
    return {
        "label": result["label"],
        "sent/s": m["frames_per_second_sent"],
        "recvq p99/max KB": f"{m['recvq_p99_kb']}/{m['recvq_max_kb']}",
        "recvq 20-60 med KB": m["recvq_median_20_60_kb"],
        "pong p99/max ms": f"{m['pong_p99_ms']}/{m['pong_max_ms']}",
        "late max ms": m["server_late_max_ms"],
        "probe p99 ms": m["probe_p99_ms"],
        "feed cpu %": m["feed_cpu_percent_of_one_vcpu"],
        "busy %": m["client_cpus_busy_percent"],
        "stored": f"{m['stored_ticks_window']}/{m['stored_ticks_needed']}",
        "restarts": m["watchdog_restarts"],
        "cuts": m["cuts"],
        "pub err": m["publish_errors"],
        "gen2 GC ms": ",".join(f"{v:.0f}" for v in m.get("gc_gen2_in_window_ms") or []) or "-",
        "cpu probe ms": m.get("cpu_probe_ms"),
        "passed": f"{len(checks) - len(failed)}/{len(checks)}",
        "incident reproduced": "yes" if valid.get("pass") else "no",
        "failed": ", ".join(failed),
    }


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "."
    results = []
    for path in sorted(glob.glob(os.path.join(out, "*", "result.json"))):
        with open(path) as f:
            results.append(json.load(f))
    if not results:
        print(f"no result.json under {out}")
        return
    rows = [row(r) for r in results]
    columns = list(rows[0])
    widths = {c: max(len(c), *(len(str(r[c])) for r in rows)) for c in columns if c != "failed"}
    print(" | ".join(c.ljust(widths[c]) for c in widths))
    for r in rows:
        print(" | ".join(str(r[c]).ljust(widths[c]) for c in widths))
    print()
    for r in rows:
        if r["failed"]:
            print(f"{r['label']}: FAILED {r['failed']}")


if __name__ == "__main__":
    main()
