# Today

The owner's one page (`/today`, admin-only, first in the console; admins land on it after signing in). The owner is
busy and does not want to answer yes or no to keep things moving (1 Oct 2026). Everything that matters on the day is
here, read-first. No section waits for an answer: the links lead to the pages where something can be changed.

| Section | What it shows | From |
|---|---|---|
| Needs a look | Live incidents (medium and above) with the Incident Explainer's one-line explanation, a check below the 80% pass mark, agents failing, Sentinel late (over 10 min), lessons learnt, open decisions; most serious first | incidents, ai_reports, ai_calls, ai_memories, the decision log |
| Trading today | Net, gross and charges per account; today's runs, worst net first; recap runs only counted (tests, never in a total) | the live run history (`RecapRuns`) |
| AI agents | Each built agent: on or off, calls and failures, reports (ok, invalid, failed), last activity, up to four highlights | ai_calls, ai_reports |
| Learning | Active memories; learnt and dropped today, with how or why; the Assistant check today and its last 7 days | ai_memories, check reports |
| System | Sentinel's last round, the latest checkup, the last deploy and any commit the gate holds, open incidents | sentinel_heartbeats, desk_checkups, data/deploy-history.json |
| Decisions | Every decision taken for or by the owner: what, by whom (owner, or Claude's default), status (decided, default, open) | `system_settings` `owner.decisions` |

`GET /api/Today` returns it all (`TodayBuilder`). The page polls every 30 seconds. `POST /api/Today/decisions` with
`{ date, title, decided, by, status }` records a decision; one with the same date and title replaces the earlier
entry.
