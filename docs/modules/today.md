# Today

The owner's one page (`/today`, admin-only, first in the console; admins land on it after signing in). The owner is
busy and does not want to answer yes or no to keep things moving (1 Oct 2026). Everything that matters on the day is
here, read-first. No section waits for an answer: the links lead to the pages where something can be changed.

| Section | What it shows | From |
|---|---|---|
| Needs a look | Live incidents (medium and above) with the Incident Explainer's one-line explanation, a check below the 80% pass mark, agents failing, Sentinel late (over 10 min), lessons learnt, open decisions, the Data Curator's high findings from its latest pass (with what breaks and what to do), the circulars the Compliance Triage found apply this week and nobody marked handled, with when they take effect; most serious first | incidents, ai_reports, ai_calls, ai_memories, the decision log |
| Trading today | Full-run net, realized net, open P&L and charges per account; today's runs and carried books, worst total net first; recap runs only counted (tests, never in a total) | the live run history (`RecapRuns`) |
| AI agents | Each built agent: on or off, calls and failures, reports (ok, invalid, failed), last activity, up to four highlights | ai_calls, ai_reports |
| AI Trader | On or off, shadow or live; today's looks, actions allowed and refused, the shadow book's positions and net after charges; its last three decisions | ai_trader_decisions, ai_trader_shadow_positions |
| Learning | Active memories; learnt and dropped today, with how or why; the Assistant check today and its last 7 days; the latest weekly exam (pass^3, practice and held out) | ai_memories, check and exam reports |
| System | Sentinel's last round, the latest checkup, the last deploy and any commit the gate holds, open incidents | sentinel_heartbeats, desk_checkups, data/deploy-history.json |
| Decisions | Every decision taken for or by the owner: what, by whom (owner, or Claude's default), status (decided, default, open) | `owner_decisions` (one row each; a single `system_settings` value until 1 Oct, when the sixth decision did not fit its 2,000 characters) |

`GET /api/Today` returns it all (`TodayBuilder`). The page polls every 30 seconds. `POST /api/Today/decisions` with
`{ date, title, decided, by, status }` records a decision; one with the same date and title replaces the earlier
entry.

## Trading P&L

The headline **Run net P&L**, account totals and worst-run ranking use:

- Realized gross: booked P&L over every position of each selected run, including partial closes.
- Charges: the existing statutory fill-charge calculation used by run detail.
- Realized net: realized gross minus charges.
- Open P&L: all remaining open legs at the latest available quote, with stored unrealized P&L as the fallback.
- Total net: realized net plus open P&L.

Stopping a runner does not remove residual open positions from its P&L. The selection includes runs started
on the current IST day, older runs still running, and older runs with open positions or a position closed today.
Recaps and backtests are excluded. Closed positions from older runs with no current-day activity are excluded.
The account totals cover the complete selection, without the history list's 500-row page limit.

These are **full-run totals, not the change since midnight**: a carried manual book can include earlier realized
P&L and entry charges. Realized net and open P&L are shown separately, including on mobile. Components are rounded
to cents per run and those same figures are summed for accounts; the headline sums the account values.
All sections in a Today response use the same valued run rows. Separate requests can differ as quotes or fills
change, and missing live quotes still use the existing stored-mark fallback.

The history API's `netPnl` remains realized net for compatibility. Its `unrealizedPnl` now covers residual open
legs even if the runner is inactive; console history totals add it once. No trade ledger or charge model changes.
