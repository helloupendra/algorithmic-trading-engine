# The desk's timetable

Every job the desk runs by the clock, with its exact time, the days it runs on, and the file that decides.
All times are IST; the server's clock is Asia/Kolkata and synchronised by NTP.

Two words need care:

- **Weekday** means Monday to Friday. **Trading day** means an NSE trading day in the exchange calendar
  (`market_holidays`). A weekday can be an exchange holiday: Gandhi Jayanti on Friday 2 Oct 2026 is one.
- **"After the close"** in the deploy gate means after `market-close.sh` has run at **23:58**. It does not mean the
  NSE close at 15:30 or the MCX close at 23:30.

## Sessions

| Market | Session |
|---|---|
| NSE (equity and F&O) | 09:15–15:30 |
| MCX | 09:00–23:30, or 23:55 while the US is off daylight saving |

## A weekday, in order

| Time | Job | Days | Decided in |
|---|---|---|---|
| 00:00–06:00 | `market-close.sh` for the day before, only if 23:58 was missed | weekdays | `market_close_due`, `scripts/lib/desk-common.sh` |
| 00:15 | Sentinel's end-of-day checkup (a message only when something is wrong) | every day | `sentinel/checkup/slots.py` |
| 06:00 | Nightly archive to Google Drive | every day | server crontab, `scripts/archive-to-drive.sh` |
| until 08:40 | Deploys build and restart (no live run needed) | weekdays | `deploy_allowed`, `desk-common.sh` |
| from 08:00 | Dhan automatic sign-in: first try at the later of 08:00 and 2 minutes after the last token ends; never while a token is live | weekdays | `DhanAutoSignIn.cs` |
| 08:10 | Telegram alert if there is still no Dhan token (moves later with the token's end, never past 08:40) | trading days | `DhanAutoSignIn.cs` |
| 08:45 | `market-open.sh`: FYERS token, feeds, the morning plan into the accounts; runners start about 08:46. It stops by itself on an exchange holiday. | weekdays | `scripts/desk.sh`, `scripts/market-open.sh` |
| 08:50 | Forecasts issued | trading days | `ForecastScheduler.cs` |
| 08:55 | Sentinel's before-the-open checkup | trading days | `slots.py` |
| 15:45 | AI Trade Reviewer may start on stopped runs (off until switched on) | when runs stopped | `Ai:ReviewAfterIst`, `TradeReviewerAgent.cs` |
| 15:50 | Forecasts scored | trading days | `ForecastScheduler.cs` |
| 16:00 | Sentinel's after-the-close checkup | trading days | `slots.py` |
| 16:40 | AI Assistant check (the daily golden test) | trading days | `Ai:AssistantCheckAfterIst`, `AssistantCheckAgent.cs` |
| 23:30 / 23:55 | MCX closes; crude runs end with it | trading days | the strategy's own session |
| 23:58 | `market-close.sh`: every run flat and stopped, feeds and recorders off. The deploy gate opens right after it. | weekdays | `scripts/desk.sh`, `desk-common.sh` |

Always running: the desk checks the API's health every 30 seconds and GitHub every 2 minutes. The AI docs index
refreshes every 6 hours.

Sundays at 18:00 Sentinel sends the weekly review. At weekends deploys go out at once.

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
