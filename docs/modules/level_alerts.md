# Level alerts

"Tell me when NIFTY crosses 24,800." "Tell me when NIFTY's PCR goes above 1.3."
A level alert watches one number against one level and says when it gets
there: a price on any symbol the platform quotes, or the put-call ratio of an
option chain the recorder captures. They live on **Markets → Alerts**
(`/markets/alerts`), for every user with the market-data grant: each trader
sets and sees their own, an admin sees everyone's.

Candle patterns and indicator crosses are a different module, on closed
candles: see [Candle pattern alerts](pattern_alerts.md).

## What a rule says

| Watches | Conditions | Level |
| --- | --- | --- |
| **Price**: the last traded price of a symbol (`NSE:NIFTY50-INDEX`, `NSE:SBIN-EQ`, `MCX:CRUDEOIL26NOVFUT`, an option) | crosses above · crosses below · touches | a price |
| **PCR**: total put OI over total call OI of an underlying's nearest-expiry chain (`NIFTY`, `BANKNIFTY`, `SENSEX`, `CRUDEOIL`, …) | goes above · goes below | a ratio, at most 10 |

Each rule also has:
* **Once** (the default) or **every time**. Once stops after it fires
  (state *fired*; *Re-arm* starts it again). Every time fires, then waits until
  the value has moved back from the level by the re-arm distance before it can
  fire again: 0.2% of the level for a price, 0.05 for a PCR, unless set. So
  "every time NIFTY crosses above 24,800" fires once when it crosses, not on
  every tick while NIFTY wanders between 24,795 and 24,812, and again after it
  has been back to 24,750.40.
* An optional **note** (up to 200 characters), sent with the alert.
* An optional **last day**: the rule fires through that IST day and is then
  shown as *expired*.

Only what the platform knows can be watched: a symbol in the instrument master
or one a feed quotes, and an index or commodity the chain recorder follows (or
any underlying with a recorded chain). A typo is refused when the rule is saved
rather than waiting all day for a quote that never comes. One account holds at
most 50 rules.

The form shows the number the rule will be compared with before it is saved
("Now: 24,812.35 · 2s ago"), read the way the service reads it, and says so
when the level is already behind it: "Now 25,010, already above 24,800: it
fires only after the price has been at or below 24,800 and goes above again."

## When it fires

* **A cross is two readings on opposite sides of the level.** Above is strictly
  above, the way the indicator alerts read RSI against 70: from at or below the
  level to above it. A price that prints exactly 24,800 and falls back has not
  crossed 24,800; *touches* is for that. *Touches* fires when a reading is on
  the level or the move between two readings went through it.
* **Fresh readings only.** A price counts for 60 seconds after the exchange
  stamped it (the time the API wrote it when the feed sends no stamp, or only a
  date, as BSE's pre-open replay of the close does). A chain capture counts for
  3 minutes: the recorder captures the nearest expiry about once a minute. A
  reading older than that is shown on the page as stale and never fires.
* **In the symbol's own session.** NSE and BSE 09:15–15:30, MCX from 09:00 into
  the evening, holidays and special sessions from the platform's calendar. A
  price stamped before the open (the pre-open auction) is not compared.
* **The first reading of a session is a baseline.** An overnight gap through
  the level is not a cross: nothing traded at the level, and a rule must not
  fire or stay quiet depending on whether the API restarted overnight (it
  usually does, when the desk deploys). The same holds for the first reading
  after a restart in the session: a cross while the API was down is not
  reported. Whether a repeating rule is armed is stored on the rule, so a
  restart never re-fires a level the price is still sitting on.
* **A PCR** is compared at the two decimals the chain page shows, so "went above
  1.30" never arrives while the page still reads 1.30. It is the newest
  capture's PCR, the number the chain's OI-history line plots; the chain
  page's header can differ by a few hundredths between captures, because it
  overlays the live OI the feed streams for the strikes near the money.

A rule made while the value is already past its level does not fire until the
value has come back and crossed again. That is the definition of a cross, and
the form says it up front.

## Delivery

When a rule fires, a row is written to `level_alert_fires` with the time, the
value and the reading before it, and the rule's own words (so the history still
reads after the rule is edited or deleted). Then:
* **The console.** A desk event (kind `alert`) goes to the owner's connections
  and the admins'; the top bar's bell shows the owner's new alerts, and opening
  the page marks them seen. The badge also polls every 30 seconds, for a
  socket that is down.
* **Telegram, for an admin's rules.** Sent to the Desk System chat
  (`Telegram:SystemChatId`, or the trades chat while that is not set). All the
  fires of one scan go as one message, at most 6 messages in 5 minutes; one over
  the limit stays on the page, marked why. A rule can turn Telegram off. A
  trader's alerts stay in the console: the desk has no Telegram chat per trader,
  and the Desk System chat is the operator's.

The fires are not written to `alert_events`: that stream is readable by every
signed-in account (System → Log), and a trader's levels are theirs.

## The service

`LevelAlertService`, a hosted service in the API, every 5 seconds:

1. Outside every exchange's hours it reads nothing at all: the session calendar
   is in memory.
2. In session, it reads the active rules (one query), then each watched target
   **once**, however many rules share it: every watched symbol's row in
   `live_quotes_latest` in one query, and each chain's newest capture at most
   every 15 seconds, adding the chain up only when a new capture has arrived.
   No vendor is called: the prices are the ones the feeds already wrote, the
   chains the ones the recorder already captured.
3. It compares each fresh reading with the last one it kept for that target
   (in memory) and writes a rule only when it fires or re-arms, and only if its
   owner has not saved, paused or deleted it since the scan read it.

On the 2-vCPU server that is, in session, about 0.4 small indexed queries a
second plus a few every 15 seconds per watched chain; outside session, none.
At most 500 symbols and 20 chains are read in one scan (the page names any
left out).

`LevelAlerts:Enabled=false` turns the service off; when it is not set it
follows `PatternAlerts:Enabled`, the switch a second API on the same database
already sets. Both take true/false, on/off, yes/no or 1/0, and a spelling that
is none of them turns it off and says so, without stopping the API.

## Storage

Migration `LevelAlerts` (additive): two new tables, nothing else changed.

| Table | Holds |
| --- | --- |
| `level_alert_rules` | Owner, kind (`price`/`pcr`), target, condition (`above`/`below`/`touch`), level, repeat and re-arm distance, note, last day, Telegram, state (`active`/`paused`/`fired`, checked), armed, fire count and last fire. Deleted with the account. |
| `level_alert_fires` | Rule id (not a foreign key: a deleted rule keeps its history), owner, time, value and the reading before it with their times, title and message, whether Telegram got it and why not, when its owner saw it. Deleted with the account. |

## API

Under `/api/LevelAlerts`, all behind the market-data grant (admins pass by
role). A trader reads and changes only their own rules and fires; another
user's rule answers 404, not 403, so a trader cannot learn which ids exist. The
owner always comes from the sign-in token.

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `rules?mine=` | Rules, newest first, each with its target's last reading; an admin gets everyone's unless `mine=true` |
| POST · PUT · DELETE | `rules`, `rules/{id}` | Create, edit (including pause, resume and re-arm through `active`), delete |
| GET | `fired?mine=&ruleId=&limit=` | What fired, newest first |
| POST | `fired/seen` | Marks the caller's own new fires seen |
| GET | `summary` | The top bar's count: the caller's unseen fires and today's |
| GET | `status` | The service's health and what it is watching (for a trader, only their own targets) |
| GET | `now?kind=&target=` | What a target reads now, for the form |

## Not built yet

* **Per-trader Telegram.** A trader's alerts reach their console only; the desk
  has no chat per trader to send them to.
* **Through-the-gap detection.** A move that goes through a level and back
  between two readings (5 seconds apart for a price) is not seen. Reading the
  ticks as they arrive would catch it, at the cost of work on the tick path.
* **Other numbers.** VIX levels work today as a price (`NSE:INDIAVIX-INDEX`
  when a feed streams it); IV, OI and the PCR of a later expiry are not offered.
