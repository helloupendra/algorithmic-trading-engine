# AI Trader

An AI agent that trades its own paper account in index options (owner's decisions, 1 Oct 2026). It is there to test
the system and collect data, not to promise profit: the desk's research found no proven edge, and any claim of
improvement needs the numbers behind it.

## The loop

Every 10 minutes from 09:20 to 15:00 IST on trading days, while it is switched on (AI → Agents, `ai-trader`):

1. **A market brief, built by code** (`MarketBriefBuilder`), as of that moment. The model reads facts, not raw data:
   - each index: price, change from the previous close, the day's open, high, low and range, EMA 20 and 50 and
     ATR(14) on 5-minute bars, the last half hour;
   - India VIX and its change;
   - each nearest-expiry chain: ATM±1 premiums, PCR, max pain, the call and put walls, OI change on the day and
     since the first capture of the session. Only a chain captured that day is offered: before the day's first
     capture (or with the recorder down) the line says when the last one was, and nothing on it can be bought;
   - today's forecasts as issued before the open, in words (the session's high−low range: median, 80% band, the
     chance of a quiet, normal or wild day; a size, not a direction), never their scores;
   - GIFT Nifty before the open, the last FII/DII cash flows, today's events;
   - the last hour's news as the News Analyst read it, policy and macro data first, then results, guidance and
     rating changes, then anything with a direction; routine filings with none are left out;
   - stop floors: for each CE and PE at ATM±1, the ask a buy would be priced at now (in a replay, the replay's own
     quote) and the lowest stop the 40% rule allows on it, rounded up to the 0.05 tick. Code never moves a stop;
   - its own book: net today after charges, the loss budget left, trades used, open positions with stops and
     targets, its running strategies (in shadow and replay, its shadow book: see below);
   - its last three looks of the day (or of the replay): what it proposed and what the rules said, so a refused plan
     is corrected rather than sent again. A plan allowed in shadow mode says it was not placed.

   A section whose source fails says so in one line; the rest of the brief stands.
2. **The model proposes** one action as JSON (Judge tier, temperature 0.2): none, buy, exit, start_strategy or
   stop_strategy, with a reason naming the facts it rests on. The entry is the option's ask: size, stop and
   target are checked against it.
3. **Code judges it** (`AiTraderGuard`): every limit below. A refusal names the rule.
4. **Every look is a row** in `ai_trader_decisions`, "none" included: the brief, the plan, the verdict, the call.

## The limits (code-enforced, never the model's)

| Rule | Value |
|---|---|
| Account | Its own (`ai-trader` user, created on first use; no password, nobody signs in as it) |
| Instruments | NIFTY, BANKNIFTY, SENSEX options; buy to open, sell only to close |
| Size | 1–2 lots, at most ₹50,000 of premium a trade |
| Positions | At most 3 open; premium in use at most ₹5 lakh |
| Stop and target | Required on every buy; the stop below the entry and no more than 40% below it, the target above it |
| Daily loss | At −₹10,000 net of charges, nothing new opens that day |
| Hours | New positions 09:20–14:45 IST on trading days |
| Trades | At most 10 a day |
| Exits | Only its own open positions: the one the plan names by `positionId`, else the only open one on the plan's underlying (and option, when given); two or none are refused with the open positions' ids |
| Strategies | Only GhostTangentCrossings and ChainFlowBuy, at most 3 running, in its own account |
| Kill switch | The desk's kill switch stops anything new (a buy, a strategy start); closing its own position or stopping its own run is never refused for it |

## Shadow first

`Ai:AiTraderExecute` is off: it decides and places nothing (mode `shadow`). Placing paper orders through its own
manual book and strategy starts is the next step, switched on only after its shadow decisions have been read.

### The shadow book

Placing nothing must not mean scoring nothing. In shadow mode, and in a replay, an allowed buy is kept by code as if
placed (`ai_trader_shadow_positions`, `AiTraderShadowBook`), and the model reads that book on its next look:

| Step | Rule |
|---|---|
| Entry | The ask its decision saw (in a replay, the replay's own quote when it has one) |
| Minute check | Every minute, whatever the decision schedule: the price it could sell at, the bid, else the last trade less half a spread, as the desk's paper fills |
| Stop | That price at or under the stop: closed at that price, which can be under the stop |
| Target | That price at or over the target: closed at that price |
| Its own exit | An allowed `exit` naming the position: closed at that price |
| Close | Squared off at the session's close (15:30 IST); one left from an earlier day closes at its last mark |
| Replay | Each replay starts a fresh shadow book; positions still open when it ends close at their last marks (`replay-ended`) |
| Charges | The desk's index option round trip (`OptionCharges`); net is after them, an open position's as if sold at its mark |

A touch between two minute checks is missed. Prices come from the feed's quote when it is under three minutes
old, else the option chain (the minute capture with fresh quotes over it) when that is under three minutes old
too, or after the close the session's last capture; with neither, the position keeps its last mark and is neither
stopped nor taken that minute. A shadow position is never put on the feed. Strategy starts and stops are recorded
only: runs are not simulated in shadow.

## In a market replay

Asked into a replay (Data → Replay, "AI Trader decides along"), it decides on the replay's clock (mode `replay`):
the brief is built as of the replayed moment, from the recorded bars, the recorded chain and the replay's prices.
It starts that replay's own fresh shadow book and places nothing. The model can take up to a minute to answer, so above
2× a replay moves faster than it can look every ten replayed minutes.

## Scored against a baseline

One day says nothing. To score it over many, a queue replays recorded days one after another with the AI Trader
alone (`POST /api/Replay/queue {dates, speed}`). Each day plays from the open with a fresh shadow book. Days play
oldest first, with a 90-second gap between them so the last one's book is squared off. Nothing starts during a
trading day's session, and a day starts only if it will end before the next trading morning's 08:45. A day with
nothing recorded is skipped with its reason. The queue holds at most 20 days.

Every day it decided on, replayed or live, is also scored under a fixed rule anyone could follow
(`ai_trader_baselines`, `AiTraderBaselineScorer`, rule `nifty-trend-1100`):

| Step | Rule |
|---|---|
| Side | At 11:00 IST, NIFTY's last 5-minute close above EMA 20 and EMA 50 with EMA 20 above: the at-the-money call; below both with EMA 20 below: the put; otherwise no trade |
| Data | Only bars that began before 11:00 (10:59's included) and the chain as recorded by 11:00 that day (none that day: no trade) |
| Trade | One lot at the ask of the first recorded tick at or after 11:00; stop 30% under, target 50% over |
| Exit | Each recorded minute's last tick at the bid (else the last trade less half a spread), as the shadow book checks; squared off at 15:30 |
| Charges | The same as the shadow book's |

The agent scores one missing day a minute, newest first, never in a trading day's session. A day that fails to score
is logged and tried again an hour later; the days before it are scored meanwhile. A day the rule does not trade is
kept at ₹0 with why. Doing nothing scores ₹0 too.

`GET /api/AiTrader/scoreboard` lists each replay and live shadow day (a row), newest first: its looks, positions, net
after charges, the baseline's net, and the difference. `take` (default 60) limits only the rows listed; `rowsTotal`
says how many there are. The totals are over every full row, listed or not (looks from 09:30 or earlier to 14:30 or
later), whose baseline is scored: the AI's net and the rule's, the rows the AI beat the rule on, and the rows each made
money on. The totals' `days` counts rows, not dates: a day replayed more than once counts each time, against the same
rule result each time.

## Daily digest

Its day goes to Telegram in the AI's one daily digest (system channel), with the day's run reviews, after the NSE
close and 15:45 ([when and how often](ai.md#scheduled-agents-phase-3)). Its section:

- the mode, and "now off" if it was switched off during the day;
- looks, actions proposed, allowed, refused, and looks with no usable answer, counted as Today counts them;
- the shadow book: each position's contract and lots, in → out (IST), entry → exit premium, how it ended (stop,
  target, its exit, close) and its net; then the day's net and charges, with an open position as if sold at its mark;
- the three rules that refused it most.

Rupees are whole, with Indian grouping, except premiums. A replay's looks and shadow book are never in it. Off all day
with no looks, the section is left out; on with none, it says so. On a day with no session the digest goes only if it
looked.

```
AI Trader (shadow mode)
Looks 10 · proposed 8 · allowed 4 · refused 4 · no answer 1
Shadow book: 3 trades, net −₹122 after ₹187 charges
• NIFTY 22650 CE, 1 lot: 09:30 → 09:52, ₹120 → ₹88, stop, −₹2,139
• NIFTY 22600 PE, 2 lots: 10:00 → 11:00, ₹81 → ₹95.5, its exit, +₹1,816
• NIFTY 22700 CE, 1 lot: 12:00 → 15:30, ₹96 → ₹100, close, +₹201
Top refusals: stop (2), hours (1), size (1)
```

## API (admin)

| Endpoint | What |
|---|---|
| `GET /api/AiTrader/status` | On or off, shadow or live, the limits, today's looks, the last three, today's shadow book |
| `GET /api/AiTrader/decisions?day=&replay=&take=&beforeId=` | Decisions, newest first by their clock (a day's list holds that day's replays too); `beforeId` goes on after that decision's clock and id |
| `GET /api/AiTrader/decisions/{id}` | One decision with its brief, plan and result |
| `GET /api/AiTrader/positions?day=&replay=` | The shadow book: a day's or a replay's positions, net after charges |
| `GET /api/AiTrader/scoreboard?take=` | Each replay and live shadow day against the baseline rule, with totals over full days |
| `POST /api/Ai/agents/ai-trader/run` | One look now |

Today shows its day in an "AI Trader" card. Its last three decisions are worded as on the AI Trader page, with the
option the plan named ("Buy NIFTY CE").
