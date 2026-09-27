"""Every watcher Sentinel runs. Adding one is a module here and a line below."""
from __future__ import annotations

from sentinel.agents.base import Agent
from sentinel.agents.health import HealthAgent
from sentinel.agents.logs import LogsAgent
from sentinel.agents.security import SecurityAgent
from sentinel.agents.trading import TradingAgent


def all_agents() -> list[Agent]:
    # Imported here: the checkup builds on the trading agent's run parsing.
    from sentinel.checkup.agent import CheckupAgent

    return [HealthAgent(), TradingAgent(), LogsAgent(), SecurityAgent(), CheckupAgent()]


__all__ = ["Agent", "all_agents", "HealthAgent", "TradingAgent", "LogsAgent", "SecurityAgent"]
