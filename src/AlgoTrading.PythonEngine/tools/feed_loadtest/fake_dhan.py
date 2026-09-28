"""
A fake Dhan feed server for the load test. LOCAL ONLY: it binds 127.0.0.1 and
nothing in the load test ever connects to Dhan, FYERS, Angel or TrueData.

    python fake_dhan.py --cert C --key K --port-file F --log L --rate 2000 --seconds 60 --drain 10

What it does, like Dhan as far as the feed can tell:
  * TLS, a websocket 101 handshake, then reads the client's subscribe JSON
    (masked client frames) and answers each subscription with a snapshot: a
    previous-close packet and the instrument's packet;
  * once every instrument is subscribed, sends `rate` frames a second of the
    universe's mix (options Full 3/s, MCX Full 5/s, index Quote 2/s, equity
    Quote 1/s, scaled) in 10 ms slots, ONE TLS RECORD PER FRAME (one send per
    frame), as Dhan does;
  * a ping every 2 s whose payload is the time it was queued, in order behind
    the data, and the pong's round trip — the reader lag Dhan itself sees;
  * the probe index every 2 s, priced at its own send time;
  * with --cut-on-pong-late S, stops sending (without closing) once a pong is
    more than S seconds late: the inferred Dhan cutoff.

One thread, non-blocking: Python's SSL objects must not be read and written
from two threads at once. Every 100 ms it logs frames sent, bytes, frames
queued behind a full window, how late the oldest queued frame is, and the ms
it spent unable to write. Pinned to LOAD_SERVER_CPUS: Dhan is not on our box.
"""

import argparse
import base64
import collections
import hashlib
import json
import os
import select
import signal
import socket
import ssl
import struct
import time

import universe as U

GUID = b"258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
SLOT = 0.01
PING_EVERY = 2.0
PROBE_EVERY = 2.0


def ws_frame(payload: bytes, opcode: int = 0x2) -> bytes:
    n = len(payload)
    if n <= 125:
        return bytes([0x80 | opcode, n]) + payload
    if n <= 0xFFFF:
        return bytes([0x80 | opcode, 126]) + struct.pack("!H", n) + payload
    return bytes([0x80 | opcode, 127]) + struct.pack("!Q", n) + payload


def pin(env):
    cpus = os.environ.get(env)
    if cpus and hasattr(os, "sched_setaffinity"):
        os.sched_setaffinity(0, {int(c) for c in cpus.split(",")})


def ltt_now():
    """A trade time as Dhan writes it: IST wall-clock seconds as an epoch."""
    return int(time.time()) + 19800


class ClientFrames:
    """Parses what the client sends: masked frames, possibly split across reads."""

    def __init__(self):
        self.buf = bytearray()

    def feed(self, data):
        self.buf += data
        frames = []
        while True:
            if len(self.buf) < 2:
                break
            b0, b1 = self.buf[0], self.buf[1]
            opcode, masked, n = b0 & 0x0F, b1 & 0x80, b1 & 0x7F
            pos = 2
            if n == 126:
                if len(self.buf) < 4:
                    break
                n = struct.unpack_from("!H", self.buf, 2)[0]
                pos = 4
            elif n == 127:
                if len(self.buf) < 10:
                    break
                n = struct.unpack_from("!Q", self.buf, 2)[0]
                pos = 10
            mask = b""
            if masked:
                if len(self.buf) < pos + 4:
                    break
                mask = bytes(self.buf[pos:pos + 4])
                pos += 4
            if len(self.buf) < pos + n:
                break
            payload = bytes(self.buf[pos:pos + n])
            if masked:
                payload = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
            del self.buf[:pos + n]
            frames.append((opcode, payload))
        return frames


class Log:
    def __init__(self, path):
        self.f = open(path, "a", buffering=1 << 16)

    def write(self, **record):
        record.setdefault("t", time.time())
        self.f.write(json.dumps(record, separators=(",", ":")) + "\n")
        if "event" in record:
            # The orchestrator waits on these (paced_start above all): never
            # leave one sitting in the buffer.
            self.f.flush()

    def flush(self):
        self.f.flush()


def build_cycle(universe, rate):
    """One second of the mix, time-ordered, as websocket frames; each instrument at its scaled rate."""
    rates = U.scaled_rates(universe, rate)
    ltt = ltt_now()
    events = []
    for inst in universe:
        per_second = rates[inst["kind"]]
        count = max(1, round(per_second))
        phase = (hash(inst["canonical"]) % 1000) / 1000.0 / count
        for k in range(count):
            events.append((phase + k / count, ws_frame(U.packet_for(inst, ltt, 0.0001 * k))))
    events.sort(key=lambda e: e[0])
    return [frame for _, frame in events]


class Session:
    """One accepted connection, from handshake to close."""

    def __init__(self, conn, args, universe, log, number, clock):
        self.conn = conn
        self.args = args
        self.universe = universe
        self.by_key = {(inst["seg"], inst["sid"]): inst for inst in universe}
        self.log = log
        self.number = number
        # {"deadline": epoch}: when paced sending stops, set by the first
        # session's paced start and kept by any session after a reconnect.
        self.clock = clock
        self.out = collections.deque()        # (scheduled epoch, bytes)
        self.parser = ClientFrames()
        self.subscribed = set()
        self.cycle = build_cycle(universe, args.rate)
        self.pings = {}                         # payload -> queued epoch
        self.cut = False
        self.closed = False

    # ------------------------------------------------------------- handshake
    def handshake(self):
        self.conn.settimeout(10)
        request = b""
        while b"\r\n\r\n" not in request:
            chunk = self.conn.recv(4096)
            if not chunk:
                raise ConnectionError("closed during the handshake")
            request += chunk
        head, _, rest = request.partition(b"\r\n\r\n")
        key = [line.split(b":", 1)[1].strip() for line in head.split(b"\r\n")
               if line.lower().startswith(b"sec-websocket-key")][0]
        accept = base64.b64encode(hashlib.sha1(key + GUID).digest())
        self.conn.sendall(b"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                          b"Sec-WebSocket-Accept: " + accept + b"\r\n\r\n")
        if rest:
            self._handle_client(self.parser.feed(rest))
        self.conn.setblocking(False)
        self.log.write(event="handshake", session=self.number)

    # --------------------------------------------------------------- reading
    def _read(self):
        while True:
            try:
                data = self.conn.recv(65536)
            except (ssl.SSLWantReadError, BlockingIOError):
                return
            except ssl.SSLWantWriteError:
                return
            except (ssl.SSLError, OSError):
                self.closed = True
                return
            if not data:
                self.closed = True
                return
            self._handle_client(self.parser.feed(data))

    def _handle_client(self, frames):
        now = time.time()
        for opcode, payload in frames:
            if opcode == 0x1:                   # a subscribe request
                try:
                    request = json.loads(payload)
                except ValueError:
                    continue
                ltt = ltt_now()
                for item in request.get("InstrumentList") or []:
                    key = (U.SEGMENT_NUMBERS.get(item.get("ExchangeSegment")), int(item.get("SecurityId", -1)))
                    if key in self.subscribed:
                        continue
                    self.subscribed.add(key)
                    inst = self.by_key.get(key)
                    if inst is not None and not self.cut:
                        # Dhan answers a subscription with the instrument's state at once.
                        self.out.append((now, ws_frame(U.pk_prev_close(inst["seg"], inst["sid"], inst["price"]))))
                        self.out.append((now, ws_frame(U.packet_for(inst, ltt))))
            elif opcode == 0xA:                 # a pong
                if len(payload) == 8:
                    (sent,) = struct.unpack("!d", payload)
                    self.pings.pop(sent, None)
                    self.log.write(pong_ms=round((now - sent) * 1000, 1), sent=sent, session=self.number)
            elif opcode == 0x8:
                self.closed = True

    # --------------------------------------------------------------- running
    def run(self):
        """Wait for the subscriptions, send the paced mix, drain; returns when done or closed."""
        wanted = len(self.universe) + 1
        waiting_since = time.time()
        while not self.closed and len(self.subscribed) < wanted and time.time() - waiting_since < 15:
            self._write()
            select.select([self.conn], [self.conn] if self.out else [], [], 0.05)
            self._read()
        if self.closed:
            return
        start = time.time()
        if self.clock["deadline"] is None:
            self.clock["deadline"] = start + self.args.seconds
        self.log.write(event="paced_start", session=self.number, subscribed=len(self.subscribed),
                       frames_per_second=self.args.rate, snapshot_frames=len(self.out))
        stop_sending = self.clock["deadline"]
        end = stop_sending + self.args.drain
        per_slot = self.args.rate * SLOT
        owed, cursor, slot = 0.0, 0, 0
        next_ping = start + PING_EVERY
        next_probe = start + 0.5
        next_log = start + 0.1
        sent = sent_bytes = 0
        interval_late = interval_blocked = 0.0
        blocked_since = None
        last_data_at = None
        while not self.closed:
            now = time.time()
            if now >= end:
                break
            # Queue whatever the schedule says is due.
            while not self.cut and start + slot * SLOT <= now < stop_sending:
                due = start + slot * SLOT
                owed += per_slot
                k = int(owed)
                owed -= k
                for _ in range(k):
                    self.out.append((due, self.cycle[cursor % len(self.cycle)]))
                    cursor += 1
                slot += 1
            if not self.cut and now >= next_probe and now < stop_sending:
                self.out.append((now, ws_frame(U.probe_packet(int(now * 1000), ltt_now()))))
                next_probe += PROBE_EVERY
            if not self.cut and now >= next_ping:
                payload = struct.pack("!d", now)
                self.pings[now] = now
                self.out.append((now, ws_frame(payload, opcode=0x9)))
                self.log.write(ping=now, session=self.number)
                next_ping += PING_EVERY
            # The cutoff Dhan is inferred to apply.
            if (self.args.cut_on_pong_late and not self.cut and self.pings
                    and now - min(self.pings.values()) > self.args.cut_on_pong_late):
                self.cut = True
                self.out.clear()
                self.log.write(event="cut", session=self.number, oldest_ping_age=round(now - min(self.pings.values()), 1))

            wrote, blocked = self._write_counted()
            sent += wrote[0]
            sent_bytes += wrote[1]
            if wrote[0]:
                last_data_at = time.time()
            if blocked and blocked_since is None:
                blocked_since = time.time()
            elif not blocked and blocked_since is not None:
                interval_blocked += time.time() - blocked_since
                blocked_since = None
            if self.out:
                interval_late = max(interval_late, time.time() - self.out[0][0])
            self._read()

            if now >= next_log:
                if blocked_since is not None:
                    interval_blocked += now - blocked_since
                    blocked_since = now
                self.log.write(sent=sent, bytes=sent_bytes, queued=len(self.out), late_ms=round(interval_late * 1000, 1),
                               blocked_ms=round(interval_blocked * 1000, 1), session=self.number,
                               sending=int(now < stop_sending and not self.cut))
                interval_late = interval_blocked = 0.0
                next_log += 0.1

            wait = min(start + slot * SLOT, next_ping, next_log) - time.time()
            if wait > 0:
                select.select([self.conn], [self.conn] if self.out else [], [], wait)
        self.log.write(event="session_end", session=self.number, sent=sent, bytes=sent_bytes, queued=len(self.out),
                       closed_by_client=self.closed, cut=self.cut, last_data_at=last_data_at)
        self.log.flush()

    def _write(self):
        return self._write_counted()[1]

    def _write_counted(self):
        """Write queued frames, one send (one TLS record) each, until the window is full."""
        frames = sent_bytes = 0
        while self.out:
            data = self.out[0][1]
            try:
                self.conn.send(data)
            except (ssl.SSLWantWriteError, ssl.SSLWantReadError, BlockingIOError):
                return (frames, sent_bytes), True
            except (ssl.SSLError, OSError):
                self.closed = True
                return (frames, sent_bytes), False
            self.out.popleft()
            frames += 1
            sent_bytes += len(data)
        return (frames, sent_bytes), False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cert", required=True)
    ap.add_argument("--key", required=True)
    ap.add_argument("--port-file", required=True)
    ap.add_argument("--log", required=True)
    ap.add_argument("--rate", type=float, default=2000)
    ap.add_argument("--seconds", type=float, default=60)
    ap.add_argument("--drain", type=float, default=10)
    ap.add_argument("--cut-on-pong-late", type=float, default=0)
    args = ap.parse_args()
    pin("LOAD_SERVER_CPUS")

    universe = U.build_universe()
    log = Log(args.log)
    signal.signal(signal.SIGTERM, lambda *_: (log.flush(), os._exit(0)))

    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.load_cert_chain(args.cert, args.key)
    listener = socket.socket()
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(("127.0.0.1", 0))
    listener.listen(4)
    port = listener.getsockname()[1]
    with open(args.port_file + ".tmp", "w") as f:
        f.write(str(port))
    os.replace(args.port_file + ".tmp", args.port_file)
    log.write(event="listening", port=port, universe=len(universe))
    log.flush()

    # The run's clock starts with the first session's paced sending; a session
    # after a reconnect only sends until the same deadline.
    clock = {"deadline": None}
    number = 0
    listener.settimeout(1.0)
    hard_stop = time.time() + 360 + args.seconds + args.drain

    def over():
        return time.time() > hard_stop or (clock["deadline"] is not None
                                           and time.time() > clock["deadline"] + args.drain)

    while not over():
        try:
            raw, _ = listener.accept()
        except socket.timeout:
            continue
        number += 1
        try:
            conn = ctx.wrap_socket(raw, server_side=True)
        except (ssl.SSLError, OSError) as ex:
            log.write(event="tls_error", session=number, error=repr(ex))
            continue
        session = Session(conn, args, universe, log, number, clock)
        try:
            session.handshake()
            session.run()
            if session.cut and not session.closed:
                # Cut, not closed: the socket stays open and silent until the
                # client gives up on it, as the inferred cutoff would.
                while not over() and not session.closed:
                    select.select([conn], [], [], 0.5)
                    session._read()
        except Exception as ex:  # noqa: BLE001
            log.write(event="session_error", session=number, error=repr(ex))
        finally:
            try:
                conn.close()
            except OSError:
                pass
        log.flush()
    log.write(event="server_done")
    log.flush()


if __name__ == "__main__":
    main()
