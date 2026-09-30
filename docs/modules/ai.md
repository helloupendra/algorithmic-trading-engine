# AI workspace

The desk's hosted language models live on one console page. The page shows:

- which agents exist and which are built;
- the model chain each agent walks;
- when each agent was last called;
- every call it made, in full;
- the owner's own questions, streamed.

There is one agent so far, the **Desk Assistant**. Since Phase 2 it reads the desk through read-only tools. The
other thirteen agents are listed as planned, with the phase that builds them, so the page shows the whole plan and
not just what runs.

- Console: **AI**, admin only:
  - `/ai`: overview;
  - `/ai/assistant`;
  - `/ai/agents`;
  - `/ai/models`;
  - `/ai/calls`.
- Code: `src/AlgoTrading.Infrastructure/Ai/`, `src/AlgoTrading.Api/Controllers/AiController.cs`.
- Table: `ai_calls`.

## Ground rules

- **Owner and admins only.** The models run on NVIDIA's free hosted endpoint (build.nvidia.com). Its terms cover
  development, testing, research and evaluation. Serving traders would be production use, so every endpoint is
  `AdminOnly`.
- **No model holds an order tool.** Nothing in the live trading path waits on a model. A future agent may *suggest*;
  code decides.
- **The key never leaves the server.** It sits in the server's `.env` as `NVIDIA_API_KEY`. The desk writes it into
  `appsettings.Local.json` on each deploy (`scripts/_gen_local_settings.py`), and it is sent only in the provider's
  `Authorization` header. The browser learns `keyConfigured: true` and nothing more. Every stored text is scrubbed
  of it as a second layer.
- **Text a model reads is data, not instructions.** This matters from Phase 3, when agents read news and filings.

## Tiers and chains

A tier is an ordered chain of models. When a model fails, the call moves to the next one. A model fails when it:

- times out;
- answers 429 or 5xx, or any other 4xx (a model withdrawn from the free tier answers 404);
- breaks off mid-stream;
- answers with nothing.

Whatever that model had streamed is dropped. An answer is never half one model's and half another's.

| Tier | Chain (first model first) | For |
| --- | --- | --- |
| Judge | Nemotron 3 Ultra → Kimi K3 → GLM-5.3 | The hardest reasoning, few calls a day |
| Analyst | Kimi K3 → GLM-5.3 → Nemotron 3 Super | Reading data the code computed |
| Extract | DeepSeek V4.1 Flash → Nemotron 3.5 Lightning → GLM-5.3 Flash | High volume, strict JSON |
| Embed | Nemotron 3 Embed 1B | Search (vectors, not answers); not used yet |

The owner can change a tier's chain, or give one agent its own chain, from the Models and Agents tabs. The change is
stored in `system_settings` under `ai.`. The defaults are in `AiCatalog` and match `core/llm.py`'s `DEFAULT_CHAINS`,
which Python scripts use directly. `AiSettingsTests` fails if the two drift.

### Latency on the free tier (measured 30 Sep, ~16:30 IST)

| Model | "Reply OK", streamed |
| --- | --- |
| Nemotron 3 Ultra | 1.2 s (a real question: first token 1.6 s, done in 3 s) |
| Nemotron 3 Super | 1.0 s |
| Kimi K3 | 78 s |
| GLM-5.3 Flash | 48 s |
| GLM-5.3 | no first token in 90 s (159 s unstreamed) |
| DeepSeek V4.1 Flash | no first token in 90 s (none in 170 s unstreamed) |
| Nemotron 3.5 Lightning | no first token in 90 s (none in 170 s unstreamed) |

The non-NVIDIA models queue on the free tier: each one in a chain can cost the full first-token limit before it
hands over. The Models tab's **Test** button measures this at any time, and each test is kept in the call log.

## A call

`AiGateway` is the one door every call goes through. In order:

1. **The agent's switch.** A planned agent, or one switched off, is refused (409).
2. **The key.** Without one the call is refused (503), with where the key goes.
3. **The desk's own rate limit** (`AiRateLimiter`, in memory). A call is refused (429, with `Retry-After`) when it would
   exceed any of these:
   - 30 questions per user in ten minutes;
   - 30 calls per minute for the whole desk, below the free tier's unpublished limit of about 40;
   - 4 calls in flight at once, and 2 per user.
4. **The audit row.** It is written as `running` before the first model is asked, so a slow call shows on the Calls tab.
5. **The chain.** Each model is asked in turn, streamed. Every model tried becomes an attempt with its outcome, seconds
   and HTTP status.
6. **The row is completed**, however the call ended:
   - `ok`: the answer, the reasoning, the tokens and the finish reason;
   - `failed`: every model's reason;
   - `cancelled`: the asker stopped it; what had arrived is kept.

A refused call is a row too, with no attempts, so "why did nothing happen" has an answer on the page. A row still
`running` 20 minutes after it started (the API restarted under it) reads as `failed`.

Timeouts per model (`Ai` settings):

| Limit | Default | Meaning |
| --- | --- | --- |
| First token | 90 s | Headers and the first reasoning or answer token |
| Silence | 60 s | A gap between tokens, mid-answer |
| Ceiling | 300 s | The whole attempt |

The stream is not only a nicety. Behind Cloudflare, a response that sends nothing for 100 seconds is cut, and a
reasoning model can think for longer than that.

## Desk tools (Phase 2)

The Desk Assistant answers from the desk's own records. The model asks for tools itself, in rounds:

1. The model either answers or asks for tools.
2. The gateway runs the tools on the server and asks the model again with what they found.
3. This repeats until the model answers.

Every tool only reads (`IAiTool`); none places, cancels or switches anything. Each tool is built from the service the
matching console page uses, so the numbers are the page's numbers.

| Tool | Reads | Built on |
| --- | --- | --- |
| `get_runs` | A day's runs with net P&L (realized − charges + open legs while active, as the Desk shows) | `LiveRunHistoryBuilder` |
| `get_run` | One run: settings, P&L, then a summary or one section (legs, orders, signals) in a time window | `PositionViewBuilder`, `RunPnl`, the run's rows |
| `get_open_positions` | Every open leg, marked, with its mark's age | `OpenPositionsBuilder` |
| `get_quotes` | Indices, large caps and commodities with the day's change; India VIX | `IMarketPulseService`, the NIFTY chain header |
| `get_option_chain_summary` | PCR, max pain, walls, ATM IV, OI totals for an underlying | `OptionChainService` (the header) |
| `get_incidents` | Sentinel's live or recent incidents | `incidents` |
| `get_latest_checkup` | The newest finished checkup, every item with what to do | `CheckupsController.ToDetail` |
| `get_forecasts` | A session's forecasts and scores | `ForecastsController.ToView` |
| `get_news` | Headlines and a stock's filings, with FinBERT sentiment | `MarketIntelligenceQueries` |
| `get_strategy_spec` | A strategy's written spec, or the list of strategies | `StrategyCatalogService`, `docs/strategies` |

`get_run` starts with a summary on purpose. On 30 Sep one busy run had 344 legs, 688 orders and 9,636 signals. The
summary gives:

- counts;
- realized P&L by IST hour;
- the five best and worst legs;
- the open legs;
- the last signals.

A small run gets its full lists as well. For the rest, the model asks for a section and an `HH:mm` window.

### Limits

Each limit is an `Ai` setting.

| Limit | Default |
| --- | --- |
| Rounds in which tools are open | 4 |
| Tool calls per question | 8 |
| Characters of one tool's answer | 16,000 (past it the model is told to narrow down) |
| Time per tool | 30 s |

After the last tool round the tools close (`tool_choice: none`). If a model still asks for one, the question moves on
to the next model. The same call made twice in one question is read once. A model that failed in an earlier round is
not asked again in the same question.

Every way a tool call can go wrong is answered to the model in words, so it can correct itself. This covers:

- an unknown tool;
- arguments that are not valid JSON, or out of range;
- a tool that is slow or throws. The model sees the exception's type only, never its message.

### What leaves the server

A tool's answer is sent to the provider, so it is projected field by field, never an entity. Free text (stop reasons,
incident evidence, checkup items) goes through `IncidentRedaction.Mask` and then `AiToolFormat.Hosts`. Between them
they remove:

- tokens and passwords;
- the machine's name, EC2 host names and IPv4 addresses;
- home paths.

Emails, broker ids and user ids are never selected. Account user names are kept: the questions are about accounts.
`AiToolDataTests` seeds the desk with each of these secrets and reads every tool's answer as the model would.

### The audit row

Each call also stores:

- `ToolsJson`: every call with its round, arguments, outcome, rows, as-of time, seconds, and the exact text the model
  was given;
- `Rounds` and `ToolCalls`;
- the round of each attempt.

The stored reasoning keeps each round's working, labelled `[round n]`.

## The event stream

`POST /api/Ai/ask/stream` answers `text/event-stream`:

| Event | Data |
| --- | --- |
| `start` | `{ callId, chain }` |
| `attempt` | `{ model, n, of, round }`: `n`/`of` count the models still available in that round |
| `reasoning` | `{ text }`: a piece of the model's reasoning (Nemotron streams it first) |
| `delta` | `{ text }`: a piece of the answer |
| `fallback` | `{ model, reason, next }`: discard what streamed since the last `attempt` |
| `tool` | `{ round, id, name, arguments, ok, error, seconds, rows, asOfUtc, summary, resultChars, result }`: a tool ran; what streamed in this round was working, not the answer |
| `done` | `{ callId, model, seconds, finishReason, usage, fallbacks, toolCalls, rounds }` |
| `error` | `{ callId, error }`: every model failed |

A `: ping` comment goes out every 15 s. Closing the request cancels the call.

A refusal comes back before any event as plain JSON `{ error, callId, retryAfterSeconds }`:

- 400: bad input;
- 409: agent off;
- 429: limit;
- 503: no key.

`POST /api/Ai/ask` is the same call without streaming, for scripts. Behind Cloudflare it is cut at 100 s.

## Endpoints

All under `api/Ai`, admin only.

| Endpoint | What |
| --- | --- |
| `GET overview` | Provider and key status, tiers, today's numbers (IST day), per-model use, last success and error, agent counts |
| `GET agents` | Every agent (built or planned) with its chain, switch, last call and today's use; the desk's rule-based parts |
| `PUT agents/{key}` | `{ enabled, chain, resetChain, reason }`: 409 for a planned agent |
| `PUT tiers/{tier}` | `{ chain, reason }`: an empty chain goes back to the default |
| `GET models?refresh=` | The provider's catalog (cached 10 min), in-use models first, each one's last test and today's attempts; local models (FinBERT) |
| `POST models/test` | `{ model }`: one tiny question to one model, logged as agent `model-test` |
| `GET calls?agent=&outcome=&model=&take=&beforeId=` | The log, newest first, paged by id |
| `GET calls/{id}` | One call in full: system prompt, messages, attempts, reasoning, answer, usage, the request's parameters |
| `POST ask/stream`, `POST ask` | Ask: `{ messages, tier, system, maxTokens, temperature, conversationId, agent }` |

## Adding a tool

1. Implement `IAiTool` in `src/AlgoTrading.Api/Services/AiTools/`:
   - take only query services;
   - return a projection;
   - pass free text through `AiToolFormat.Text`;
   - bound the rows.
2. Register it in `Program.cs`.
3. Add its name to `AiToolNames` and to the agent's `Tools` in `AiCatalog`.
4. Add it to `AiToolDataTests`.

## Adding an agent

1. Build it: code that assembles its input from the desk's own data. The model reads that input; it never fetches
   anything itself.
2. In `AiCatalog.Agents`, set its entry to `Built: true`, with its system prompt.
3. Call `AiGateway.AskAsync` with its key and `Source = "schedule"`, or `POST /api/Ai/ask` with the engine's service
   account (that path is not open to the Service role yet).

The switch, the chain, the rate limit and the audit row then apply to it without further work.
