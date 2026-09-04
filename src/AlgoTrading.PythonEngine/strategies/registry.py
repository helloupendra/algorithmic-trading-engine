"""
strategies/registry.py

Strategy discovery shared by the live runner (`execution_runner.py`) and the
offline catalog tool (`tools/list_strategies.py`).

Discovery walks the `strategies` package, imports every module and collects
the concrete `BaseStrategy` subclasses defined in it, keyed by their `name`
attribute. Private, parameterised factories from `private_strategies.py` are
layered on top so both callers see exactly the same map.

Importing this module (and running discovery) must stay side-effect free: no
Redis, broker or API connection is opened, so the catalog can be produced while
the platform is offline. The two runner-infrastructure modules that pull in
those clients are skipped explicitly; they never define a strategy.
"""

from __future__ import annotations

import importlib
import inspect
import os
import pkgutil
import traceback
from dataclasses import dataclass
from typing import Any, Callable, Dict, List, Optional

import strategies
from strategies.base_strategy import BaseStrategy

# Modules inside the package that are runner plumbing, not strategies. They are
# excluded so discovery does not import the Redis/broker/metrics stack.
NON_STRATEGY_MODULES = frozenset({
    "strategies.execution_runner",
    "strategies.contract_selector",
    "strategies.signal_utils",
})

StrategyFactory = Callable[..., BaseStrategy]


@dataclass
class DiscoveredStrategy:
    """A concrete strategy class found in the package."""
    name: str
    cls: type
    module_name: str
    source_file: str  # absolute path to the defining module


@dataclass
class DiscoveryError:
    """A module that could not be imported or inspected."""
    module_name: str
    source_file: Optional[str]
    error: str


def strategies_root() -> str:
    """Absolute path of the `strategies` package directory."""
    return os.path.dirname(os.path.abspath(strategies.__file__))


def relative_source(path: Optional[str]) -> str:
    """Path of a strategy module relative to the package, with forward slashes."""
    if not path:
        return ""
    try:
        rel = os.path.relpath(path, strategies_root())
    except ValueError:
        rel = path
    return rel.replace(os.sep, "/")


def _guess_module_file(info: pkgutil.ModuleInfo) -> Optional[str]:
    """Best-effort source path for a module that failed to import."""
    base = getattr(info.module_finder, "path", None)
    if not base:
        return None
    leaf = info.name.rsplit(".", 1)[-1]
    if info.ispkg:
        return os.path.join(base, leaf, "__init__.py")
    return os.path.join(base, leaf + ".py")


def discover_strategy_classes(errors: Optional[List[DiscoveryError]] = None) -> Dict[str, DiscoveredStrategy]:
    """
    Import every module under `strategies/` and return the strategy classes
    keyed by their `name` attribute (class name when `name` is missing).

    Import failures never raise: they are appended to `errors` (when given) so
    the caller can report them without losing the rest of the catalog.
    """
    found: Dict[str, DiscoveredStrategy] = {}
    sink: List[DiscoveryError] = errors if errors is not None else []

    def on_error(module_name: str) -> None:
        sink.append(DiscoveryError(module_name, None, traceback.format_exc().strip()))

    for info in pkgutil.walk_packages(strategies.__path__, strategies.__name__ + ".", onerror=on_error):
        if info.name in NON_STRATEGY_MODULES:
            continue
        try:
            module = importlib.import_module(info.name)
        except BaseException as ex:  # SystemExit in a module body must not kill discovery either
            sink.append(DiscoveryError(info.name, _guess_module_file(info), f"{type(ex).__name__}: {ex}"))
            continue

        try:
            source_file = os.path.abspath(getattr(module, "__file__", None) or _guess_module_file(info) or "")
            for _, obj in inspect.getmembers(module, inspect.isclass):
                if not issubclass(obj, BaseStrategy) or obj is BaseStrategy:
                    continue
                if obj.__module__ != info.name:
                    continue
                strategy_name = str(getattr(obj, "name", "") or obj.__name__)
                found[strategy_name] = DiscoveredStrategy(
                    name=strategy_name,
                    cls=obj,
                    module_name=info.name,
                    source_file=source_file,
                )
        except Exception as ex:
            sink.append(DiscoveryError(info.name, _guess_module_file(info), f"{type(ex).__name__}: {ex}"))

    return found


def discover_strategies() -> Dict[str, StrategyFactory]:
    """
    Name -> factory map for the auto-discovered strategy classes. Each factory
    accepts an optional params dict, matching the private factories' shape.
    """
    factories: Dict[str, StrategyFactory] = {}
    for name, entry in discover_strategy_classes().items():
        factories[name] = lambda params=None, cls=entry.cls: cls(params or {})
    return factories


def get_private_strategy_factories() -> Dict[str, StrategyFactory]:
    """Private/parameterised factories, or an empty map when the module is absent."""
    try:
        from strategies.private_strategies import get_private_strategies
    except ImportError:
        return {}
    try:
        return dict(get_private_strategies())
    except Exception:
        return {}


def load_strategy_factories() -> Dict[str, StrategyFactory]:
    """
    The complete name -> factory map used to launch a strategy: discovered
    classes first, private factories layered on top (they win on a name clash).
    """
    factories = discover_strategies()
    factories.update(get_private_strategy_factories())
    return factories


def normalise_doc(text: Optional[str]) -> str:
    """Collapse a docstring/description to a single line of plain text."""
    if not text:
        return ""
    return " ".join(str(text).split())


def describe_strategy(name: str, obj: Any, source_file: Optional[str] = None,
                      factory_params: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
    """
    Catalog entry (JSON-ready, camelCase) for a strategy class or instance.

    `obj` may be the class (auto-discovered strategies) or an instance built by
    a private factory; the metadata attributes are read the same way in both
    cases. `factory_params` are the private factory's defaults, which are merged
    over the class-level `default_params`.
    """
    cls = obj if inspect.isclass(obj) else type(obj)
    module_file = source_file or _class_source_file(cls)

    description = normalise_doc(getattr(obj, "description", "")) or normalise_doc(inspect.getdoc(cls))
    if not description:
        description = f"{name}: no description provided (add a `description` attribute to {cls.__name__})."

    underlyings = [str(u).upper() for u in (getattr(obj, "supported_underlyings", None) or [])]

    default_params: Dict[str, Any] = dict(getattr(obj, "default_params", None) or {})
    if factory_params:
        default_params.update(factory_params)

    requirements: List[Dict[str, str]] = []
    try:
        for req in obj.get_data_requirements() or []:
            requirements.append({
                "symbolType": str(getattr(req, "symbol_type", "")),
                "resolution": str(getattr(req, "resolution", "")),
            })
    except Exception as ex:
        requirements = []
        description = f"{description} (get_data_requirements failed: {ex})"

    # Contract requirements are read with params={} so the catalog shows the
    # declared defaults; the launch dialog then overrides them per run.
    contract_requirements: List[Dict[str, Any]] = []
    try:
        for req in obj.get_contract_requirements({}) or []:
            contract_requirements.append(_contract_requirement_entry(req))
    except Exception as ex:
        contract_requirements = []
        description = f"{description} (get_contract_requirements failed: {ex})"

    return {
        "name": name,
        "className": cls.__name__,
        "sourceFile": relative_source(module_file),
        "description": description,
        "category": str(getattr(obj, "category", "") or "Other"),
        "supportedUnderlyings": underlyings,
        "instrumentKind": str(getattr(obj, "instrument_kind", "") or "options"),
        "legsSummary": normalise_doc(getattr(obj, "legs_summary", "")),
        "defaultLots": BaseStrategy.lots_from({}, getattr(obj, "default_lots", 1)),
        "defaultParameters": default_params,
        "dataRequirements": requirements,
        "contractRequirements": contract_requirements,
        "createdUtc": _file_mtime_iso(module_file),
        "listed": bool(getattr(obj, "listed", True)),
    }


def _optional_number(value: Any) -> Optional[float]:
    """Float for a number, None for anything else (so JSON gets null, not 0)."""
    if value is None or isinstance(value, bool):
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def _contract_requirement_entry(req: Any) -> Dict[str, Any]:
    """One ContractRequirement as the catalog's camelCase object."""
    param = getattr(req, "param", None)
    return {
        "key": str(getattr(req, "key", "")),
        "optionType": str(getattr(req, "option_type", "") or "").upper(),
        "moneyness": str(getattr(req, "moneyness", "") or "atm").lower(),
        "steps": _optional_number(getattr(req, "steps", 0.0)) or 0.0,
        "points": _optional_number(getattr(req, "points", None)),
        "param": str(param) if param else None,
        "optional": bool(getattr(req, "optional", False)),
    }


def _class_source_file(cls: type) -> Optional[str]:
    try:
        return os.path.abspath(inspect.getfile(cls))
    except (TypeError, OSError):
        return None


def _file_mtime_iso(path: Optional[str]) -> Optional[str]:
    from datetime import datetime, timezone

    if not path:
        return None
    try:
        stamp = os.path.getmtime(path)
    except OSError:
        return None
    return datetime.fromtimestamp(stamp, tz=timezone.utc).isoformat()
