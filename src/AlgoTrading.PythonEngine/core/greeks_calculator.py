from typing import Optional
from .data_models import OptionGreeks

try:
    from vollib.black_scholes_merton.implied_volatility import implied_volatility
    from vollib.black_scholes_merton.greeks.analytical import delta, gamma, theta, vega, rho
except ImportError:
    pass

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
