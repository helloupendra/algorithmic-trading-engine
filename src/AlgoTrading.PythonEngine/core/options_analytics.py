import math

def norm_cdf(x: float) -> float:
    """Cumulative distribution function for the standard normal distribution."""
    return (1.0 + math.erf(x / math.sqrt(2.0))) / 2.0

def norm_pdf(x: float) -> float:
    """Probability density function for the standard normal distribution."""
    return math.exp(-0.5 * x * x) / math.sqrt(2.0 * math.pi)

def calculate_d1_d2(S: float, K: float, T: float, r: float, sigma: float):
    if T <= 0:
        return 0.0, 0.0
    if sigma <= 0:
        sigma = 1e-5
    d1 = (math.log(S / K) + (r + 0.5 * sigma ** 2) * T) / (sigma * math.sqrt(T))
    d2 = d1 - sigma * math.sqrt(T)
    return d1, d2

def black_scholes_price(S: float, K: float, T: float, r: float, sigma: float, option_type: str) -> float:
    """Calculate theoretical option price."""
    if T <= 0:
        return max(0.0, S - K) if option_type == 'CE' else max(0.0, K - S)
        
    d1, d2 = calculate_d1_d2(S, K, T, r, sigma)
    
    if option_type == 'CE':
        return S * norm_cdf(d1) - K * math.exp(-r * T) * norm_cdf(d2)
    elif option_type == 'PE':
        return K * math.exp(-r * T) * norm_cdf(-d2) - S * norm_cdf(-d1)
    return 0.0

def implied_volatility(S: float, K: float, T: float, r: float, market_price: float, option_type: str) -> float:
    """Calculate Implied Volatility using Newton-Raphson method."""
    if T <= 0 or market_price <= 0:
        return 0.0
        
    MAX_ITER = 100
    PRECISION = 1.0e-5
    sigma = 0.3 # Initial guess (30%)
    
    for _ in range(MAX_ITER):
        price = black_scholes_price(S, K, T, r, sigma, option_type)
        vega = calculate_vega(S, K, T, r, sigma)
        
        diff = market_price - price
        
        if abs(diff) < PRECISION:
            return sigma
            
        if vega < 1e-5:
            break
            
        sigma = sigma + diff / vega
        
    return max(0.0, sigma)

def calculate_vega(S: float, K: float, T: float, r: float, sigma: float) -> float:
    if T <= 0: return 0.0
    d1, _ = calculate_d1_d2(S, K, T, r, sigma)
    return S * norm_pdf(d1) * math.sqrt(T) / 100.0 # Vega per 1% change

def calculate_greeks(S: float, K: float, T: float, r: float, sigma: float, option_type: str):
    """Calculate Delta, Gamma, Theta, Vega."""
    if T <= 0:
        return {"delta": 0.0, "gamma": 0.0, "theta": 0.0, "vega": 0.0}
        
    if sigma <= 0:
        sigma = 1e-5

    d1, d2 = calculate_d1_d2(S, K, T, r, sigma)
    
    gamma = norm_pdf(d1) / (S * sigma * math.sqrt(T))
    vega = calculate_vega(S, K, T, r, sigma)
    
    if option_type == 'CE':
        delta = norm_cdf(d1)
        theta = (- (S * norm_pdf(d1) * sigma) / (2 * math.sqrt(T)) - r * K * math.exp(-r * T) * norm_cdf(d2)) / 365.0
    elif option_type == 'PE':
        delta = norm_cdf(d1) - 1.0
        theta = (- (S * norm_pdf(d1) * sigma) / (2 * math.sqrt(T)) + r * K * math.exp(-r * T) * norm_cdf(-d2)) / 365.0
    else:
        delta = gamma = theta = 0.0
        
    return {
        "delta": delta,
        "gamma": gamma,
        "theta": theta,
        "vega": vega
    }

def analyze_option(S: float, K: float, T_days: float, r: float, market_price: float, option_type: str):
    """
    Main entry point for calculating IV and Greeks.
    T_days: Days to expiry.
    Returns: dict with iv, delta, gamma, theta, vega.
    """
    T_years = max(T_days / 365.0, 0.0001) # Avoid division by zero
    
    iv = implied_volatility(S, K, T_years, r, market_price, option_type)
    
    # If Newton-Raphson failed to converge or gave strange results, fallback
    if math.isnan(iv) or iv < 0.0 or iv > 5.0:
        iv = 0.0
        
    greeks = calculate_greeks(S, K, T_years, r, iv, option_type)
    greeks['iv'] = iv
    
    # Clean up NaN or Infinity due to extreme OTM
    for key in greeks:
        if math.isnan(greeks[key]) or math.isinf(greeks[key]):
            greeks[key] = 0.0
            
    return greeks
