"""
analysis — forecasts with proof (docs/modules/analysis.md is the contract).

    python -m analysis backtest            walk-forward history, register the model versions
    python -m analysis issue --session D   write the day's forecasts before 09:15 IST
    python -m analysis score               score every forecast whose session has closed

The package forecasts three things about NIFTY, BANKNIFTY and SENSEX sessions:
the range (high − low as % of the previous close), whether the day trends, and
whether it closes above its open. Direction is there as a control: the desk's
research found nothing that predicts it, so a direction model that "works" is a
warning about the method before it is a finding about the market.

Modules:
    data      read-only Postgres: session bars, India VIX, expiry and holiday calendars
    models    the four models and their baselines; one fit + predict path for everything
    scoring   the outcome of a session and the forecast's loss against the baseline's
    backtest  expanding-window walk-forward, the registration payloads, the report
    issue     the morning's forecasts
    score     the evening's scores
    api       the Forecasts endpoints, through the engine's service-account session
"""
