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
   - similar past moments: for each index, what followed the 50 most similar moments of earlier days, as base
     rates, not a forecast ([below](#similar-past-moments));
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
   target are checked against it. Its prompt carries its tested lessons from earlier days ([Lessons](#lessons)).
3. **Code judges it** (`AiTraderGuard`): every limit below. A refusal names the rule. It judges the book as it is
   once the answer is in, not as the brief showed it: the book is read again, and in the API one look at a time judges
   and acts (`AiTraderAgent.BookGate`, which the shadow book's minute check takes too). So "Run now" beside the
   scheduled look cannot pass a limit the other was about to use, or close a position the other closed. The model is
   asked outside it, so a slow answer never holds up the minute check.
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
| Kill switch | The desk's kill switch stops anything new (a buy, a strategy start) in shadow and live mode; closing its own position or stopping its own run is never refused for it. A market replay places nothing, so the live desk's switch does not reach its book |

## Shadow first

`Ai:AiTraderExecute` is off: it decides and places nothing (mode `shadow`). Placing paper orders through its own
manual book and strategy starts is the next step, switched on only after its shadow decisions have been read.

In live mode the rules judge its own account (`AiTraderBookReader`), and its net today is today's part only:

| Part | Counted |
|---|---|
| Positions closed today | Their realized P&L (one carried in from an earlier day and closed today: all of it) |
| Open positions bought today | Their P&L at the mark |
| Open positions carried from an earlier day | The mark less the previous session's close, as the feed sends it with today's quotes. With no quote of today, so no close: the P&L since entry if it is a loss, nothing if it is a gain, so the daily-loss rule is never loosened by a gain it cannot date |
| Charges | Those of today's fills |
| Its strategy runs | Only runs started today, whole (a run's figures cover its whole life, and live runs stop at the close); a run still running from an earlier day is listed with its net and left out of the day's |

Until 1 Oct a carried position's whole P&L since entry, and every running run's whole net, counted against today's
charges only, so a winner carried in hid the day's losses from the daily-loss rule.

### The shadow book

Placing nothing must not mean scoring nothing. In shadow mode, and in a replay, an allowed buy is kept by code as if
placed (`ai_trader_shadow_positions`, `AiTraderShadowBook`), and the model reads that book on its next look:

| Step | Rule |
|---|---|
| Entry | The ask its decision saw (in a replay, the replay's own quote when it has one) |
| Minute check | Every minute, whatever the decision schedule and whether the AI Trader is on or off (`AiTraderShadowWatcher`): the price it could sell at, the bid, else the last trade less half a spread, as the desk's paper fills |
| Stop | That price at or under the stop: closed at that price, which can be under the stop |
| Target | That price at or over the target: closed at that price |
| Its own exit | An allowed `exit` naming the position: closed at that price |
| Close | Squared off at the session's close (15:30 IST); one left from an earlier day closes at its last mark |
| Replay | Each replay starts a fresh shadow book; positions still open when it ends close at their last marks (`replay-ended`) |
| Charges | The desk's index option round trip (`OptionCharges`); net is after them, an open position's as if sold at its mark |

The minute check is a hosted service of its own, not the agent's tick: the scheduler runs only the agents that are on,
and a position left open when it was switched off went unmarked, missed its stop, target and the 15:30 square-off,
and closed on a later day at a stale mark. Like the scheduler it starts 90 seconds after the API and runs only where
`Ai:SchedulerEnabled` is on. A touch between two minute checks is missed. Prices come from the feed's quote when it is under three minutes
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
trading day's session or while MCX trades, and a day starts only if it will end before the next trading morning's
08:45 and MCX's next open. A day with nothing recorded is skipped with its reason; one that failed early is tried
again ([the queue](market_replay.md#the-queue)). The queue holds at most 20 days.

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
kept at ₹0 with why. Doing nothing scores ₹0 too. A day counts as over the next day, or on a trading day from 15:45
IST: a [reflection](#lessons) on a live day scores the rule for it then.

`GET /api/AiTrader/scoreboard` lists each replay and live shadow day (a row), newest first: its looks, positions, net
after charges, the baseline's net, and the difference. `take` (default 60) limits only the rows listed; `rowsTotal`
says how many there are. The totals are over every full row, listed or not (looks from 09:30 or earlier to 14:30 or
later), whose baseline is scored: the AI's net and the rule's, the rows the AI beat the rule on, and the rows each made
money on. The totals' `days` counts rows, not dates: a day replayed more than once counts each time, against the same
rule result each time. The page words them so ("AI beat the rule on 3 of 8 full replays and shadow days") and says
"60 of 75 rows listed" when it lists fewer rows than there are.

## Similar past moments

In its first replayed days it mostly bought calls in "uptrends", with no idea how often such a setup went on
rising. The brief now says what happened after moments like the present one, computed by code from the stored
history of each index (`ai_trader_situations`, `SituationMath`, `SimilarMoments`). It is a base rate, not a forecast.

### What is stored

A moment is an index (NIFTY, BANKNIFTY, SENSEX) on a day at a slot: every 10 minutes from 09:25 to 15:05 IST, 35 a
day. Its looks run at 09:20, 09:30, … (`Ai:AiTraderFromIst`), so a look is matched with slots on either side (see the
time window below). Everything is read from the regular session's 1-minute bars (09:15–15:30 IST), and a moment knows
only the bars that began before its minute: at 10:25 the last is 10:24's, which closed at 10:25.

| Feature | Definition |
|---|---|
| Move since the previous close | The last close against the previous session's last close, % |
| Move since the open | Against the day's first open, % |
| Last 30 minutes | Against the close of the last bar that began 30 minutes before the moment; since the open when the day is younger |
| Range so far | The day's high − low, % of the previous close |
| EMA side | The last close against EMA 20 and EMA 50 of 5-minute closes (over the last 1,000 session minutes): above both, below both, or between |
| EMA gap | EMA 20 − EMA 50, % of EMA 50 |
| India VIX, and its change | Its last minute and its move since its previous close; empty when not recorded (no VIX before Aug 2021) |
| Minutes since the open | From 09:15 |
| Trading days to expiry | To the index's nearest option expiry, 0 on the expiry day: the exchanges' calendar from Aug 2020 together with the instrument master's expiries, counted on the session service's trading days. Before 2026 only weekends are known to be closed, so a holiday counts as a day. SENSEX before May 2023 has none |
| Weekday | Stored, not matched on: the expiry cycle is in days to expiry, and the expiry weekday has changed over the years |

| Outcome | Definition |
|---|---|
| +30 min, +60 min | The return to the close of the last bar before the horizon, or before 15:25 when that comes sooner |
| To 15:25 | The return to 15:25 |
| Biggest move up, down | The highest high and lowest low from the moment to 15:25 against the price; 0 when it never went that way |

A moment is not stored when the index has no bar of the day yet, its last bar is over 10 minutes old (a feed gap), the
previous close is not within a week (the previous session's last bar must be from 15:00 on, or its bars stopped
early), or there are fewer than 50 five-minute bars. An outcome is empty when the bar at
its horizon is over 10 minutes old; the extremes are empty with the return to 15:25. The base rates read only rows with
every outcome. The same function computes the features for the table and for the brief, so the two cannot drift.

### Matching, and the leakage rule

At a look, the moment is described from the recorded 1-minute bars up to the brief's clock (live, or the replay's).
Then, for each index:

1. **Only days before the brief's day.** A replay of 16 Sep sees nothing of 16 Sep or later, and the features are
   scaled over those earlier rows alone.
2. **Same EMA side**, and a slot within 30 minutes of the same time of day.
3. **Standardised distance** over the numeric features known now (each divided by its standard deviation over the
   earlier rows). A feature not known now (VIX unrecorded) is left out, and the line says so; a row missing a feature
   known now is not a candidate.
4. **One moment per day**, its nearest, then the 50 nearest days. A day's moments share most of their future;
   counting a day several times would only look like more evidence.

Fewer than 50 such days: the line says there is too little history and gives no rate. The moment cannot be described
(no bar of the day yet, a feed gap): the line says why. A failure says "not available just now" and the rest of the
brief stands. The stored moments are kept in memory, reloaded after each night's build, so a look reads no table.

The shape of the section (illustrative numbers, not results):

```
SIMILAR PAST MOMENTS (base rates from the 50 most similar past moments of each index, one a day, only days before today; not a forecast)
NIFTY (50 days since Aug 2021, price above both EMAs as now): next hour up 52% of the time, median +0.03% (middle half −0.18% to +0.21%); to 15:25 up 49%, median −0.01% (middle half −0.45% to +0.40%); biggest move to the close: up median +0.38%, down median −0.41%
BANKNIFTY: too little history for a base rate: 31 similar past days before today, 50 needed.
SENSEX: cannot be compared now: its last minute recorded is 10:09, over 10 minutes old.
```

"Next hour" reads "next hour (cut at 15:25)" when the hour runs past 15:25.

### What the numbers are not

- **Not a forecast and not an edge.** Fifty similar days that rose 52% of the time is a coin with a story. A median
  move of a few hundredths of a per cent is inside the noise.
- **The index, not the option.** A call needs the index to move far enough and soon enough to beat time decay, the
  spread and the charges; a median +0.03% hour does none of that.
- **Similar is only as good as its features.** Nothing about news, the option chain, global markets or the regime
  is matched; days from 2021 and 2026 count alike.
- **Two stores.** The table is built from the stored candles (Dhan's history, then the nightly archive's, rolled up
  from the live bars); now is read from the live bars themselves. Where a vendor's candle and the live bar differ, so
  can a feature, a little.

### Building it

`AiTraderSituationBuilder` fills the table day by day, oldest first, from the 1-minute candles (NIFTY from Aug 2020,
BANKNIFTY, SENSEX and India VIX from Aug 2021): about 143,000 rows for the history there is. Each day reads at most
2,500 minutes per symbol, replaces that day's rows, and saves itself as the last day done
(`aitrader.situations.lastDay` in system_settings) in the same transaction, then waits half a second. So a stopped
run carries on from the next day, and a day is never half written.

It never runs from 08:45 to 15:45 IST on a trading day (the session service's open less 30 minutes to its close plus
15; a holiday is not a session). It builds only up to the nightly candle archive's last day, so each night it adds
the day the archive has just finished, within 10 minutes of it. It does nothing until an admin starts it:
`POST /api/AiTrader/situations/backfill` (all the history) or `?from=yyyy-MM-dd` (rebuild from that day). Progress
goes to the log every 100 days with bars and to `GET /api/AiTrader/situations`; a failure is a warning (at most hourly)
and the day is tried again within 10 minutes.

## Lessons

It learns from its own days, with the same model (owner, 2 Oct). The model's weights do not change: a lesson is a
short rule put into its prompt. A Judge writes it from one of its finished days, and it is tested on its past looks
before it is used. Every lesson can be read, traced to its day and taken out on AI → Memory (agent `ai-trader`).

### Made: a reflection on each finished day

`AiTraderReflection`, named `ai-trader-reflect` on its calls and reports.

| Step | Rule |
|---|---|
| When | A replay once it has ended and its shadow book is closed. A live day from 15:45 IST: its 15:30 square-off has settled, and the baseline rule can score it |
| Reads | Every look of the day (its number, time, what it proposed, the verdict, its reason); the shadow positions (entry, stop, target, exit, how each ended, net after charges); the baseline rule's trade that day |
| Asks | One Judge-tier call, no tools: at most 3 lessons, each a general rule resting on that day's decisions by number |
| Keeps | A lesson of at most 300 characters, with no date, no price or index level and no contract, resting on at least one of the day's decisions, and not one already kept in any status. Each is an `ai_memories` row: agent `ai-trader`, kind `lesson`, status `proposed`, source and via `check`, its source report the reflection |
| Report | One per replay or live day (`ai_reports`, subject `replay:12` or `day:2026-10-05`, the day as its session date), so a day is never reflected on twice. A day with no readable look is skipped. A failed or unreadable answer is tried again after 15 minutes, three tries in all; one turned away for capacity is not counted |

### Tested: on past looks, without it and with it

`AiTraderLessonCheck`, named `ai-trader-lesson-check` on its reports. The owner does not approve each lesson
(1 Oct: lessons are tested, not approved).

| Step | Rule |
|---|---|
| Looks | Up to 16 past decisions (`Ai:AiTraderLessonPoints`) with a brief and a readable answer, from days other than the lesson's and made before it was proposed. Only looks in the hours a buy may open (09:20–14:45): outside them every buy is refused either way. The most recent days first, spread across days, alternating looks that acted with looks that did nothing |
| Asked | Each look's stored brief twice, with the AI Trader's own prompt. CONTROL: the lessons it would read at that look (active, learned before that look's day). TREATMENT: those and the lesson on trial |
| Judged | Each answer read as a plan and judged by the rules against an empty book at the look's time. Refused, or anything but a buy, is ₹0 |
| Played | An allowed buy: the contract on the chain as recorded at that time (only a chain captured that day), in at the ask of the first recorded tick at or after it, each recorded minute's last tick checked at the bid against its stop and target, squared off at 15:30, after the shadow book's charges. An exit a later look would have made is not played, in either arm |
| Used | Only if TREATMENT's net beats CONTROL's by ₹500 or more (`Ai:AiTraderLessonMinGain`), it helps on at least as many looks as it hurts, and it has no more unreadable or unanswered answers. Otherwise it is dropped (`rejected`), with the reason |
| Cap | At most 8 active (`Ai:AiTraderMaxLessons`). A lesson that passes when 8 are active replaces the one with the weakest evidence (its test's gain; ₹0 for one never tested), or is dropped when it is no stronger |
| Kept | Every look in the report: the decision, each side's answer and net, the totals. The verdict is the lesson's decider, for example "check: +₹3,268 over 16 looks; helped 3, hurt 1" |

The owner can still approve, reject, edit or retire any lesson on AI → Memory. One he decides during its test is left
as he decided.

**Paced.** One step a scheduler minute: a reflection, or one look of a test (two asks). So a test never holds the
other agents' turn, and stays at 20 asks in ten minutes, under the desk's 30 for one asker. It runs:
- only outside 09:00–15:45 IST on an NSE trading day (the exchange calendar): the free tier is slow and the live desk
  comes first;
- never while a replay the AI Trader decides in is playing, so the replay's looks never wait behind it;
- only while the AI Trader is on and `Ai:AiTraderLessons` is true.

A call turned away (the rate limit, the provider's capacity, the switch) pauses the test, and the same look is asked
again. A call no model answered is asked once more, then counts as no answer. A failure never makes a lesson active.
A test under way is kept in memory: a restart begins it again from its first look.

**Cost.** A reflection is one Judge call. A test is two calls a look, so up to 32, plus one embedding call when the
lesson becomes active. A day costs at most 1 + 3 × 32 = 97 calls, about 50 minutes of evening at one look a minute.

### Read: only what was known before the day

`AiTraderMemory`. A replay decides on an earlier day's clock. A lesson from a later day knows how that day went:
read in a replay of an earlier one, it would score the AI on knowledge it could not have had. So a decision reads:
- active lessons learned from a day before its own (the reflection's session date). A replay of 16 Sep never reads a
  lesson from 24 Sep; a live look on 5 Oct reads those up to 4 Oct;
- the owner's notes and corrections for `ai-trader` written before its clock;
- corrections first, then notes, then lessons (the newest first), within `Ai:MemoryBudgetChars` and
  `Ai:MemoryMaxItems`.

The gateway's recall knows no day, so it never runs for the AI Trader. The agent sends its own prompt with the
memories in it and names them (`AiAskInput.GivenMemoryIds`), so its call keeps them (`ai_calls.MemoryIdsJson`) like
any recalled ones. A decision's detail lists them ("Read: M45, M46").

### On the console

AI → Agents, the AI Trader's panel, under the scoreboard: each lesson used, waiting or under test (with how far),
dropped or retired. Each shows the day it came from and its decisions, and once tested its evidence ("With it +₹3,268
against ₹0 without, over 16 looks: helped 3, hurt 1"), with links to the reflection and the test. "Run now" takes one
step at once.

**A first round.** With the AI Trader on, finished replays are reflected on one a minute, oldest first, outside the
session; their lessons are then tested one look a minute. Eight replay days make 8 reflections and up to 24 tests:
at most about 780 calls, some six and a half hours of evenings. To start at once,
`POST /api/Ai/agents/ai-trader-reflect/run` (blank: one step; `{ "subjectId": "12" }`: reflect on replay 12).

## Daily digest

Its day goes to Telegram in the AI's one daily digest (system channel), with the day's run reviews, after the NSE
close and 15:45 ([when and how often](ai.md#scheduled-agents-phase-3)). Its section:

- the mode, and "now off" if it was switched off during the day;
- looks, actions proposed, allowed, refused, and looks with no usable answer, counted as Today counts them;
- the shadow book: each position's contract and lots, in → out (IST), entry → exit premium, how it ended (stop,
  target, its exit, close) and its net; then the day's net and charges, with an open position as if sold at its mark;
- on a day it was live, its live book (its manual book) the same way, with no "how it ended" (the book does not keep
  it): each line's net is after its own round trip's charges, and the head line's is its net today (above) after the
  charges of the day's fills. A position carried in from an earlier day names that day. Its strategy runs are
  reviewed on their own;
- the three rules that refused it most.

Rupees are whole, with Indian grouping, except premiums. A replay's looks and shadow book are never in it. Off all day
with no looks, the section is left out; on with none, it says so. On a day with no session the digest goes only if it
looked.

An example, with made-up numbers from the tests:

```
AI Trader (shadow mode)
Looks 10 · proposed 8 · allowed 4 · refused 4 · no answer 1
Shadow book: 3 trades, net −₹122 after ₹187 charges
• NIFTY 22650 CE, 1 lot: 09:30 → 09:52, ₹120 → ₹88, stop, −₹2,139
• NIFTY 22600 PE, 2 lots: 10:00 → 11:00, ₹81 → ₹95.5, its exit, +₹1,816
• NIFTY 22700 CE, 1 lot: 12:00 → 15:30, ₹96 → ₹100, close, +₹201
Top refusals: stop (2), hours (1), size (1)
```

In live mode:

```
AI Trader (live mode)
Looks 3 · proposed 3 · allowed 3 · refused 0 · no answer 0
Live book: 2 trades, net −₹332 after ₹137 charges
• NIFTY 22650 CE, 1 lot: 09:30 → 09:52, ₹120 → ₹88, −₹2,142
• NIFTY 22600 PE, 2 lots: 10:00 → 11:00, ₹81 → ₹95.5, +₹1,809
```

## API (admin)

| Endpoint | What |
|---|---|
| `GET /api/AiTrader/status` | On or off, shadow or live, the limits, today's looks, the last three, today's shadow book |
| `GET /api/AiTrader/decisions?day=&replay=&take=&beforeId=` | Decisions, newest first by their clock (a day's list holds that day's replays too); `beforeId` goes on after that decision's clock and id |
| `GET /api/AiTrader/decisions/{id}` | One decision with its brief, plan and result, and the memories its call was given |
| `GET /api/AiTrader/positions?day=&replay=` | The shadow book: a day's or a replay's positions, net after charges |
| `GET /api/AiTrader/scoreboard?take=` | Each replay and live shadow day against the baseline rule, with totals over full days |
| `GET /api/AiTrader/situations` | The similar-moments history: the last day built, the backfill's state and this run's days and rows, each index's rows and days |
| `POST /api/AiTrader/situations/backfill?from=` | Starts the history over everything stored, or rebuilds it from `from` (yyyy-MM-dd); 202, taken on the builder's next pass |
| `GET /api/AiTrader/lessons` | Its lessons with their day, their decisions and their test's evidence; the one under test; the limits |
| `POST /api/Ai/agents/ai-trader/run` | One look now |
| `POST /api/Ai/agents/ai-trader-reflect/run` | One lesson step now (`{ subjectId }`: a replay's id, to reflect on it); never 09:00–15:45 IST on a trading day |

Today shows its day in an "AI Trader" card. Its last three decisions are worded as on the AI Trader page, with the
option the plan named ("Buy NIFTY CE").
