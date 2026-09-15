"""
research/

Offline research tooling for option-buying ideas: a market regime classifier,
read-only data access, a fast candidate simulator with walk-forward evaluation,
and a self-contained HTML report.

Nothing in this package is imported by the live runner, the replay engine or
any strategy. It reuses `strategies.indicators` and `backtest.timeutil` by
import and never changes them. Candidates here are research ideas, not live
strategies; promoting one means writing a real `BaseStrategy` with a spec.

Reports go to `private/research/` (gitignored) and are never published.
"""
