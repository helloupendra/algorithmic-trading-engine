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
     since the first capture of the session;
   - today's forecasts as issued before the open, in words (median move, 80% band, the chance of a quiet, normal or
     wild day), never their scores;
   - GIFT Nifty before the open, the last FII/DII cash flows, today's events;
   - the last hour's news as the News Analyst read it, policy and macro data first, then results, guidance and
     rating changes, then anything with a direction; routine filings with none are left out;
   - its own book: net today after charges, the loss budget left, trades used, open positions with stops and
     targets, its running strategies.

   A section whose source fails says so in one line; the rest of the brief stands.
2. **The model proposes** one action as JSON (Judge tier, temperature 0.2): none, buy, exit, start_strategy or
   stop_strategy, with a reason naming the facts it rests on.
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
| Strategies | Only GhostTangentCrossings and ChainFlowBuy, at most 3 running, in its own account |
| Kill switch | The desk's kill switch stops anything new |

## Shadow first

`Ai:AiTraderExecute` is off: it decides and places nothing (mode `shadow`). Placing paper orders through its own
manual book and strategy starts is the next step, switched on only after its shadow decisions have been read.

## In a market replay

Asked into a replay (Data → Replay, "AI Trader decides along"), it decides on the replay's clock (mode `replay`):
the brief is built as of the replayed moment, from the recorded bars, the recorded chain and the replay's prices.
It is judged on a fresh day's empty book and places nothing. The model can take up to a minute to answer, so above
2× a replay moves faster than it can look every ten replayed minutes.

## API (admin)

| Endpoint | What |
|---|---|
| `GET /api/AiTrader/status` | On or off, shadow or live, the limits, today's looks, the last three |
| `GET /api/AiTrader/decisions?day=&replay=&take=&beforeId=` | Decisions, newest first |
| `GET /api/AiTrader/decisions/{id}` | One decision with its brief, plan and result |
| `POST /api/Ai/agents/ai-trader/run` | One look now |

Today shows its day in an "AI Trader" card.
