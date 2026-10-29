# The desk's timetable

Every job the desk runs by the clock, with its exact time, the days it runs on, and the file that decides.
All times are IST; the server's clock is Asia/Kolkata and synchronised by NTP.

Two words need care:

- **Weekday** means Monday to Friday. **Trading day** means an NSE trading day in the exchange calendar
  (`market_holidays`). A weekday can be an exchange holiday: Gandhi Jayanti on Friday 2 Oct 2026 is one.
- **"After the close"** in the deploy gate means after `market-close.sh` has run at **23:58**. It does not mean the
  NSE close at 15:30 (15:45 for options and futures from 7 Dec 2026) or the MCX close at 23:30.

## Sessions

| Market | Session |
|---|---|
| NSE and BSE cash, and the indices (computed from cash prices) | 09:15–15:30 |
| NSE and BSE options and futures | 09:15–15:30 until 6 Dec 2026; **09:15–15:45 from 7 Dec 2026**. On an index's expiry day its expiring contracts still stop at 15:30. |
| MCX | 09:00–23:30, or 23:55 while the US is off daylight saving |

### The 15:45 derivatives close from 7 December 2026

SEBI's circular HO/47/11/11(3)2025-MRD-POD2/I/23843/2026 of 15 Oct 2026 (its effective date is in para 6.1):

- index and stock derivatives trade to **15:45** on every trading day (paras 4.2.4 and 4.2.5), except that on an expiry
  day the **expiring index contracts stop at 15:30**. Stock derivatives trade to 15:45 on their expiry day too;
- the cash market keeps its continuous session to 15:30, followed by a Closing Auction Session from 15:30 to 15:40
  (para 4.2.1). The indices therefore stop moving at about 15:30 while their options trade on;
- an expiring index derivative settles on the index value computed from its constituents' VWAP over the last 30
  minutes of the cash session (para 1.1.9);
- orders carried into the closing auction (para 4.8) start on 8 Feb 2027. They concern the cash market, where the
  desk places no orders.

The rule and its date live in one place per language: `EquityMarketHours` (C#), `core/market_hours.py` (the Python
engine and Sentinel) and `web/src/lib/marketHours.ts` (the console). A later circular is a change of
`LaterDerivativesCloseFrom` / `LATER_DERIVATIVES_CLOSE_FROM` and the close beside it. `MarketSessionService` answers
the **FO** segment from it and the **CM** segment with 15:30, so code that asks for FO moves on 7 Dec and code that
asks for CM does not. Any day before 7 Dec, today's and every day a backtest or a replay reads included, answers
15:30 exactly as before.

What moves to 15:45 from 7 Dec (each item at 15:30 on the days before):

| What | How |
|---|---|
| The close sweep's stop of NSE and BSE runs, reason `Market closed (15:45 IST)` | `MarketCloseRules`. A run on an index whose options expire that day still stops at 15:30 (the runner trades the nearest expiry, which is that day's); a stock run trades to 15:45. |
| Feeds, the FYERS chain poller and the alerter stopped | `MarketHoursService`, with the same reason as the runs |
| The Dhan chain recorder, live greeks, chain and straddle pages | the FO session |
| The manual book's intraday square-off | options and futures at 15:45, an index option expiring that day at 15:30, a share at 15:30 (`ManualIntradaySquareOff`) |
| A live run may still start | until 15:45, except an index on its expiry day after 15:30 (`StrategyStartService.WhyMarketClosed`) |
| Option and future 1-minute bars, and their nightly candles | kept to 15:45 (`LiveDataService`, `DailyCandleArchiveService`); an index's or a share's still end at 15:30 |
| Ira's close briefing (15:55), the AI agents' quiet hours and the situation builder's (to 16:00), the news scorer and the market-intelligence backfills (to 15:55) | the same ten or fifteen minutes after the derivatives close they kept after 15:30. The AI digest waits for the later of the close and 15:45, as before. |
| A replay or recap of a day from 7 Dec | plays to 15:48 and ends at that day's 15:45 (15:30 for an index's expiry-day recap) |
| Sentinel's close checks and hold times, the console's Desk phase, markets pill, Day P&L axis, carry texts and option charts | the day's derivatives close |

What stays at 15:30 on every day: the indices and shares (their bars, forecasts and the analysis scoring, the
VIX), backtests (driven by the index's candles, which end at 15:30; a live run may hold its legs to 15:45 while a
backtest of the same day ends by 15:30), the feed failover and the feeds' own stall watch, Sentinel's NSE session for
its feed rule (after 15:30 the indices go quiet, and a recording list without options would read as a silent feed),
and the AI Trader's shadow book with its baseline and lesson checks (its own 15:30 square-off, written into its
prompts and its history; its live book is a manual book and follows the manual book's close). A strategy's own
square-off time (`eod_square_off_ist`, 15:15 by default) is its rule and stays.

Expiry settlement (`ExpirySettler`) stamps an expiring index contract at 15:30 and a stock contract at 15:45 from
7 Dec, and reads the underlying's last print before 15:30 in both cases. It does not compute the exchange's new
VWAP settlement price: an index's own bars carry no volume, and the desk keeps neither every constituent's minute
bars nor the index weights. The reason line names the price used, so the gap of a few points on a day that moves
in its last half hour is visible.

From 7 Dec the index stops ticking at 15:30 while a run's option legs are open to 15:45. A strategy reads the index,
so in those fifteen minutes it has nothing new to act on; its legs are still marked and risk-guarded on their own
quotes until the close squares them off.

## A weekday, in order

| Time | Job | Days | Decided in |
|---|---|---|---|
| 00:00–06:00 | `market-close.sh` for the day before, only if 23:58 was missed | weekdays | `market_close_due`, `scripts/lib/desk-common.sh` |
| 00:15 | Sentinel's end-of-day checkup (a message only when something is wrong) | every day | `sentinel/checkup/slots.py` |
| 20 minutes after the archive is due (00:10, or 00:35 the US winter) | AI Data Curator's night pass: code checks the trading day's recorded data, the model triages what it found ([AI](ai.md#data-curator)). Waits for the archive's marker, at most two hours. | after trading days | `DataCuratorAgent.cs` |
| 06:00 | Nightly archive to Google Drive | every day | server crontab, `scripts/archive-to-drive.sh` |
| 08:00–09:00 | AI Data Curator's morning pass: the trading day before, and today's expiries | trading days | `Ai:DataCuratorMorningIst`, `DataCuratorAgent.cs` |
| until 08:40 | Deploys build and restart (no live run needed) | weekdays | `deploy_allowed`, `desk-common.sh` |
| from 08:00 | Dhan automatic sign-in: first try at the later of 08:00 and 2 minutes after the last token ends; never while a token is live | weekdays | `DhanAutoSignIn.cs` |
| 08:10 | Telegram alert if there is still no Dhan token (moves later with the token's end, never past 08:40) | trading days | `DhanAutoSignIn.cs` |
| 08:45 | `market-open.sh`: FYERS token, feeds, the morning plan into the accounts; runners start about 08:46. It stops by itself on an exchange holiday. | weekdays | `scripts/desk.sh`, `scripts/market-open.sh` |
| 08:50 | Forecasts issued | trading days | `ForecastScheduler.cs` |
| 08:55 | Sentinel's before-the-open checkup | trading days | `slots.py` |
| 15:45 | AI Trade Reviewer may start on stopped runs (off until switched on) | when runs stopped | `Ai:ReviewAfterIst`, `TradeReviewerAgent.cs` |
| from 15:45 | The AI's one Telegram digest: the AI Trader's day, the run reviews, and a line for each of the Data Curator, the Compliance Triage and the Strategy Researcher that wrote since the last digest. After the NSE close and 15:45; while reviews are still owed it waits, at most until 17:45. Once a day. A day it missed (the API down from the close to midnight) is sent on the API's next minute, titled "(sent late)"; only the latest missed day, older ones as one line. | trading days; other days only if the AI Trader looked or one of those three wrote | `Ai:ReviewDigestToTelegram`, `AiDailyDigest.cs` |
| 15:50 | Forecasts scored | trading days | `ForecastScheduler.cs` |
| 16:00 | Sentinel's after-the-close checkup | trading days | `slots.py` |
| 16:40 | AI Assistant check (the daily golden test) | trading days | `Ai:AssistantCheckAfterIst`, `AssistantCheckAgent.cs` |
| from 20:30 | SEBI, NSE and BSE circulars and FYERS notices recorded with their text, once a day ([market intelligence](market_intelligence.md#sebi-nse-and-bse-circulars)) | every day | `MarketIntelligenceSchedule.CircularsDue` |
| 23:30 / 23:55 | MCX closes; crude runs end with it | trading days | the strategy's own session |
| 23:50, or 00:15 the next morning | Nightly candle archive: the day's live 1-minute bars into 1/5/15-minute candles, and the broker's own index candles. Due at `Archive:RunAtIst` (23:50) or 20 minutes after the day's last exchange close, whichever is later; the closes come from the session service. So 23:50 while MCX closes at 23:30, and 00:15 the next morning while it closes at 23:55 (the US on standard time, November to March). A day missed (API down) is caught up on the API's next minute, up to 7 days back. A day whose archive fails is tried again after 5 minutes, 15, then hourly, not every minute. | every day | `ArchiveSchedule.cs`, `NightlyArchiveService.cs`, `MarketSessionService.cs` |
| right after the archive | India VIX check: the last 20 trading days' 1/5/15-minute VIX bars, every missing one asked of the history vendors; a day still unusable is an error and one System message ([data module](data_module.md#india-vix)). Runs when a day just archived was an NSE trading day, so also after a catch-up. | trading days | `NightlyArchiveService.cs`, `VixBackfill.cs` |
| within 10 minutes of the archive | The AI Trader's similar-moments history: the day the candle archive has just finished is cut into its 35 moments per index (`ai_trader_situations`), then the copy the brief reads is reloaded. The builder looks every 10 minutes and never from 08:45 to 15:45 on a trading day (the NSE session from the session service, less 30 minutes before and plus 15 after); the one-off backfill an admin starts runs the same way. Nothing runs until that backfill is started. | every day, once started | `AiTraderSituationBuilder.cs` |
| 23:58 | `market-close.sh`: every run flat and stopped, feeds and recorders off. The deploy gate opens right after it. | weekdays | `scripts/desk.sh`, `desk-common.sh` |

Always running: the desk checks the API's health every 30 seconds and GitHub every 2 minutes. The AI docs index
refreshes every 6 hours.

Sundays at 18:00 Sentinel sends the weekly review. At weekends deploys go out at once.

Saturdays from 10:00 the AI Compliance Triage reads the week's circulars (`Ai:ComplianceDay`, `Ai:ComplianceAfterIst`);
Sundays from 11:00 the AI Strategy Researcher writes its weekly note (`Ai:ResearchDay`, `Ai:ResearchAfterIst`), never
inside a session. The digest goes after 15:45 on those days when either wrote.

## When a deploy goes out

A web or API change pulled on a weekday between 08:40 and 23:58 waits. It goes out at the first 2-minute check
after `market-close.sh` has run (about 23:59–00:01), or before 08:40 the next weekday. A live run keeps it waiting
even inside those windows.

The desk's message names the time: "waits for the evening close job at 23:58 (market-close.sh, after the MCX
close)". `touch ~/.local/state/algotrading/deploy-now` sends one out at once; the file counts for an hour.

A deploy of scripts, docs or Python only rebuilds nothing and is never held. Python runners pick up new code at
their next start. `desk.sh` and `desk-common.sh` load when the desk starts, so a change to them takes effect after
`sudo systemctl restart algotrading-desk`.

## Rules for code and tests that depend on the time

These come from real mistakes; each rule names the one it prevents.

1. **Say "trading day" when the job is about the market.** Checking only for Saturday and Sunday runs the job on
   exchange holidays. Ask `IMarketSessionService.GetSessionInfo(…, "NSE", "CM").IsTradingDay`, as the forecast
   scheduler does. (The Assistant check skipped only weekends until 1 Oct 2026.)
2. **Name a time from the code, never from the market's hours.** On 30 Sep 2026 a deploy was promised for "after
   the 23:30 MCX close". The gate opens after the 23:58 job, and the deploy went out at 23:59.
3. **A figure read from a live run can move while it is being read.** A check that compares two readings of it
   must allow for the time between them. (The Assistant check marked a correct day total wrong by ₹320 while the
   crude run was trading.)
4. **In tests, a time relative to "now" can fall on another IST day.** "An hour ago" at 00:30 IST is yesterday.
   Use a fixed time, or ask for the day the seeded data is on. (A tool test failed from 00:00 to 01:00 IST.)
5. **Check a test run's exit code, not the last line of its output.** `dotnet test … | tail -1` succeeds even
   when a test failed, and a commit went out that way on 1 Oct 2026.
6. **After a push, read the CI result.** The script tests were red on every push from 28 Sep to 1 Oct 2026 and
   nobody looked: Linux `lsof` omits a field that macOS always prints (see [local-live](../dev/local-live.md)).
