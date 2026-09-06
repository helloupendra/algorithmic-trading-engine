"""
strategies/variants.py

Parameterised strategy factories that are registered under their own names
(e.g. "FulcrumMulti50" is FulcrumMultiStraddleStrategy with a half-strike threshold,
and "FulcrumMultiBuy50" is the same class going long instead of short).
The names are part of the API contract: the platform launches runs by name.

Each factory accepts an optional params dict (the run's parametersJson); user
values override the built-in defaults. The catalog tool instantiates every
factory with `{}` and reads the metadata attributes from the instance.
"""

from typing import Dict, Any, Callable

try:
    from strategies.fulcrum.fulcrum_standard import FulcrumStrategy
    from strategies.fulcrum.fulcrum_2_straddle_20 import Fulcrum2Straddle20Strategy
    from strategies.fulcrum.fulcrum_3_straddle_175 import Fulcrum3Straddle175Strategy
    from strategies.fulcrum.fulcrum_multi import FulcrumMultiStraddleStrategy
    from strategies.fulcrum.fulcrum_qty_adj import FulcrumQtyAdjustmentStrategy
    HAS_FULCRUM = True
except ImportError:
    HAS_FULCRUM = False


def get_parameterised_strategies() -> Dict[str, Callable[..., Any]]:
    if not HAS_FULCRUM:
        return {}

    def _make(cls, **defaults):
        factory = lambda p=None: cls(params={**defaults, **(p or {})})
        # Exposed so the catalog can report the factory's defaults without
        # having to diff the instance against the class.
        factory.defaults = dict(defaults)  # type: ignore[attr-defined]
        return factory

    # Thresholds are strike multiples, not points. On BANKNIFTY's 100-point grid
    # 0.5 IS the 50 these variants are named after; on NIFTY's 50-point grid it
    # is 25 points — the same fraction of the way to the next strike. No
    # strike_step is pinned any more: the platform reads the real grid off the
    # option chain, and a number here would be BANKNIFTY's applied to everything.
    sell = {
        "Fulcrum": _make(FulcrumStrategy),
        "Fulcrum2Straddle20": _make(Fulcrum2Straddle20Strategy, adjustment_steps=0.2),
        "Fulcrum3Straddle175": _make(Fulcrum3Straddle175Strategy),
        "FulcrumMulti50": _make(FulcrumMultiStraddleStrategy, adjustment_steps=0.5, minor_steps=0.1),
        "FulcrumMulti70": _make(FulcrumMultiStraddleStrategy, adjustment_steps=0.7, minor_steps=0.1),
        "FulcrumMulti90": _make(FulcrumMultiStraddleStrategy, adjustment_steps=0.9, minor_steps=0.1),
        "FulcrumQtyAdjustment": _make(FulcrumQtyAdjustmentStrategy, adjustment_steps=0.7, minor_steps=0.1),
    }

    # The same five classes, registered again as buyers. Long straddles instead
    # of short, and no wings — a long option's loss is already capped at the
    # premium paid, so a protective wing costs money and protects nothing.
    #
    # These are not mirror images in behaviour. A seller earns time decay and a
    # buyer pays it, so every roll that realises a profit for the sell variant
    # costs the buy variant premium. The thresholds are deliberately left the
    # same as their sell counterparts so the two can be compared on identical
    # data; widen them with `adjustment_steps` if the rolling proves too costly.
    buy = {
        "FulcrumBuy": _make(FulcrumStrategy, direction="BUY"),
        "Fulcrum2StraddleBuy20": _make(Fulcrum2Straddle20Strategy, adjustment_steps=0.2, direction="BUY"),
        "Fulcrum3StraddleBuy175": _make(Fulcrum3Straddle175Strategy, direction="BUY"),
        "FulcrumMultiBuy50": _make(FulcrumMultiStraddleStrategy, adjustment_steps=0.5, minor_steps=0.1, direction="BUY"),
        "FulcrumMultiBuy70": _make(FulcrumMultiStraddleStrategy, adjustment_steps=0.7, minor_steps=0.1, direction="BUY"),
        "FulcrumMultiBuy90": _make(FulcrumMultiStraddleStrategy, adjustment_steps=0.9, minor_steps=0.1, direction="BUY"),
        "FulcrumQtyAdjustmentBuy": _make(FulcrumQtyAdjustmentStrategy, adjustment_steps=0.7, minor_steps=0.1, direction="BUY"),
    }

    return {**sell, **buy}
