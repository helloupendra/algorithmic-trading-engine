# ChainFlowBuyV2

Source: `strategies/directional/chain_flow_buy_v2.py` (`ChainFlowBuyV2Strategy`), a subclass of
`ChainFlowBuyStrategy`. The entry rule (the trend candle and the four chain confirmations) is the
original's and runs unchanged; it is documented in [ChainFlowBuy](ChainFlowBuy.md). This page documents
what V2 changes. Every rule below is read from the code.

## Idea

The original's bet, kept: buy an index option only when a 5-minute trend candle is backed by the option
chain (writers' OI flow, an ATM volume surge, a premium being bid up, a calm IV). V2 exists to test that
bet without the faults the original's spec lists:
- no exit and no look at its own positions;
- a volume check on one contract and a purchase of another;
- an OI flow that can span an hour after a chain outage;
- a two-sided age check that lets a replay read a chain from after its decision.

Replayed offline on recorded option premiums on 5 Nov 2026, it loses money, as the original does (see
Limitations). It can be run beside the original, one change switched on or off at a time.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| index candles | the run's spot symbol (e.g. `NSE:NIFTY50-INDEX`) | 5m | as the original: `ema_slow` + 2 bars in the runner's list | ingestor (`live_bars`); `candles` in a backtest |
| option candles | the ATM CE and ATM PE (`atm_ce`, `atm_pe`) | 5m | as the original: `volume_lookback` + 1 | ingestor (`live_bars`); stored option candles in a backtest |
| option chain OI | the underlying's chain via `GET /api/OptionChain/view` | per-minute capture | as the original: a sample at least `flow_lookback_minutes` earlier in the session, and (V2) no older than `flow_max_lookback_minutes` | chain poller; `asOfUtc` in a backtest or recap |

V2 reads the chain the original just fetched for the same decision. The call is answered from the
original's cache (its live throttle, or the replay's moment), so V2 makes no extra request.

## Timeframe

As the original: one decision per closed 5-minute candle, live on the first tick after it closes.
Entries are allowed between `entry_start` and `entry_end` (`expiry_day_entry_end` on expiry day), at most
`max_trades_per_session` a day. V2's exits are judged on the same closed candles, from the candle after
the entry's own. The platform's close square-off (15:30; 15:45 for options from 7 Dec 2026) and a
backtest's `eod_square_off_ist` still close whatever is open.

## Entry

The original decides; V2 then takes or drops that entry. With $t$ the decision's moment and
$\tau_{\text{chain}}$ the chain's capture time, an entry is taken only if every check passes, in this
order:

1. **Replay clock:** in a backtest or recap, $\tau_{\text{chain}} \le t$. The original allows
   $|t - \tau_{\text{chain}}| \le 180$ s on both sides, so up to three minutes of the future passed it.
   Always on.
2. **Flow window:** the earlier OI sample the flow was measured from is at most
   `flow_max_lookback_minutes` old. The original keeps samples by count, so after a chain outage it
   measured "15 minutes of flow" against a sample from before the outage. 0 = no limit.
3. **Same contract:** the bought leg's strike and the chain's expiry equal the ATM contract's whose
   5-minute volume the surge check read (`atm_ce` / `atm_pe`). `require_same_contract`. With V2's
   default `delta_search_strikes` = 0, the original searches the chain's ATM strike only, so this almost
   always holds; it fails when the chain's ATM and the runner's ATM differ by a strike, or when the chain
   does not list the ATM strike (the original's `near_money` counts listed rows, not strike distance).
4. **Position rules** (`one_position`, below).

A dropped entry is undone in the original's bookkeeping: the day's trade count goes back down by one,
no side is disarmed, and the previous group id is restored. The drop is counted in `state["dropped"]`
(`chain_ahead`, `flow_too_old`, `contract_mismatch`, `same_side_held`, `opposite_ignored`) and printed
(`… entry dropped: <why>`).

A taken entry is the original's `OPEN_GROUP`, unchanged except for `metadata.v2`:
`flow_lookback_s` (the age of the earlier OI sample) and `atm_contract` (the contract whose volume was
read).

In words: take the original's entry only if its chain is not from the future, its flow is measured over
the window it names, and the option bought is the one whose volume confirmed it.

## Position management

With `one_position` (default true) V2 holds one position at a time:
- an entry on the side already held is dropped (`same_side_held`);
- an entry on the other side follows `on_opposite`: `"reverse"` sends a `CLOSE_GROUP` for the held group
  and then the new `OPEN_GROUP`; `"close"` only closes, and the entry is undone; `"ignore"` keeps the
  position and drops the entry;
- it never adds, rolls or adjusts.

**Knowing the position is gone.** A replay passes `metadata.open_groups`, so a position closed by a run
rule or the square-off is forgotten at once. Live says nothing: the position is dropped when a new
session starts.

**What the run's rules add.** Everything the original relies on still applies:
- `parametersJson.risk` (`leg`, `group` and `overall` stop-loss / target / trailing);
- in a backtest, the run rules of `backtest/rules.py`: `exits` (`time_exit_minutes`, breakeven,
  trailing, indicator exits), `limits` and `costs`.

V2 does not reimplement any of them.

With `one_position` false, V2 stacks as the original does and has no exits of its own.

## Exit

In a backtest, on each candle: the end-of-day square-off once its time has come; the run's leg, exit and
group rules and the overall rule; then V2 on the strategy's call; then the run's rules again on the new
marks. V2's own, on a candle that closed after the entry's:

1. **Trend break** (`exit_on_trend_break`): for a CE, the close below the slow EMA ($C_t < E^{21}_t$),
   or below the fast EMA with `"fast"`; the mirror for a PE. Reason: `Close CE: trend broke: …`.
2. **Flow flip** (`exit_on_flow_flip`): for a CE, the OI flow $F \le -$`flow_min_pct`; for a PE,
   $F \ge +$`flow_min_pct`. Writers now add on the other side. Only a flow measured within
   `flow_max_lookback_minutes`, on a chain that passed the data check and is not ahead of the decision,
   counts. Reason: `Close CE: OI flow turned against the CE: …`.
3. **Opposite entry** (`on_opposite` = `"reverse"` or `"close"`).

Live, the API's risk guard (every 3 s), a manual square-off and the close square-off close positions on
their own clocks.

## Parameters

The original's parameters keep their meaning. Their defaults are kept too, except
`delta_search_strikes`; see the original's spec for the rest. V2 changes or adds:

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `delta_search_strikes` | 0 (original: 3) | the original's: listed strikes either side of ATM searched for `target_delta`; 0 = the ATM strike only | wider search; the contract bought can differ from the one whose volume was checked | — |
| `require_same_contract` | `true` | drop an entry whose strike or expiry is not the ATM contract's | n/a | `false`: the original's mismatch allowed |
| `flow_max_lookback_minutes` | 30 | oldest earlier OI sample a flow may use; 0 = no limit | flows across outages accepted | more entries blocked after gaps |
| `one_position` | `true` | hold one position at a time; `false` = the original's stacking and no V2 exits | n/a | n/a |
| `on_opposite` | `"reverse"` | `"reverse"`, `"close"` or `"ignore"` | n/a — a choice | |
| `exit_on_trend_break` | `"slow"` | the EMA a close must cross back over to exit: `"slow"`, `"fast"`, `"off"` | n/a — `"fast"` exits sooner | |
| `exit_on_flow_flip` | `true` | exit when the OI flow reads `flow_min_pct` against the position | n/a | `false`: no flow exit |

## Worked example

None yet: no live run of ChainFlowBuyV2 exists, and the offline replay of 5 Nov 2026 has not been
walked through. The first run should be walked through here, signal by signal, with the template's SQL (docs/strategies/README.md);
`MetadataJson.v2` carries the flow window and the contract whose volume was read.

What the tests pin (`tests/test_chain_flow_buy_v2.py`):

- **The original's own fixture.** NIFTY rising 6 points a candle from 24,000; signal candle 12:25–12:30;
  close 24,234. Writers added 6,000 puts and 1,000 calls per strike over 20 minutes.
  - The original enters, and so does V2. V2 buys the 24,250 CE: the ATM contract whose volume surged.
  - With the earlier sample an hour old, the original still enters and V2 drops the entry
    (`flow_too_old`).
- **Exits.** On the next candle:
  - a close 150 points lower (below the slow EMA) closes the call;
  - a chain where writers add 10,000 calls per strike closes it on the flow flip.
- **A replay through the engine.** BANKNIFTY rises 1.5 a candle, then falls 4 a candle from the 41st.
  The ATM call's volume surges on the 11:45 candle. V2 enters there, once, and closes when the trend
  breaks.

## Limitations

- **It loses money.** Replayed on 5 Nov 2026 with recorded option premiums, 1 Jan 2025 to 11 Sep 2026,
  NIFTY, BANKNIFTY and SENSEX together, 2 lots, 0.5% slippage plus charges and a 20-point target: the
  original lost ₹10,82,855 and V2 ₹9,61,225; per trade, ₹449 against the original's ₹498.
- **Live does not know a run rule closed its position** until the next session (see Position
  management). After a leg target, entries on that side are dropped, and an opposite entry closes a
  group that is already closed.
- **The original's entry is kept as it is.** The issues in the original's spec that V2 does not address
  still hold:
  - re-arming reads only the EMAs;
  - the IV low is the run's own;
  - expiry-day contracts;
  - the unproven thresholds;
  - the historical chain rebuilt from `option_history_bars` before September 2026.
- **The trend-break exit is a close on a 5-minute candle**, so the fill can be well past the EMA.
- **`delta_search_strikes` = 0 changes which contract the original would buy** when the strike nearest
  0.5 delta is not the ATM one. That is the point, but it also changes results against the original;
  the replay above includes it.

## Facts (machine-readable)

```yaml
name: ChainFlowBuyV2
category: Directional
evaluates_on: bar
resolution: 5m
data: index candles, option candles, option chain OI
instruments: NIFTY, BANKNIFTY, SENSEX, FINNIFTY, MIDCPNIFTY
default_lots: 1
built_in_exit: true
added: 2026-11-04
spec_version: 1
```
