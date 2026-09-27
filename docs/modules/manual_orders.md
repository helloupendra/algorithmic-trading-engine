# Manual Orders — the hand-placed book, intraday vs carry forward, expiry and greeks

## Overview
Everything else the platform books comes from a strategy. The manual order ticket is the other half: an order the operator decides on — an MCX future, an NSE share, an index or commodity option — priced and sized by the rules of its own segment (tick size and lot size from the instrument master), filled from the live book (a buy pays the ask, a sell hits the bid), with an optional stop-loss and target on that position alone.

Hand-placed orders live in their own run, one per user: a `SimulationRun` with `StrategyName = "Manual"` (`ManualOrdersController.BookStrategyName`), `Mode = LivePaper`, `Status = Running`, and no runner process behind it by design. Because it is an ordinary run, every tool that understands a run works on it unchanged: the position rows, the live P&L, charges, and the per-position square-off. The console shows it as a run card under the ticket.

This page covers what is special about the book: each position is **intraday or carried forward** (a tick on the ticket and on the position), carried positions are **settled at expiry**, and every open leg shows its **greeks** and what they are worth in rupees.

---

## 1. Intraday or carry forward
On 27 Sep the owner asked, first: "if I want to carry forward a position, it should carry", and then: "Put a carry-forward system in strategies and in manual orders: if I want to carry forward, there should be a tick there and ticking it is enough." So each position carries one flag, `CarryForward`, like a broker's two products:

| Tick | Product | At its exchange's close |
|---|---|---|
| **off** (the default) | intraday (MIS) | **squared off** at its last price — NSE and BSE at 15:30 IST, MCX at the MCX close (23:30 IST while New York is on summer time, 23:55 otherwise) |
| **on** | carry forward (NRML) | **held overnight**, until it is closed by hand, by its own stop-loss/target, or settled at expiry |

The ticket has the tick, **"Carry forward (hold overnight)"**, off by default. Each ticket order opens a position of its own (its own group), so two orders never merge into one position and their ticks never combine; should an order ever add to a held position, the position stays carried if either asked for it. The tick can be changed later on any open position in the book (the **Carry** column of the book's run card, `PUT …/positions/{id}/carry-forward`); each change is a `CARRY_FORWARD` row on the book's activity (`Carry forward unticked by trader: NIFTY 24500 CE · 29 Sep — intraday — squared off at the close (15:30 IST)`) and a row in the activity log.

**The square-off.** `ManualIntradaySquareOff`, called every minute by `MarketHoursService`, closes each open, unticked position whose exchange has closed since it **became intraday** — since it was opened, or, if its tick was cleared later, since then. Through the usual reduce-only close (`CLOSE_GROUP`, at the latest quote, else the last mark), attributed to `market-hours`, with the reason:

> Intraday — squared off at the close (15:30 IST)
> Intraday — squared off at the MCX close (23:30 IST)

The rule keeps no memory, so it is restart-safe: an API that was down at 15:30 squares the day's intraday positions off in its first minute back, and a second pass finds nothing left to do. Consequences worth knowing:
- a position carried from yesterday and **unticked this morning** is intraday for **today's** close — not squared off the minute the tick is cleared;
- a position opened **after** its market closed (a limit order in the evening) waits for the **next** close;
- the tick is re-read under the book's lock at the moment of the close, so a tick that lands while the sweep is on its way is honoured;
- ticked positions are never touched.

**The deploy.** Until the tick existed every manual position was carried (from earlier on 27 Sep). The migration that adds the column (`PositionCarryForward`) ticks every position open in a manual book at that moment, so the first minute of the new API squares off nothing anybody chose to keep.

What touches the book, and what does not:

| Mechanism | When | What it does to the manual book |
|---|---|---|
| `MarketHoursService` → `ManualIntradaySquareOff` | every minute | Squares off **unticked** positions at their exchange's close (above). Never a ticked one. |
| `MarketHoursService` → strategy stops | at each market's close | Stops registered runners; a strategy leg ticked to carry arrives in its owner's book (section 1a). |
| `scripts/market-close.sh` | 23:35 IST nightly | **Leaves it alone.** It stops every other Running run with `flatten:true`, and logs `manual book(s) left open — hand-placed positions carry overnight: <id>`. It cannot tell carried from intraday, so the intraday square-off lives in the API instead. Until 27 Sep it stopped the book like any run, squaring off every hand-placed position each night. |
| `LiveRunStartupReconciler` | API start | Skips the book explicitly (an API restart used to liquidate it). |
| `StrategyRiskGuardService` | every few seconds | Closes a position only when its own stop-loss or target is crossed. |
| `CarriedPositionsService` → `ExpirySettler` | after an expiry day's close | Settles positions whose contract has expired (section 2). |

### 1a. Legs a strategy carried in
A strategy leg ticked to carry forward is moved into its run owner's book when the market close stops the run (the book is opened for an owner who has none) — see [Strategies › Carrying a leg forward](strategies_module.md#8-carrying-a-leg-forward). In the book it is an ordinary position: the same contract, side, lots and **entry price** as in the run, opened at the time the run opened it, ticked to carry, in a group of its own (`CARRY-<runId>-<group>`, so the legs of one straddle stay together). Its row says where it came from (`from run #412 · Ghost`), and a `CARRY_IN` activity row reads:

> Carried forward from run #412 (Ghost) at the close (15:30 IST): NIFTY 24500 CE · 29 Sep — SELL 2 lots at 100.00

From then on it is the book's: its P&L from the entry, its exit fill's charges, the settlement at expiry, the greeks, the next morning's quote. The run's own stop-loss, target and leg rules do not travel with it; set a level by hand or square it off.

**The next morning.** A carried leg is marked against its contract's live quote, exactly like a leg opened today. Until the feed delivers today's first tick, the quote is the last session's, and the row says so: the LTP cell shows `as of 15:29 · 18h ago` under the price whenever it is more than a minute old. With no quote row at all, the leg shows its last stored mark with the time that mark was written (the API used to send no time at all, so yesterday's price read as current).

A leg can only be marked if its contract is on the recording list the feeds subscribe to. The ticket puts a contract on the list when it is first looked at, and the list keeps it until it expires. As insurance against a row removed by hand, `CarriedPositionsService` checks every five minutes that each carried, unexpired contract is on the list and puts back any that is missing (logged as a warning).

---

## 2. Expiry settlement
Once positions carry, one of them eventually meets its expiry. Before this, nothing closed it: the contract stopped quoting and the row sat "open" for ever at its last trade. Now, after the expiry day's close (plus five minutes of grace), each open manual-book position in an expired contract is closed at what the exchange would settle it at:

- **Option:** intrinsic value against the underlying's closing price — `CE: max(0, S − K)`, `PE: max(0, K − S)`. An out-of-the-money leg settles at 0: a buyer loses the premium, a writer keeps it.
- **Index options** (NIFTY, BANKNIFTY, SENSEX, …) are cash-settled on the index close in reality too; S is read from the index (`NSE:NIFTY50-INDEX`, `BSE:SENSEX-INDEX`, …).
- **MCX options** are written on a future and in reality devolve into a futures position. Here they are cash-settled at intrinsic against **that future's close** (the nearest future of the same commodity still alive on the option's expiry: CRUDEOIL's September options, expiring on the 17th, against the September future, which runs to the 21st). This is a paper simplification, chosen so the book never holds a future the operator did not buy.
- **Stock options** are physically settled in reality; here they are cash-settled at intrinsic against the share's close, for the same reason.
- **Future:** closed at its own last price.

**When.** At the expiry day's session close on the contract's exchange plus 5 minutes: 15:35 IST for NSE and BSE; for MCX, 5 minutes after its evening close (23:30 IST while New York is on summer time, 23:55 otherwise, from the market-session rules). `CarriedPositionsService` runs a pass two minutes after the API starts and every five minutes after that, so an NSE/BSE expiry settles between 15:35 and 15:40.

**Catch-up.** The start-up pass is the catch-up: anything that expired while the API was down is due the moment it next runs. The close is stamped at the expiry's close time, not at the moment the API came back.

**S, the closing price.** The underlying's last price of the expiry day, observed within the last 15 minutes of its session, from the first of:
1. the last one-minute bar the platform recorded (`live_bars`), cut at the close (feeds have been seen writing flat candles after it);
2. a stored intraday candle (`candles`, not daily), cut at the close;
3. the live quote, if it last moved that day;
4. the day's stored daily candle (how a catch-up finds a close the feed never recorded).

An index's official close is computed from its constituents and can differ from its last print by a few points; the activity line names the price and where it came from so the difference is visible. With no price at all, nothing is invented: the position stays open, one warning is logged, and every later pass tries again.

**What the row shows.** Quantity 0, status Closed, the settlement price as the exit, realized P&L at that price, and one `CLOSE_GROUP` activity line such as:

> Expired — settled at intrinsic 150.35, S=24,650.35 (NIFTY close 29 Sep, last recorded bar), K=24,500 CE

or `Expired — settled at intrinsic 0.00 (out of the money), …`, or `Expired — settled at the future's last price 6,590.00 (…)`.

**Charges.** An expiry is not a trade: the exchange settles it, no order is placed and no brokerage is paid. So settlement books **no order row** (`IPaperTradingService.SettleExpiredPositionAsync` closes the position directly under the run's lock and records the signal). `RunCharges` charges fills, so a settlement adds nothing to the book's charges. Not modelled: the STT India levies on an exercised in-the-money long option (0.125% of the intrinsic value).

**Idempotent.** A position already closed — by hand, by the guard, or by an earlier pass — is skipped by the write itself, so a second pass changes nothing.

**Scope.** Manual books only, which includes strategy legs carried into a book at the close. Strategy runs themselves square off (or carry into the book) at their market's close and never hold an expired contract overnight.

---

## 3. Greeks on open positions
Also 27 Sep: "if I have bought or sold an option, the Greeks' effect should show too — like theta shows when you buy." Every open option leg — in the manual book and in strategy runs, since both are built by `PositionViewBuilder` — carries its greeks and their rupee effect. Backtests do not: a finished replay has no "now" to price at.

### What they mean
Per unit, in premium points:
- **IV** — implied volatility, in percent: the volatility at which the model prices the option at its last trade.
- **Delta (Δ)** — how much the premium moves for a 1-point move in the underlying. A call is between 0 and 1, a put between −1 and 0.
- **Gamma** — how much delta moves for a 1-point move.
- **Theta (Θ)** — how much the premium changes in **one calendar day** if nothing else moves. Negative: an option loses time value as expiry approaches.
- **Vega** — how much the premium changes for a **one-point (1%) rise in IV**.

For the position, each is multiplied by the quantity (lots × lot size) and by the side (+1 bought, −1 sold):
- **Theta ₹/day** = theta × quantity × side. A bought option **pays** its theta (negative, shown in red); a sold option **collects** it (positive, green).
- **Delta** = delta × quantity × side: the position's size in units of the underlying, which is also the rupees it makes on a 1-point rise.
- **Vega ₹** = vega × quantity × side: rupees per 1% rise in IV.

Worked example: bought 1 lot of NIFTY 24500 CE (75 units), theta −12.50, delta 0.52, vega 8.20 → **Theta −₹937.50/day**, delta +39 (₹39 per NIFTY point), vega +₹615 per 1% IV. The same contract sold: +₹937.50/day, delta −39, vega −₹615.

The run's totals add theta and vega across all open legs (they are rupees). Delta is added only within one underlying — a NIFTY point and a crude rupee are different moves — so the totals give delta per underlying, and a book-wide net delta only when the book holds one underlying. Futures and shares count toward delta as delta 1 per unit, so a hedged book reads correctly.

### Where they come from
Each leg names its source, in this order:
1. **`feed`** — the greeks the feed's enricher (`core/live/greeks_enricher.py`) wrote onto the contract's live quote, if the quote is fresh.
2. **`chain`** — the option-chain recorder's newest snapshot of the strike (`option_chain_snapshots`: Dhan's own greeks, or the FYERS poller's), if fresh.
3. **`computed`** — Black-Scholes, with the IV solved (bisection) from the option's last price and the greeks from that IV: the underlying's live price (the index spot; for an MCX option, the future it is written on, priced as Black-76), the strike, and the time to the contract's expiry at its exchange's close in IST.
4. The feed's figures even though stale, if nothing else can price the leg.

Conventions match across all sources so a leg reads the same whichever answered: risk-free rate **5%** (the default of `core/greeks_calculator.py`, which the enricher and the poller use), **365-day calendar** day count (theta = annual / 365), vega **per 1% of IV**. The Black-Scholes code is `OptionMath.Greeks` / `OptionMath.ImpliedVolatility`, pinned against the textbook case (S = K = 100, T = 1, r = 5%, σ = 20%: call delta 0.6368, gamma 0.01876, vega 0.3752, theta −0.01757/day).

**Freshness.** Figures older than two minutes are marked stale and shown with their time (`IV 13.6% · as of 15:29`), dimmed; they are never passed off as now. A computed figure is as old as the older of the two prices it used, and its time to expiry is counted from that moment.

**A known gap in the feed's figures.** The enricher dates a monthly symbol (`NIFTY26SEP…`) to the month's last Thursday, the NSE rule before 2025; NSE's monthlies now expire on the last Tuesday. For those contracts the feed's figures are skipped and the leg is priced from the chain or computed against the instrument master's expiry.

An open option that nothing can price (no quote for it or its underlying) shows "—" and is counted as "not priced" in the totals rather than shown as zeros.

---

## 4. In the console
- **Trade → Ticket** (`/trade/ticket`, the same page for every role): the ticket, with the **Carry forward (hold overnight)** tick above Buy/Sell and a line under it that says what happens to the order (`Intraday: squared off at the close (15:30 IST)…`, or the MCX close for a commodity), and under the ticket the **Manual book** run card.
- **Trade → Runs** (`/trade/runs`) and a run's own page (`/trade/runs/{runId}`, which the API refuses for someone else's run): one run card per run.
- **Trade → Positions** (`/trade/positions`): every open leg of every book the viewer may see, the manual books first, each with its **Carry** tick and **Square off**. The checkup's manual-book items link here.

On every run card:
- the positions table has a **Carry** column: a tick on each open row, and a line under the table saying what the ticks do there. The tick is disabled, with a tooltip saying why, for someone who may not change it (only the owner of the run or an admin may) and on a run that is no longer live. An open row with the tick shows a **Carry forward** badge beside **Open**; a strategy leg that moved to the book reads **Carried** with `→ book` in its exit cell; a book row that came from a run says `from run #N · Strategy` under its contract;
- the metric strip has **Greeks · open legs**: `Theta −₹450/day` (red when the book pays for time, green when it collects), and under it vega ₹ per 1% IV, delta per underlying, how many legs could not be priced, and "as of" when stale;
- the positions table has a **Δ · IV / Θ ₹/day / Vega ₹** column group after P&L (source and IV under delta, per-unit theta under the rupees; hover for every figure and its age);
- the LTP cell says `as of HH:MM · age` when the price is more than a minute old.

The table scrolls sideways inside its frame on a phone, like every table in the console.

## 5. API
`POST /api/ManualOrders` takes `carryForward` (bool, default false — intraday) with the order; the response echoes it.

`PUT /api/Strategy/runs/{runId}/positions/{positionId}/carry-forward` with `{ "carryForward": true | false }` ticks or unticks one position, in the book or in a strategy run. Admin, or the owner of the run (403 otherwise). Only an **open** position of a **running, non-recap** run: anything else answers 409 and changes nothing. Asking for the value it already has answers 200 with `changed: false` and writes nothing.

`GET /api/Strategy/runs/{runId}/live` (the manual book's run id comes from `GET /api/ManualOrders/book`):
- `isManualBook`, `canCarryForward` (the run is live and not a recap; who may is `canControl`);
- `positions[].carryForward`; `positions[].status` is `Open`, `Closed` or `Carried`; `positions[].carriedFromRunId` / `carriedFromStrategy` on a book row a strategy carried in;
- `positions[].greeks` — `source`, `asOfUtc`, `stale`, `ivPercent`, `delta`, `gamma`, `theta`, `vega`, `underlyingPrice` (computed only), `deltaQuantity`, `deltaRupeesPerPoint`, `thetaRupeesPerDay`, `vegaRupeesPerIvPoint`; null on closed legs and unpriced options;
- `positions[].ltpUpdatedUtc` — the age of the LTP (the quote's, or the stored mark's when there is no quote);
- `greeks` — `thetaRupeesPerDay`, `vegaRupeesPerIvPoint`, `netDeltaQuantity` (one underlying only), `byUnderlying[]`, `legs`, `unpriced`, `stale`, `oldestAsOfUtc`.

## Components
- `src/AlgoTrading.Api/Controllers/ManualOrdersController.cs` — ticket, book, place (with the carry tick).
- `src/AlgoTrading.Api/Services/ManualBook.cs` — finding and opening a user's book, shared with the close's carry.
- `src/AlgoTrading.Api/Services/ManualIntradaySquareOff.cs` — which unticked positions are due at which close, and the square-off.
- `src/AlgoTrading.Api/Services/PositionCarryForward.cs` — changing the tick; moving a strategy's ticked legs into the book at the close.
- `src/AlgoTrading.Api/Services/CarriedPositionsService.cs` — the five-minute pass: settle, keep carried contracts on the feed.
- `src/AlgoTrading.Api/Services/ExpirySettler.cs` — which positions are due, S, the settlement price and its reason.
- `src/AlgoTrading.Infrastructure/Services/PaperTradingService.cs` — `SettleExpiredPositionAsync`: the close, without an order; `SetCarryForwardAsync`, `CloseIntradayPositionsAsync` (re-reads the tick under the lock) and `CarryPositionAsync` (the move).
- `src/AlgoTrading.Api/Services/PositionGreeks.cs` — source order, freshness, rupee effects, totals (`PositionGreeks`), and the loader (`PositionGreeksBuilder`).
- `src/AlgoTrading.Infrastructure/Services/OptionHistory/OptionMath.cs` — Black-Scholes(-Merton / Black-76) greeks and the IV solver.
- `scripts/market-close.sh` — the nightly close, which skips the book.
- `web/src/lib/greeks.ts`, `web/src/pages/strategies/RunCard.tsx` — the column group, the totals line, the LTP age, the Carry column.
- `web/src/lib/carry.ts`, `web/src/pages/trading/ManualOrderPage.tsx` — the tick's rules and words; the ticket's tick.

Tests: `tests/AlgoTrading.UnitTests/OptionGreeksTests.cs`, `PositionGreeksTests.cs`, `ExpirySettlementTests.cs`, `CarryForwardTests.cs`; `web/src/lib/greeks.test.ts`, `carry.test.ts`, `positions.test.ts`; `scripts/tests/market-close-manual-book.test.sh`.
