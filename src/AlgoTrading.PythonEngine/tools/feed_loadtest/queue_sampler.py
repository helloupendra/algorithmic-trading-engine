"""
Samples the kernel's view of the feed socket every 100 ms, from outside the
feed process (Linux only; it reads /proc).

    python queue_sampler.py --port SERVER_PORT --feed-pid PID --log L

Per sample: the client socket's rx_queue (the Recv-Q `ss -tn` shows: bytes
the kernel holds that the feed has not read), the server socket's tx_queue,
the feed process's CPU seconds so far, and the busy share of the CPUs under
test. Pinned to LOAD_SERVER_CPUS, so sampling does not load what it measures.
"""

import argparse
import json
import os
import signal
import time

CLK_TCK = os.sysconf("SC_CLK_TCK") if hasattr(os, "sysconf") else 100


def queues(port: int):
    """(client rx_queue, server tx_queue) summed over established sockets on the server's port."""
    rx = tx = 0
    found = False
    for path in ("/proc/net/tcp", "/proc/net/tcp6"):
        try:
            with open(path) as f:
                next(f)
                for line in f:
                    parts = line.split()
                    local, remote, state, queue = parts[1], parts[2], parts[3], parts[4]
                    if state != "01":            # ESTABLISHED
                        continue
                    local_port = int(local.rsplit(":", 1)[1], 16)
                    remote_port = int(remote.rsplit(":", 1)[1], 16)
                    tx_q, rx_q = (int(x, 16) for x in queue.split(":"))
                    if remote_port == port:      # the feed's end
                        rx += rx_q
                        found = True
                    elif local_port == port:     # the fake Dhan's end
                        tx += tx_q
        except FileNotFoundError:
            continue
    return (rx if found else None), tx


def process_cpu_seconds(pid: int):
    try:
        with open(f"/proc/{pid}/stat") as f:
            fields = f.read().rsplit(")", 1)[1].split()
        return (int(fields[11]) + int(fields[12])) / CLK_TCK   # utime + stime
    except (OSError, IndexError, ValueError):
        return None


def cpu_busy(cpus):
    """{cpu: (busy ticks, total ticks)} for the named CPUs, from /proc/stat."""
    out = {}
    try:
        with open("/proc/stat") as f:
            for line in f:
                name = line.split(" ", 1)[0]
                if name.startswith("cpu") and name[3:].isdigit() and int(name[3:]) in cpus:
                    values = [int(v) for v in line.split()[1:]]
                    idle = values[3] + values[4]
                    out[int(name[3:])] = (sum(values) - idle, sum(values))
    except OSError:
        pass
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--feed-pid", type=int, required=True)
    ap.add_argument("--log", required=True)
    ap.add_argument("--every", type=float, default=0.1)
    args = ap.parse_args()
    server = os.environ.get("LOAD_SERVER_CPUS")
    if server and hasattr(os, "sched_setaffinity"):
        os.sched_setaffinity(0, {int(c) for c in server.split(",")})
    client_cpus = {int(c) for c in os.environ.get("LOAD_CLIENT_CPUS", "").split(",") if c.strip()}

    log = open(args.log, "a", buffering=1 << 16)
    signal.signal(signal.SIGTERM, lambda *_: (log.flush(), os._exit(0)))
    previous = cpu_busy(client_cpus)
    next_at = time.time()
    while True:
        rx, tx = queues(args.port)
        busy = cpu_busy(client_cpus)
        share = None
        if previous and busy:
            used = sum(busy[c][0] - previous[c][0] for c in busy if c in previous)
            total = sum(busy[c][1] - previous[c][1] for c in busy if c in previous)
            share = round(100.0 * used / total, 1) if total else None
        previous = busy
        log.write(json.dumps({"t": time.time(), "rx": rx, "tx": tx, "feed_cpu": process_cpu_seconds(args.feed_pid),
                              "client_busy": share}, separators=(",", ":")) + "\n")
        next_at += args.every
        delay = next_at - time.time()
        if delay > 0:
            time.sleep(delay)
        else:
            next_at = time.time()


if __name__ == "__main__":
    main()
