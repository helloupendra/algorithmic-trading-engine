"""
tools/list_strategies.py

Prints the strategy catalog as ONE JSON array on stdout and exits 0. The API's
StrategyCatalogService runs it (cwd = engine dir, PYTHONPATH = engine dir) to
build the strategy library, so:

  - nothing but the JSON array is ever written to stdout (diagnostics and any
    stray prints from strategy modules go to stderr);
  - a strategy that fails to import or inspect does not abort the listing --
    its entry carries an "error" field instead;
  - it works offline: discovery never touches Redis, the broker or the API.

Usage:
    python tools/list_strategies.py [--pretty] [--include-hidden]

Entry schema (camelCase, one object per strategy name):
    name, className, sourceFile, description, category, supportedUnderlyings,
    instrumentKind, legsSummary, defaultLots, defaultParameters,
    dataRequirements: [{symbolType, resolution}], createdUtc, [error]
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import traceback
from typing import Any, Dict, List

ENGINE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)


def _json_safe(value: Any) -> Any:
    """Round-trip through JSON so exotic parameter values become plain data."""
    return json.loads(json.dumps(value, default=str))


def _error_entry(name: str, source_file: str, error: str) -> Dict[str, Any]:
    return {
        "name": name,
        "className": "",
        "sourceFile": source_file,
        "description": f"Failed to load: {error.splitlines()[-1] if error else 'unknown error'}",
        "category": "Other",
        "supportedUnderlyings": [],
        "instrumentKind": "options",
        "legsSummary": "",
        "defaultLots": 1,
        "defaultParameters": {},
        "dataRequirements": [],
        "createdUtc": None,
        "error": error,
    }


def build_catalog(include_hidden: bool) -> List[Dict[str, Any]]:
    from strategies.registry import (
        DiscoveryError,
        describe_strategy,
        discover_strategy_classes,
        get_private_strategy_factories,
        relative_source,
    )

    entries: Dict[str, Dict[str, Any]] = {}
    errors: List[DiscoveryError] = []

    # 1. Auto-discovered classes: metadata is read from the class, no instance needed.
    for name, found in discover_strategy_classes(errors).items():
        try:
            entries[name] = describe_strategy(name, found.cls, found.source_file)
        except Exception as ex:
            print(f"[list_strategies] {name}: {ex}", file=sys.stderr)
            entries[name] = _error_entry(name, relative_source(found.source_file), traceback.format_exc().strip())

    # 2. Private factories: instantiate with {} so per-variant attributes are visible.
    for name, factory in get_private_strategy_factories().items():
        try:
            instance = factory({})
            factory_defaults = dict(getattr(factory, "defaults", None) or {})
            if not factory_defaults:
                # Older factories carry their defaults only in the instance's params.
                factory_defaults = dict(getattr(instance, "params", None) or {})
            entries[name] = describe_strategy(name, instance, None, factory_defaults)
        except Exception as ex:
            print(f"[list_strategies] {name}: {ex}", file=sys.stderr)
            entries[name] = _error_entry(name, "private_strategies.py", traceback.format_exc().strip())

    # 3. Modules that failed to import: surface them so a broken file is visible
    #    in the catalog instead of silently disappearing.
    for err in errors:
        leaf = err.module_name.rsplit(".", 1)[-1]
        print(f"[list_strategies] import failed for {err.module_name}: {err.error}", file=sys.stderr)
        if leaf not in entries:
            entries[leaf] = _error_entry(leaf, relative_source(err.source_file), err.error)

    catalog: List[Dict[str, Any]] = []
    for entry in entries.values():
        if not include_hidden and not entry.pop("listed", True):
            continue
        entry.pop("listed", None)
        entry["defaultParameters"] = _json_safe(entry.get("defaultParameters") or {})
        catalog.append(entry)

    catalog.sort(key=lambda e: (str(e.get("category", "")), str(e.get("name", ""))))
    return catalog


def main() -> int:
    parser = argparse.ArgumentParser(description="Print the strategy catalog as a JSON array.")
    parser.add_argument("--pretty", action="store_true", help="Indent the JSON output.")
    parser.add_argument("--include-hidden", action="store_true",
                        help="Also list strategies that set `listed = False`.")
    args = parser.parse_args()

    # Strategy modules may print during import/instantiation; keep stdout clean.
    real_stdout = sys.stdout
    sys.stdout = sys.stderr
    try:
        catalog = build_catalog(include_hidden=args.include_hidden)
    except Exception:
        # Discovery itself is broken (e.g. base_strategy does not import). Exit
        # non-zero so the API falls back to its file scan instead of showing an
        # empty library.
        traceback.print_exc(file=sys.stderr)
        sys.stdout = real_stdout
        return 1
    finally:
        sys.stdout = real_stdout

    text = json.dumps(catalog, indent=2 if args.pretty else None, ensure_ascii=False, default=str)
    sys.stdout.write(text + "\n")
    sys.stdout.flush()
    return 0


if __name__ == "__main__":
    sys.exit(main())
