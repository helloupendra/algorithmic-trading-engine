"""
One load-test trial of the Dhan feed's read path, end to end, in a Linux
container. Run it through matrix.sh (which pins the CPUs); by hand:

    python run_load.py --tree /trees/fix --label fix-L2-2000-1 --rate 2000 \
        --burners 2 --runners 23 --emulators filtered --out /out

LOCAL ONLY. The "Dhan" is fake_dhan.py on 127.0.0.1; the API is a fake inside
the feed process; Redis is a private redis-server. Nothing here connects to
Dhan, FYERS, Angel or TrueData, and it must never be run on the live server.

What runs, on LOAD_CLIENT_CPUS (the two vCPUs under test): the feed built from
--tree exactly as production builds it (feed_proc.py), redis-server behind a
socat proxy on 6380 (standing in for docker-proxy; the feed and the emulators
connect through it), --burners CPU-burner processes and --runners runner
emulators. On LOAD_SERVER_CPUS: the fake Dhan and the queue sampler.

Options for the cases in matrix.sh: --spinner (a thread in the feed process
busy 100% of the time), --gil-ms (FEED_GIL_SWITCH_MS), --redis-stop-at T
--redis-stop-for S (SIGSTOP/SIGCONT on redis-server), --cut-on-pong-late S.

Timeline: the fake Dhan starts sending once every instrument is subscribed
(t = 0), sends --seconds, then --drain seconds of pings only. Criteria are
judged on t = 10 s to --seconds. Output: <out>/<label>/result.json, the raw
logs beside it, and one PASS/FAIL line per criterion.
"""

import argparse
import json
import os
import shutil
import signal
import statistics
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
PY = sys.executable

# Pass criteria (section 4 of the build spec).
RECVQ_P99_MAX = 64 * 1024
RECVQ_MAX = 256 * 1024
LATE_MAX_MS = 200
PONG_P99_MS = 200
PONG_MAX_MS = 1000
DRAIN_SECONDS = 1.0
PROBE_P99_MS = 1500
STORED_SHARE = 0.95
BURST_MAX = 1024 * 1024
BURST_SETTLED = 64 * 1024
# The harness reproduces the incident when the baseline shows either of these.
BASELINE_RECVQ_MEDIAN = 256 * 1024
BASELINE_PONG_MS = 5000


def cpus(env):
    return {int(c) for c in os.environ.get(env, "").split(",") if c.strip()}


def pinned(cpu_set):
    def pin():
        if cpu_set and hasattr(os, "sched_setaffinity"):
            os.sched_setaffinity(0, cpu_set)
    return pin


def percentile(values, fraction):
    if not values:
        return None
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, int(len(ordered) * fraction))]


def read_jsonl(path):
    out = []
    try:
        with open(path) as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        out.append(json.loads(line))
                    except ValueError:
                        pass
    except FileNotFoundError:
        pass
    return out


def copy_tree(root, dest):
    """The engine and the seed data it reads, into a writable place (core.config makes logs/)."""
    ignore = shutil.ignore_patterns("__pycache__", "*.pyc", "tests", "backtest_reports", "research")
    shutil.copytree(os.path.join(root, "src", "AlgoTrading.PythonEngine"),
                    os.path.join(dest, "src", "AlgoTrading.PythonEngine"), ignore=ignore)
    seed = os.path.join(root, "src", "AlgoTrading.Api", "SeedData")
    if os.path.isdir(seed):
        shutil.copytree(seed, os.path.join(dest, "src", "AlgoTrading.Api", "SeedData"))
    os.makedirs(os.path.join(dest, "logs", "fyers"), exist_ok=True)
    return os.path.join(dest, "src", "AlgoTrading.PythonEngine")


def make_cert(work):
    cert, key = os.path.join(work, "cert.pem"), os.path.join(work, "key.pem")
    subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", key, "-out", cert,
                    "-days", "2", "-subj", "/CN=localhost",
                    "-addext", "subjectAltName=DNS:localhost,IP:127.0.0.1"], check=True, capture_output=True)
    return cert, key


def cpu_probe_ms(cpu_set):
    """
    How long a fixed pure-Python loop takes on one CPU under test, before the
    trial loads it: the same number means the same machine. On a shared host
    it is how a trial run on a starved VM is told apart from a slow feed.
    """
    one = {min(cpu_set)} if cpu_set else None
    started = time.perf_counter()
    subprocess.run([PY, "-c", "x = 0\nfor i in range(2_000_000):\n    x += i * i"], check=True,
                   preexec_fn=pinned(one) if one else None)
    return round((time.perf_counter() - started) * 1000)


def wait_for(condition, seconds, what):
    deadline = time.time() + seconds
    while time.time() < deadline:
        value = condition()
        if value:
            return value
        time.sleep(0.1)
    raise SystemExit(f"timed out waiting for {what}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tree", required=True, help="repository root of the tree under test")
    ap.add_argument("--label", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--rate", type=float, default=2000)
    ap.add_argument("--seconds", type=float, default=60)
    ap.add_argument("--drain", type=float, default=10)
    ap.add_argument("--burners", type=int, default=2)
    ap.add_argument("--runners", type=int, default=23)
    ap.add_argument("--emulators", choices=["filtered", "unfiltered"], default="unfiltered")
    ap.add_argument("--spinner", action="store_true")
    ap.add_argument("--gil-ms", default=None, help="FEED_GIL_SWITCH_MS for the feed (the fix reads it)")
    ap.add_argument("--redis-stop-at", type=float, default=None)
    ap.add_argument("--redis-stop-for", type=float, default=10)
    ap.add_argument("--cut-on-pong-late", type=float, default=0)
    ap.add_argument("--env", action="append", default=[], help="KEY=VALUE for the feed process")
    args = ap.parse_args()

    client, server = cpus("LOAD_CLIENT_CPUS"), cpus("LOAD_SERVER_CPUS")
    if server and hasattr(os, "sched_setaffinity"):
        os.sched_setaffinity(0, server)
    out_dir = os.path.join(args.out, args.label)
    os.makedirs(out_dir, exist_ok=True)
    work = tempfile.mkdtemp(prefix=f"load-{args.label}-")
    engine = copy_tree(args.tree, os.path.join(work, "tree"))
    cert, key = make_cert(work)

    env = dict(os.environ, REDIS_HOST="127.0.0.1", REDIS_PORT="6380", PYTHONUNBUFFERED="1",
               WEBSOCKET_CLIENT_CA_BUNDLE=cert, PYTHONDONTWRITEBYTECODE="1")
    for key_value in args.env:
        k, _, v = key_value.partition("=")
        env[k] = v
    if args.gil_ms is not None:
        env["FEED_GIL_SWITCH_MS"] = str(args.gil_ms)

    procs = {}
    on_client = pinned(client)
    events = {"cpu_probe_ms": cpu_probe_ms(client)}

    def start(name, cmd, log=None, client_side=True, extra_env=None):
        stdout = open(os.path.join(out_dir, log), "w") if log else subprocess.DEVNULL
        procs[name] = subprocess.Popen(cmd, stdout=stdout, stderr=subprocess.STDOUT, cwd=HERE,
                                       env=dict(env, **(extra_env or {})),
                                       preexec_fn=on_client if client_side else None)
        return procs[name]

    try:
        start("redis", ["redis-server", "--port", "6379", "--bind", "127.0.0.1", "--save", "", "--appendonly",
                        "no", "--dir", work], log="redis.log")
        wait_for(lambda: subprocess.run(["redis-cli", "-p", "6379", "ping"], capture_output=True,
                                        text=True).stdout.strip() == "PONG", 10, "redis")
        start("proxy", ["socat", "TCP-LISTEN:6380,fork,reuseaddr,bind=127.0.0.1", "TCP:127.0.0.1:6379"])
        wait_for(lambda: subprocess.run(["redis-cli", "-p", "6380", "ping"], capture_output=True,
                                        text=True).stdout.strip() == "PONG", 10, "the proxy")
        for i in range(args.burners):
            start(f"burner{i}", [PY, "-c", "while True:\n    pass"])

        port_file = os.path.join(work, "port")
        server_log = os.path.join(out_dir, "server.jsonl")
        start("server", [PY, "fake_dhan.py", "--cert", cert, "--key", key, "--port-file", port_file,
                         "--log", server_log, "--rate", str(args.rate), "--seconds", str(args.seconds),
                         "--drain", str(args.drain), "--cut-on-pong-late", str(args.cut_on_pong_late)],
              log="server.out", client_side=False)
        port = int(wait_for(lambda: os.path.exists(port_file) and open(port_file).read().strip(), 20, "the fake Dhan"))

        for i in range(args.runners):
            start(f"emu{i}", [PY, "runner_emulator.py", "--tree", engine, "--mode", args.emulators,
                              "--out", os.path.join(work, f"emu{i}.json")], log=f"emu{i}.log" if i == 0 else None)
        time.sleep(1.0)

        feed_cmd = [PY, "feed_proc.py", "--tree", engine, "--port", str(port), "--out", os.path.join(work, "feed.json")]
        if args.spinner:
            feed_cmd.append("--spinner")
        feed = start("feed", feed_cmd, log="feed.log")
        start("sampler", [PY, "queue_sampler.py", "--port", str(port), "--feed-pid", str(feed.pid),
                          "--log", os.path.join(out_dir, "sampler.jsonl")], client_side=False)

        def paced_start():
            for record in read_jsonl(server_log):
                if record.get("event") == "paced_start":
                    return record["t"]
            return None

        # Generous: 25 Python processes importing at once on two busy vCPUs
        # can take a minute or more to reach the point of subscribing.
        t0 = wait_for(paced_start, 300, "the fake Dhan to start sending (every instrument subscribed)")
        events["t0"] = t0
        if args.redis_stop_at is not None:
            time.sleep(max(0.0, t0 + args.redis_stop_at - time.time()))
            os.kill(procs["redis"].pid, signal.SIGSTOP)
            events["redis_stopped"] = time.time()
            time.sleep(args.redis_stop_for)
            os.kill(procs["redis"].pid, signal.SIGCONT)
            events["redis_continued"] = time.time()
        time.sleep(max(0.0, t0 + args.seconds + args.drain + 1.0 - time.time()))
    finally:
        # The measured processes write their reports on SIGTERM; everything
        # gets 15 s in all to go, then is killed.
        if "redis" in procs:
            try:
                os.kill(procs["redis"].pid, signal.SIGCONT)
            except OSError:
                pass
        reporting = ["feed"] + [n for n in procs if n.startswith("emu")] + ["sampler", "server"]
        for name in reporting:
            proc = procs.get(name)
            if proc and proc.poll() is None:
                proc.send_signal(signal.SIGTERM)
        deadline = time.time() + 15
        while time.time() < deadline and any(procs[n].poll() is None for n in reporting if n in procs):
            time.sleep(0.1)
        for name, proc in procs.items():
            if proc.poll() is None:
                if name in reporting:
                    proc.kill()          # had its SIGTERM and did not go
                else:
                    proc.terminate()
        for proc in procs.values():
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()

    result = analyse(args, work, out_dir, events)
    with open(os.path.join(out_dir, "result.json"), "w") as f:
        json.dump(result, f, indent=1)
    if os.path.exists(os.path.join(work, "feed.json")):
        shutil.copy(os.path.join(work, "feed.json"), os.path.join(out_dir, "feed.json"))
    print_result(result)
    shutil.rmtree(work, ignore_errors=True)


def analyse(args, work, out_dir, events):
    t0 = events.get("t0")
    window = (t0 + 10, t0 + args.seconds)
    server = read_jsonl(os.path.join(out_dir, "server.jsonl"))
    samples = read_jsonl(os.path.join(out_dir, "sampler.jsonl"))
    try:
        with open(os.path.join(work, "feed.json")) as f:
            feed = json.load(f)
    except (OSError, ValueError):
        feed = {}
    emulators = []
    for i in range(args.runners):
        try:
            with open(os.path.join(work, f"emu{i}.json")) as f:
                emulators.append(json.load(f))
        except (OSError, ValueError):
            pass
    try:
        with open(os.path.join(out_dir, "feed.log")) as f:
            feed_log = f.read()
    except OSError:
        feed_log = ""

    def within(t, lo=window[0], hi=window[1]):
        return lo <= t <= hi

    rx = [(s["t"], s["rx"]) for s in samples if s.get("rx") is not None]
    rx_window = [v for t, v in rx if within(t)]
    first_10 = [v for t, v in rx if within(t, window[0], window[0] + 10)]
    last_10 = [v for t, v in rx if within(t, window[1] - 10, window[1])]
    mid = [v for t, v in rx if within(t, t0 + 20, t0 + args.seconds)]

    pings = [r["ping"] for r in server if "ping" in r]
    pongs = {r["sent"]: r["pong_ms"] for r in server if "pong_ms" in r and "sent" in r}
    end_of_run = t0 + args.seconds + args.drain
    rtts = []
    unanswered = 0
    for sent in pings:
        if not within(sent):
            continue
        if sent in pongs:
            rtts.append(pongs[sent])
        else:
            unanswered += 1
            rtts.append(round((end_of_run - sent) * 1000, 1))   # at least this late
    late = [r["late_ms"] for r in server if "late_ms" in r and within(r["t"])]
    blocked = [r["blocked_ms"] for r in server if "blocked_ms" in r and within(r["t"])]
    sent_records = [r for r in server if "sent" in r and "late_ms" in r]
    sent_in_window = [r["sent"] for r in sent_records if within(r["t"])]
    frames_sent = (sent_in_window[-1] - sent_in_window[0]) if len(sent_in_window) > 1 else 0

    handshake = next((r["t"] for r in server if r.get("event") == "handshake"), None)
    burst = [v for t, v in rx if handshake and within(t, handshake, handshake + 10)]
    burst_late = [v for t, v in rx if handshake and within(t, handshake + 5, handshake + 10)]

    first_end = next((r for r in server if r.get("event") == "session_end"), {})
    last_data = first_end.get("last_data_at") or (t0 + args.seconds)
    drained_at = next((t for t, v in rx if t >= last_data and v == 0), None)
    drain_seconds = None if drained_at is None else round(drained_at - last_data, 2)

    probes = [lag for emu in emulators for t, lag in emu.get("probes", []) if within(t)]
    cuts = [r for r in server if r.get("event") == "cut"]

    stored = feed.get("stored_per_second") or {}
    stored_window = sum(n for second, n in stored.items() if within(int(second) + 0.5))
    instruments = len(feed.get("stored_per_symbol") or {}) or 313
    window_seconds = window[1] - window[0]
    covered = 0
    for seconds in (feed.get("symbol_seconds") or {}).values():
        covered += sum(1 for s in seconds if within(s + 0.5))

    cpu = [(s["t"], s["feed_cpu"]) for s in samples if s.get("feed_cpu") is not None and within(s["t"])]
    feed_cpu = (round(100 * (cpu[-1][1] - cpu[0][1]) / (cpu[-1][0] - cpu[0][0]), 1)
                if len(cpu) > 1 and cpu[-1][0] > cpu[0][0] else None)
    busy = [s["client_busy"] for s in samples if s.get("client_busy") is not None and within(s["t"])]

    connects = feed.get("connects") or []
    published = feed.get("published_times") or []
    resumed_after = None
    if "redis_continued" in events:
        after = [t for t in published if t >= events["redis_continued"]]
        resumed_after = round(after[0] - events["redis_continued"], 2) if after else None

    metrics = {
        "recvq_p99_kb": kb(percentile(rx_window, 0.99)),
        "recvq_max_kb": kb(max(rx_window) if rx_window else None),
        "recvq_mean_first10_kb": kb(statistics.mean(first_10) if first_10 else None),
        "recvq_mean_last10_kb": kb(statistics.mean(last_10) if last_10 else None),
        "recvq_median_20_60_kb": kb(statistics.median(mid) if mid else None),
        "server_late_max_ms": max(late) if late else None,
        "server_blocked_ms_total": round(sum(blocked), 1) if blocked else 0,
        "frames_sent_in_window": frames_sent,
        "frames_per_second_sent": round(frames_sent / window_seconds, 1) if window_seconds else None,
        "pong_p99_ms": percentile(rtts, 0.99),
        "pong_max_ms": max(rtts) if rtts else None,
        "pongs_unanswered": unanswered,
        "pings_in_window": len([p for p in pings if within(p)]),
        "drain_seconds": drain_seconds,
        "probe_p50_ms": percentile(probes, 0.5),
        "probe_p99_ms": percentile(probes, 0.99),
        "probe_samples": len(probes),
        "watchdog_restarts": len(feed.get("watchdogs") or []),
        "watchdog_texts": [w.get("text") for w in feed.get("watchdogs") or []][:5],
        "reconnects": max(0, len(connects) - 1),
        "rejected_ticks": feed_log.count("could not accept a tick"),
        "falling_behind_lines": feed_log.count("FALLING BEHIND"),
        "publish_errors": feed.get("publish_errors"),
        "stored_ticks_window": stored_window,
        "stored_ticks_needed": int(STORED_SHARE * instruments * window_seconds),
        "stored_symbol_seconds_share": round(covered / (instruments * window_seconds), 3) if instruments else None,
        "instruments_stored": instruments,
        "burst_max_kb": kb(max(burst) if burst else None),
        "burst_after_5s_max_kb": kb(max(burst_late) if burst_late else None),
        "feed_cpu_percent_of_one_vcpu": feed_cpu,
        "client_cpus_busy_percent": round(statistics.mean(busy), 1) if busy else None,
        "cuts": len(cuts),
        "resumed_after_sigcont_s": resumed_after,
        "ingest": feed.get("ingest"),
        "gil_switch_ms": round(feed["gil_switch_seconds"] * 1000, 2) if feed.get("gil_switch_seconds") else None,
        "emulators_reporting": len(emulators),
        "feed_stats": feed.get("feed_stats"),
        "cpu_probe_ms": events.get("cpu_probe_ms"),
        "gc_frozen_objects": feed.get("gc_frozen_objects"),
        "gc_gen2_in_window_ms": [ms for t, g, ms in feed.get("gc_events") or [] if g == 2 and within(t)],
        "slow_passes_in_window": len([1 for t, ms in feed.get("slow_passes") or [] if within(t)]),
        "slowest_pass_in_window_ms": max([ms for t, ms in feed.get("slow_passes") or [] if within(t)], default=None),
    }
    checks = criteria(args, metrics, events)
    return {"label": args.label, "tree": args.tree, "config": vars(args), "events": events,
            "metrics": metrics, "checks": checks}


def kb(value):
    return None if value is None else round(value / 1024, 1)


def criteria(args, m, events):
    """[(name, passed, value, limit)] — the fix must pass every one; the baseline is expected to fail."""
    def check(name, passed, value, limit):
        return {"name": name, "pass": bool(passed), "value": value, "limit": limit}

    recvq_trend_limit = max(16.0, 2 * (m["recvq_mean_first10_kb"] or 0))
    out = [
        check("client Recv-Q p99", m["recvq_p99_kb"] is not None and m["recvq_p99_kb"] <= 64, m["recvq_p99_kb"], "<= 64 KB"),
        check("client Recv-Q max", m["recvq_max_kb"] is not None and m["recvq_max_kb"] <= 256, m["recvq_max_kb"], "<= 256 KB"),
        check("Recv-Q trend (last 10 s mean)", m["recvq_mean_last10_kb"] is not None
              and m["recvq_mean_last10_kb"] <= recvq_trend_limit, m["recvq_mean_last10_kb"],
              f"<= {recvq_trend_limit:.0f} KB"),
        check("server schedule lateness max", m["server_late_max_ms"] is not None
              and m["server_late_max_ms"] <= LATE_MAX_MS, m["server_late_max_ms"], f"<= {LATE_MAX_MS} ms"),
        check("pong round trip p99", m["pong_p99_ms"] is not None and m["pong_p99_ms"] <= PONG_P99_MS,
              m["pong_p99_ms"], f"<= {PONG_P99_MS} ms"),
        check("pong round trip max", m["pong_max_ms"] is not None and m["pong_max_ms"] <= PONG_MAX_MS,
              m["pong_max_ms"], f"<= {PONG_MAX_MS} ms"),
        check("drain to 0 after the last frame", m["drain_seconds"] is not None
              and m["drain_seconds"] <= DRAIN_SECONDS, m["drain_seconds"], f"<= {DRAIN_SECONDS} s"),
        check("probe lag p99 (server -> runner)", m["probe_p99_ms"] is not None
              and m["probe_p99_ms"] <= PROBE_P99_MS, m["probe_p99_ms"], f"<= {PROBE_P99_MS} ms"),
        check("watchdog restarts", m["watchdog_restarts"] == 0, m["watchdog_restarts"], "0"),
        check("rejected ticks", m["rejected_ticks"] == 0, m["rejected_ticks"], "0"),
        check("stored ticks", m["stored_ticks_window"] >= m["stored_ticks_needed"], m["stored_ticks_window"],
              f">= {m['stored_ticks_needed']}"),
        check("snapshot burst max", m["burst_max_kb"] is not None and m["burst_max_kb"] < 1024, m["burst_max_kb"],
              "< 1024 KB"),
        check("snapshot burst after 5 s", m["burst_after_5s_max_kb"] is not None
              and m["burst_after_5s_max_kb"] < 64, m["burst_after_5s_max_kb"], "< 64 KB"),
    ]
    if args.redis_stop_at is None:
        out.append(check("publish errors", m["publish_errors"] == 0, m["publish_errors"], "0"))
    else:
        out.append(check("publish errors (Redis stopped)", (m["publish_errors"] or 0) > 0,
                         m["publish_errors"], "> 0, bounded"))
        out.append(check("stream resumes after SIGCONT", m["resumed_after_sigcont_s"] is not None
                         and m["resumed_after_sigcont_s"] <= 5, m["resumed_after_sigcont_s"], "<= 5 s"))
        out.append(check("no reconnect", m["reconnects"] == 0, m["reconnects"], "0"))
    if args.cut_on_pong_late:
        out.append(check("cut by the inferred Dhan cutoff", m["cuts"] == 0, m["cuts"], "0"))
    out.append({"name": "baseline reproduces the incident (validity)",
                "pass": bool((m["recvq_median_20_60_kb"] or 0) > BASELINE_RECVQ_MEDIAN / 1024
                             or (m["pong_max_ms"] or 0) > BASELINE_PONG_MS or m["cuts"] > 0),
                "value": {"recvq_median_20_60_kb": m["recvq_median_20_60_kb"], "pong_max_ms": m["pong_max_ms"],
                          "cuts": m["cuts"]},
                "limit": "median Recv-Q > 256 KB or pong > 5 s or cut", "informational": True})
    return out


def print_result(result):
    m = result["metrics"]
    print(f"== {result['label']}: {m['frames_per_second_sent']} frames/s sent, Recv-Q p99 {m['recvq_p99_kb']} KB "
          f"max {m['recvq_max_kb']} KB, pong p99 {m['pong_p99_ms']} ms max {m['pong_max_ms']} ms, probe p99 "
          f"{m['probe_p99_ms']} ms, feed CPU {m['feed_cpu_percent_of_one_vcpu']}%, stored {m['stored_ticks_window']}"
          f"/{m['stored_ticks_needed']}, restarts {m['watchdog_restarts']}, cuts {m['cuts']}, "
          f"cpu probe {m['cpu_probe_ms']} ms")
    for c in result["checks"]:
        tag = "INFO" if c.get("informational") else ("PASS" if c["pass"] else "FAIL")
        print(f"   {tag} {c['name']}: {c['value']} ({c['limit']})")


if __name__ == "__main__":
    main()
