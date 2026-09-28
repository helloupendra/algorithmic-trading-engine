import sys
from typing import Optional
from .data_models import OptionGreeks

#: Why the Black-Scholes library could not be loaded, or None when it was.
#:
#: The import used to sit in `try: ... except ImportError: pass`. When it
#: failed, `implied_volatility` was simply undefined, calculate_greeks raised
#: NameError on every call, its own `except Exception` read that as "IV did not
#: converge", and every option on the desk carried null greeks with nothing
#: anywhere saying why. The failure is now kept, said once, and reported in the
#: feed's heartbeat (core/live/feed_runner.py).
IMPORT_ERROR: Optional[str] = None

try:
    from vollib.black_scholes_merton.implied_volatility import implied_volatility
    from vollib.black_scholes_merton.greeks.analytical import delta, gamma, theta, vega, rho
except Exception as _vollib_missing:  # noqa: BLE001 — a library that loads badly is as missing as an absent one
    try:
        # requirements.txt asks for py_vollib, unpinned. From 1.0.12 it installs
        # `vollib` too; before that `py_vollib` is the only name it has.
        # pyrefly: ignore [missing-import]
        from py_vollib.black_scholes_merton.implied_volatility import implied_volatility
        # pyrefly: ignore [missing-import]
        from py_vollib.black_scholes_merton.greeks.analytical import delta, gamma, theta, vega, rho
    except Exception as _py_vollib_missing:  # noqa: BLE001 — see above
        IMPORT_ERROR = f"{_vollib_missing}; {_py_vollib_missing}"

_unavailable_reported = False


def unavailable_reason() -> Optional[str]:
    """Why greeks cannot be computed in this process, or None when they can."""
    return IMPORT_ERROR


def _report_unavailable_once() -> None:
    global _unavailable_reported
    if _unavailable_reported:
        return
    _unavailable_reported = True
    print(f"GREEKS UNAVAILABLE: the Black-Scholes library could not be imported ({IMPORT_ERROR}). "
          f"Every option quote will carry no IV or greeks until it is installed "
          f"(pip install -r requirements.txt).", file=sys.stderr, flush=True)


def calculate_greeks(
    spot: float, 
    strike: float, 
    tte_years: float, 
    option_type: str, 
    option_price: float, 
    risk_free_rate: float = 0.05, 
    dividend_yield: float = 0.0
) -> Optional[OptionGreeks]:
    """
    Calculates Implied Volatility and Greeks using the Black-Scholes-Merton model.
    """
    if tte_years <= 0 or spot <= 0 or strike <= 0 or option_price <= 0:
        return None

    if IMPORT_ERROR is not None:
        _report_unavailable_once()
        return None
        
    flag = 'c' if option_type.upper() in ['C', 'CE', 'CALL'] else 'p'
    
    try:
        # 1. Calculate Implied Volatility
        iv = implied_volatility(
            option_price, 
            spot, 
            strike, 
            tte_years, 
            risk_free_rate, 
            dividend_yield, 
            flag
        )
        
        # Avoid exploding greeks if IV is completely unrealistic
        if iv <= 0 or iv > 5.0: 
            return None
            
        # 2. Calculate Greeks based on the derived IV
        # Arguments: flag, S, K, t, r, sigma, q
        d = delta(flag, spot, strike, tte_years, risk_free_rate, iv, dividend_yield)
        g = gamma(flag, spot, strike, tte_years, risk_free_rate, iv, dividend_yield)
        t = theta(flag, spot, strike, tte_years, risk_free_rate, iv, dividend_yield)
        v = vega(flag, spot, strike, tte_years, risk_free_rate, iv, dividend_yield)
        r = rho(flag, spot, strike, tte_years, risk_free_rate, iv, dividend_yield)
        
        return OptionGreeks(
            iv=iv,
            delta=d,
            gamma=g,
            theta=t,
            vega=v,
            rho=r
        )
    except Exception:
        # Happens if IV fails to converge (e.g., deep ITM/OTM with weird pricing)
        return None
