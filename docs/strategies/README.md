# Strategy specifications

Every strategy the engine can launch has a Markdown specification in this
folder, named after its registry name: `docs/strategies/<StrategyName>.md`,
where `<StrategyName>` is exactly the key `strategies.registry.load_strategy_factories()`
returns (the class's `name` attribute, or the factory name in `variants.py`).
Nothing else lives here: the test treats any other `.md` file as a spec for a
strategy that no longer exists (usually a rename) and fails.
The console renders the file on the Strategies → Library page (GET
`/api/Strategy/{id}/spec`), so what is written here is what a trader reads
before starting a run.

Why a document and not the code: a reviewer has to be able to check the code
against a statement of what it is supposed to do, and a trader has to know
what a run will do to their capital without reading Python. The spec is that
statement. It is enforced by
`src/AlgoTrading.PythonEngine/tests/test_strategy_specs.py`: **a strategy is
not done until its spec exists.** The test reads the registry, so a new
strategy fails the suite the moment it is discoverable and has no spec.

## The rules

- English, precise, no marketing. A number that cannot be traced to a row or
  a line of code does not go in.
- Explain *why*, in the voice of the code comments: the reader is a
  reviewer, not a customer.
- Use the strategy's registry name and the parameter names from
  `default_params`. Never invent a name for something the code calls
  differently.
- Headings are fixed: the H2 headings below, in this order, and only these
  H2 headings. Sub-structure goes under H3.

## Template

Copy this and fill every section. The heading text must match exactly.

```markdown
# <StrategyName>

## Idea
## Data it needs
## Timeframe
## Entry
## Position management
## Exit
## Parameters
## Worked example
## Limitations
## Facts (machine-readable)
```

### `## Idea`

Two or three sentences: what market behaviour the strategy trades and why
that behaviour should pay. If the honest answer is "it is an example", say so.

### `## Data it needs`

A table, one row per feed:

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|

"What" is one of: ticks, index candles, option candles, option chain OI.
"Where" is one of: ingestor (live ticks and `live_bars`), backfill (`candles`
from the broker's history endpoint), chain poller (`option_chain_snapshots`).
The history column is what the strategy needs to warm up (e.g. "40 closed 5m
bars for the EMA") — the runner cannot start earlier than that.

### `## Timeframe`

The bar it evaluates on; whether it decides on bar close or on every tick;
the session window it trades in (first and last entry time); and what
happens at 15:30 — the platform squares off every open position at market
close (`MarketHoursService`, "Market closed (15:30 IST)"), so say whether the
strategy expects that or exits earlier on its own.

### `## Entry`

The rule as math, then the same rule in one plain sentence. Use LaTeX in
`$...$` for inline and `$$...$$` for display; GitHub and the console
(react-markdown + remark-math + rehype-katex) both render it. Define every
symbol the first time it appears:

```markdown
$$
\text{enter long} \iff C_t > H_{t-1} \land V_t > k \cdot \bar V_{20}
$$

where $C_t$ is the close of the bar that just completed, $H_{t-1}$ the
previous bar's high, $V_t$ its volume, $\bar V_{20}$ the 20-bar mean volume
and $k$ the parameter `volume_multiple`.

In words: buy when a bar closes above the previous bar's high on volume at
least $k$ times the recent average.
```

Say which contract is bought or sold (ATM CE, one strike OTM PE, ...) and how
the strike is chosen — this is what `get_contract_requirements` declares.

### `## Position management`

What the strategy does after entry: adjustments, rolls, additions,
re-entries, as math plus prose. Then two things the reader must not have to
guess:

- **What the run's risk rules add on top.** A run carries
  `parametersJson.risk` with three levels — `leg` (points / percent per
  contract), `group` (rupees per signal group) and `overall` (rupees on the
  run, measured per trading day by default, `scope: "run"` for the whole
  replay). The API guard sweeps them every 3 s and closes through reduce-only
  `CLOSE_GROUP` signals; the backtest engine mirrors the same rules. Say
  which of these the strategy relies on.
- **What the strategy itself never does.** For instance "it has no built-in
  exit: without a leg target or the 15:30 square-off a position stays open".

### `## Exit`

Every way a position closes, in order of precedence (the first rule that
fires wins). Include the ones the platform supplies: leg / group / overall
risk rules, the run's stop button, 15:30 market close.

### `## Parameters`

A table: name (as in `default_params`), default, meaning, effect of raising
and of lowering it.

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|

### `## Worked example`

One real replay, walked through signal by signal. **Every number must be
traceable to a database row.** Pick one run — prefer a live-paper run of the
last two days — and cite its id; then walk through two to four signals with:

- the actual timestamp in IST (`TimestampUtc` + 5:30),
- the actual bar values / trigger levels from `simulation_signals.MetadataJson`,
- the contract chosen (`paper_orders.Symbol`),
- the fill (`paper_orders.FillPrice`, `Quantity` — which is **lots**),
- how the P&L in `paper_positions` follows (`AveragePrice`, `RealizedPnl`).

Show the arithmetic: entry, exit, points, × lots × lot size = ₹. Lot sizes
come from `Instruments.LotSize` (NIFTY 65, BANKNIFTY 30, FINNIFTY 60,
MIDCPNIFTY 120 in the September 2026 master); a `RealizedPnl` that does not
equal points × lots × lot size means one of the inputs is wrong — find out
which before publishing.

Cite ids inline, e.g. "run 96, signal 1409 → order 2042 → position 1031".
The rows come from:

```sql
-- the run
SELECT "Id", "Mode", "Symbol", "Resolution", "StrategyName", "ParametersJson", "StartedUtc"
FROM simulation_runs WHERE "Id" = <run id>;

-- its signals, oldest first (MetadataJson carries the trigger values)
SELECT "Id", "SignalType", "TimestampUtc", "GroupId", "MetadataJson"
FROM simulation_signals WHERE "SimulationRunId" = <run id> ORDER BY "Id";

-- the fills each signal produced
SELECT "Id", "SimulationSignalId", "Symbol", "Side", "Quantity", "FillPrice", "FilledUtc"
FROM paper_orders WHERE "SimulationRunId" = <run id> ORDER BY "Id";

-- the positions and their realised P&L
SELECT "Id", "Symbol", "Direction", "AveragePrice", "RealizedPnl", "OpenedUtc", "ClosedUtc"
FROM paper_positions WHERE "SimulationRunId" = <run id> ORDER BY "Id";
```

Always filter by `SimulationRunId`; never scan `live_ticks`.

### `## Limitations`

Honest: known weaknesses, what the strategy cannot see, data the platform
lacks (e.g. OI cannot be backfilled — it exists only from when the chain
poller was running), and where the backtest differs from live (fills at bar
close versus the next tick, no slippage model, contracts that have expired
have no broker history).

### `## Facts (machine-readable)`

The last thing in the file is a fenced `yaml` block of simple `key: value`
lines. The API parses it (one key per line, nothing nested) and the Library
page shows it as chips, so keep values short:

```yaml
name: <StrategyName>          # must equal the registry name — the test checks it
category: Directional         # the class's `category`
evaluates_on: bar             # tick | bar
resolution: 5m                # the bar it evaluates on
data: index candles           # comma-separated list from "Data it needs"
instruments: NIFTY, BANKNIFTY # the underlyings it is written for
default_lots: 1
built_in_exit: false          # true only when the strategy closes positions on its own
added: 2026-09-09             # the date the spec was first written
spec_version: 1               # bump when the rule changes, not for wording
```

## Definition of done

1. `docs/strategies/<StrategyName>.md` exists with the headings above, in
   order, and a facts block whose `name` equals the registry name.
2. The name is **not** in `PENDING_SPECS` in
   `src/AlgoTrading.PythonEngine/tests/test_strategy_specs.py`. That set is
   the list of strategies that predate this rule and still owe a spec; the
   test fails if a name listed there has a spec, so the debt list cannot go
   stale. Remove the name when the spec is written.
3. The suite passes:

   ```sh
   .venv/bin/python -m unittest src/AlgoTrading.PythonEngine/tests/test_strategy_specs.py -v
   ```

4. The page renders: open Strategies → Library, select the strategy, read
   the spec. Maths that looks right on GitHub but not in the console (or the
   other way round) is a bug in the spec, not in the renderer — stick to
   what both support.
