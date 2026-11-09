# Python environments

Use CPython 3.12. Run commands from the repository root; local wheel links are
relative to that directory. Runtime dependencies, installer and maintenance tools
have separate input files and complete version/hash locks. Do not install a new
lock over the running production environment.

## Runtime

```sh
python3.12 -m venv .venv-next
.venv-next/bin/python -m pip install --require-hashes --only-binary=:all: -r src/AlgoTrading.PythonEngine/requirements-bootstrap.txt
.venv-next/bin/python -m pip install --require-hashes --only-binary=:all: -r src/AlgoTrading.PythonEngine/requirements.txt
.venv-next/bin/python -m pip check
```

The core profile includes broker adapters, feeds, Redis/PostgreSQL access, pricing,
backtesting and analytics. It excludes the large FinBERT inference stack. Windows
uses `.venv-next/Scripts/python.exe`. Setup scripts install this core profile.

## Optional news/research profile

The deployment lock targets CPython 3.12, Linux x86_64 and CPU inference:

```sh
.venv-next/bin/python -m pip install --require-hashes --only-binary=:all: -r src/AlgoTrading.PythonEngine/requirements-research-linux.txt
.venv-next/bin/python -m pip check
.venv-next/bin/python scripts/smoke_research.py
```

This includes the same pinned core dependencies. The torch wheel has a direct
URL and SHA-256 from the official PyTorch CPU index; other packages resolve only
from PyPI. No competing package index is added. The inference smoke uses tiny local
weights through the production scorer; it makes no model download or sentiment
quality claim. Production FinBERT weights remain pinned separately in `analysis/news.py`.
Other OS/architecture research profiles need their own generated and tested locks.
A core-only install reports that news scoring needs the optional profile.

## Audit and updates

```sh
python3.12 -m venv .audit-venv
.audit-venv/bin/python -m pip install --require-hashes --only-binary=:all: -r src/AlgoTrading.PythonEngine/requirements-tools.txt
.audit-venv/bin/python scripts/audit_python.py --python .venv-next/bin/python --output python-audit.json
.audit-venv/bin/python scripts/audit_python.py --python .venv-next/bin/python --format cyclonedx-json --output python-sbom.json
.audit-venv/bin/python scripts/lock_python.py
```

Review updated inputs, lock diffs and advisories, then repeat clean installation,
`pip check`, the complete Python suite, FYERS transport smoke tests and the Linux
research CI job. The [reviewed FYERS compatibility wheel](../vendor/python/README.md)
fixes upstream dependency pins explicitly. There are no advisory ignore IDs.
A clean scan reports known advisories at scan time; it cannot establish absence of
unknown vulnerabilities. CI uploads audit reports/SBOMs and fails on advisories.

## Deployment and rollback

Record the current deployed SHA, `pip freeze --all`, environment location, model
cache location and audit report. Keep that environment intact while building a
separate replacement at its final path (venv console scripts contain absolute paths).
At the normal quiet-desk release window, with runners stopped, switch the configured
Python executable, restart affected services, and verify feed/profile reads and
news scoring. Preserve the former interpreter path for rollback; never remove it
while a process may still need it. Reverting the Python executable and release SHA
restores the prior environment. A dependency install alone does not upgrade an
already-running process. Do not copy production credentials into build artifacts.
