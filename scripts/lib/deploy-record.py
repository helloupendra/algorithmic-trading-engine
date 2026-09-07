#!/usr/bin/env python3
"""
Appends one deploy record to data/deploy-history.json — the file the
Deployments page (DeployController.history) reads.

Same shape the Windows auto-deploy.ps1 writes, so the page shows both machines
the same way. Newest first, capped at 40: an unbounded file on a two-minute
schedule is a slow leak nobody notices until it is large.

Usage (from desk.sh):
  deploy-record.py --outcome ok --summary "..." --from abc1234 --to def5678 \
      --files 12 --started 2026-09-07T18:00:00Z \
      --commit "def5678 Fix the thing" --commit "..." \
      --step "Pulled from GitHub|ok|3 commits" --step "Console rebuilt|ok|..."
"""
import argparse, json, os, socket, sys
from datetime import datetime, timezone

ap = argparse.ArgumentParser()
ap.add_argument("--outcome", required=True)          # ok | failed | skipped
ap.add_argument("--summary", default="")
ap.add_argument("--from", dest="from_commit", default="")
ap.add_argument("--to", dest="to_commit", default="")
ap.add_argument("--files", type=int, default=0)
ap.add_argument("--started", default=None)
ap.add_argument("--commit", action="append", default=[])
ap.add_argument("--step", action="append", default=[])   # "name|status|detail"
a = ap.parse_args()

root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
path = os.path.join(root, "data", "deploy-history.json")

def step(spec):
    parts = (spec.split("|", 2) + ["", ""])[:3]
    return {"name": parts[0], "status": parts[1], "detail": parts[2]}

record = {
    "startedUtc": a.started or datetime.now(timezone.utc).isoformat(),
    "finishedUtc": datetime.now(timezone.utc).isoformat(),
    "outcome": a.outcome,
    "summary": a.summary,
    "fromCommit": a.from_commit,
    "toCommit": a.to_commit,
    "commits": a.commit,
    "filesChanged": a.files,
    "steps": [step(s) for s in a.step],
    "machine": socket.gethostname().split(".")[0],
}

history = []
try:
    with open(path) as f:
        existing = json.load(f)
        history = existing if isinstance(existing, list) else [existing]
except (FileNotFoundError, json.JSONDecodeError):
    pass

history = [record] + history
history = history[:40]
os.makedirs(os.path.dirname(path), exist_ok=True)
tmp = path + ".tmp"
with open(tmp, "w") as f:
    json.dump(history, f, indent=2)
os.replace(tmp, path)
print(f"deploy record written: {a.outcome} -> {a.to_commit or '-'} ({record['machine']})")
