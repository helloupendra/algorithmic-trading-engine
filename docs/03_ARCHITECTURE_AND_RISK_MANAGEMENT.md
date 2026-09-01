# Architecture & Risk Management Guide

This document covers two critical pieces of the enterprise architecture: The **Global Kill Switch** and the **State Recovery System**.

## 1. Global Risk Management (The Kill Switch)

The `RiskManagementService` is built directly into the C# Backend (`AlgoTrading.Infrastructure.Services.RiskManagementService`). It acts as a gatekeeper between the Python Engine and the Broker.

Every single `CreateSimulationSignal` request hits the `EvaluateOrderAsync()` method before a paper order or real order is ever generated.

### Automatic Enforcement
By default, the Risk Management Service enforces:
- **Max Orders Per Minute (OPM)**: Configured in `appsettings.json`. If a strategy goes haywire and emits an infinite loop of signals, the C# API automatically rejects them to prevent API bans.
- **Max Daily Loss**: The engine aggregates the total Realized + Unrealized PnL of all open positions. If this drops below your defined threshold, it immediately throws a `RiskViolationException`.

### Triggering the Global Kill Switch

If the market is crashing or your strategy is behaving unexpectedly, you can trigger the global kill switch to instantly halt everything.

**Kill Switch Activation Endpoint:**
`POST http://localhost:5025/api/Risk/killswitch/activate`

**What it does:**
1. It immediately sets the global `_isKillSwitchActive` flag to `true`.
2. It rejects ANY incoming signal from ANY strategy.
3. It iterates through **every single open position** in `PaperTradingService.FlattenAllPositionsAsync()` and automatically emits market orders to close them out.

**Kill Switch Deactivation Endpoint:**
`POST http://localhost:5025/api/Risk/killswitch/deactivate`
Allows trading to resume.

---

## 2. Python Engine State Recovery

The Python Engine uses **Redis** to persist its exact internal state so that it is fault-tolerant to crashes, power outages, and manual restarts.

### How it works:
1. When you run `execution_runner.py --strategy Titli --user-id 1 --run-id 50`, it checks Redis for the key `strategy:state:50`.
2. If the key exists, it overrides the `Titli` strategy's internal variables (`st0`, `st1`, `current_group_id`, etc.) with the snapshot from Redis.
3. If the key does not exist, it starts fresh.
4. During execution, it acquires a **Distributed Lock** (`strategy:lock:50`). If you accidentally spin up two terminal windows with the same `--run-id`, the second one will immediately crash with an error to prevent duplicate orders.
5. At the end of every 1-second market tick, the Python engine saves the entire strategy dictionary back into Redis.

### Viewing the State
If you want to manually inspect the state of a running strategy, open Redis CLI or a tool like RedisInsight:
```bash
> GET strategy:state:50
```
This will return a JSON blob containing the exact state of your straddles.
