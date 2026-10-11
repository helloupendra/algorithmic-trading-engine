# AI workspace

The desk's hosted language models live on one console page. The page shows:

- which agents exist and which are built;
- the model chain each agent walks;
- when each agent was last called;
- every call it made, in full;
- the owner's own questions, streamed.

All fifteen agents are built (owner, 10 Oct 2026: every AI agent built and on):
- the **Desk Assistant**, which the owner asks and which reads the desk through read-only tools (Phase 2);
- three scheduled, advisory agents from Phase 3: the **Trade Reviewer**, the **News Analyst** and the
  **Incident Explainer**;
- four desk analysts from Phase 4 (10 Oct): the **Technical**, **Options/OI**, **Macro** and **Fundamental Analysts**
  ([The desk analysts](#the-desk-analysts-phase-4));
- three from Phase 4 that word what code measured, on from the start: the **[Data Curator](#data-curator)**, the
  **[Compliance Triage](#compliance-triage)** and the **[Strategy Researcher](#strategy-researcher)**;
- the [AI Trader](ai_trader.md), and from 10 Oct the three checks around its live paper orders: the **Validator**, the
  **Risk Memo** and the **Execution Reporter** ([below](#the-checks-around-the-ai-trader)).

None is left planned. The page still shows each agent's phase, so it reads as the whole plan and not just
what runs. The owner can talk with each built agent, the [AI Trader](ai_trader.md) included, not only the Desk
Assistant ([Talking to the agents](#talking-to-the-agents)).

- Console: **AI**, admin only:
  - `/ai`: overview;
  - `/ai/assistant`;
  - `/ai/agents`;
  - `/ai/models`;
  - `/ai/calls`.
- Code: `src/AlgoTrading.Infrastructure/Ai/`, `src/AlgoTrading.Api/Controllers/AiController.cs`,
  `src/AlgoTrading.Api/Services/AiAgents/`.
- Tables: `ai_calls`, `ai_reports`; `regulatory_circulars` for the Compliance Triage.

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
- **Amazon Bedrock has no key at all.** Claude there (off by default) and the open models there (on by default) sign
  with the server's own AWS role; no AWS credential is in the repo, the settings or `.env`
  ([Providers](#providers-nvidia-and-amazon-bedrock)).
- **Text a model reads is data, not instructions.** This matters from Phase 3, when agents read news and filings.

## Tiers and chains

A tier is an ordered chain of models. When a model fails, the call moves to the next one. A model fails when it:

- times out;
- answers 429 or 5xx, or any other 4xx (a model withdrawn from the free tier answers 404);
- breaks off mid-stream;
- answers with nothing.

Whatever that model had streamed is dropped. An answer is never half one model's and half another's.

A refusal for capacity is treated differently. This covers "Service temporarily overloaded", 429, 502 and 503
when they come back at once. The same model is asked once more after `Ai:CapacityRetrySeconds` (2 s) before the
chain moves on. On 30 Sep both Nemotrons refused like this for moments at a time, while the next model in the chain
queued for 90 s.

In a question with several rounds, a model is left out of the later rounds only if its failure cost a long wait
(`Ai:SlowFailureSeconds`, 10 s). A model that refused at once is asked again in the next round.

### Model health: routing around a queue

Across questions, the desk learns from its own calls which models are answering (`AiModelHealth`).

- **Cooling.** A model cools in two cases: its failure cost a long wait, or it failed twice in a row. The first
  cooling lasts 10 minutes, then 20, 40, and at most 60 if it keeps failing.
- **Order.** While cooling, it is asked after its chain's healthy models. It is never dropped: when every healthy
  model fails, a cooling one is still asked.
- **Healing.** One answer makes it healthy again.
- **Probe.** `AiHealthProbe` asks each model whose cooling ended one tiny question through the gateway (agent
  `model-test`, source `health`, on the Calls tab), so a recovered model is back in its place before a person's
  question has to find out.
- **Restart.** At start the probe replays the last half hour of calls, so a deploy does not forget which models were
  queueing.
- **Where to see it.** The overview, the models list and `GET health` show each model's state: healthy, failed,
  cooling (with an until time) or unknown.

| Tier | Chain (first model first) | For |
| --- | --- | --- |
| Judge | Nemotron 3 Ultra → Nemotron 3 Super → Kimi K3 | The hardest reasoning, few calls a day |
| Analyst | Kimi K3 → Nemotron 3 Super → GLM-5.3 | Reading data the code computed |
| Extract | DeepSeek V4.1 Flash → Nemotron 3 Super → Nemotron 3.5 Lightning | High volume, strict JSON |
| Embed | Nemotron 3 Embed 1B | Search (vectors, not answers); not used yet |

The owner can change a tier's chain, or give one agent its own chain, from the Models and Agents tabs. The change is
stored in `system_settings` under `ai.`. The defaults are in `AiCatalog` and match `core/llm.py`'s `DEFAULT_CHAINS`,
which Python scripts use directly. `AiSettingsTests` fails if the two drift.

These are NVIDIA's chains. While Claude on Bedrock is off and the open models there are on (the default), each chat
tier walks four open models on Bedrock instead, and NVIDIA is not asked ([Open models on Bedrock](#open-models-on-bedrock)).
NVIDIA still makes the embeddings until Titan answers.
With Claude on, every tier is Claude ([Providers](#providers-nvidia-and-amazon-bedrock)).

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

Measured again at 18:46 IST:

| Model | Result |
| --- | --- |
| Ultra | 0.9 s |
| Super | 1.1 s |
| Lightning | 74 s |
| GLM-5.3 Flash | 85 s |
| Kimi K3, DeepSeek V4.1 Flash and GLM-5.3 | No response headers at all in 100 s |

Around 17:30 Ultra twice answered "Service temporarily overloaded".

The free endpoint queues the other vendors' models behind demand, and NVIDIA publishes no limits for it. A model in
the queue can cost the full first-token limit before it hands over. So since 30 Sep **Nemotron 3 Super is every
tier's first fallback**; `core/llm.py` keeps the same chains. The Models tab's **Test** button measures latency at any
time, and each test is kept in the call log.

## Providers: NVIDIA and Amazon Bedrock

A model's id names who serves it:

| Id | Provider | Asked through |
| --- | --- | --- |
| `vendor/model`, such as `nvidia/nemotron-3-super-120b-a12b` | NVIDIA build (NIM API) | `NvidiaChatClient`: the OpenAI-style `/chat/completions`, streamed, with `NVIDIA_API_KEY` |
| `bedrock:` and a Bedrock model id, such as `bedrock:anthropic.claude-opus-5-5` | Amazon Bedrock | `BedrockClaudeClient`: Anthropic's SDK (`Anthropic.Bedrock`), the Messages API at `bedrock-mantle.{region}.api.aws/anthropic/v1/messages`, streamed, signed with the server's AWS role |
| `bedrock-oss:` and a Bedrock model id, such as `bedrock-oss:moonshotai.kimi-k2.5` | Amazon Bedrock (open models) | `BedrockOpenChatClient`: the OpenAI-compatible `/chat/completions` at `bedrock-mantle.{region}.api.aws/v1/chat/completions`, streamed, each request signed with SigV4 |

`NvidiaChatClient` and `BedrockOpenChatClient` share one OpenAI-compatible client (`OpenAiCompatibleChatClient`): the
request, the stream, the tool calls and the usage are read the same way; only the endpoint, the authorization and the
meaning of a refusal differ.

`AiChatRouter` picks the client for each model, so one chain can name all three and fall back from one to another. A
model whose provider cannot be asked now is **skipped**: it is an attempt with its reason, for example `skipped:
Amazon Bedrock is off (Ai:Bedrock:Enabled is false)`, that took no time. A skip is not a fallback, and it does not
touch the model's health. A call left with nothing to ask is refused (503).

The Claude models a chain may name (Bedrock's Messages API has no model list, so the desk keeps its own, in
`AiCatalog.BedrockModels`): Claude Fable 5.1, Opus 5.5, Sonnet 5.5, Haiku 5.5, Opus 4.8, Sonnet 5 and Haiku 4.5, each as
`bedrock:anthropic.claude-…`.

### Settings

`Ai:Bedrock`:

| Setting | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Off: nothing calls AWS, and the chains are NVIDIA's. On: every chain is Claude (below). |
| `Region` | `us-east-1` | Where Claude, Titan and the open models are asked. Mumbai's Messages API answered "model does not exist" for these models on 10 Oct. The model ids carry no region prefix; the Global endpoint type routes across regions at no premium. |
| `EffortByTier` | judge `high`, analyst `high`, extract `low` | Claude's `output_config.effort`. A model's test from the Models tab asks at `low`. |
| `MinMaxTokens` | 16,000 | The fewest tokens a Claude answer may use: its thinking counts against the limit, and a caller's limit was set for the answer alone |
| `EmbeddingModel` | `amazon.titan-embed-text-v2:0` | What the re-embedding switches search and memory to ([Embeddings](#embeddings-titan)) |
| `EmbeddingDimensions` | 1024 | Titan's vector size: 1024, 512 or 256 |
| `Prices` | Anthropic's list prices | US dollars per million tokens, for the cost of an answer; Bedrock bills per AWS's own price page |

On the server they come from `.env`, written into `appsettings.Local.json` on each deploy like the NVIDIA key:
`AI_BEDROCK_ENABLED=true` and, to move it, `AI_BEDROCK_REGION`. Nothing else about AWS goes in `.env`.

### While Bedrock is on, every chain is Claude

Owner, 10 Oct 2026: every answer comes from Bedrock, the most capable model first, cost no issue.

| Tier | Chain | Effort |
| --- | --- | --- |
| Judge | Claude Fable 5.1 → Claude Opus 5.5 | high |
| Analyst | Claude Opus 5.5 → Claude Sonnet 5.5 | high |
| Extract | Claude Sonnet 5.5 → Claude Haiku 5.5 | low |

- A tier the owner never changed walks this chain (`AiTierDef.BedrockChain`).
- A chain the owner set, for a tier or an agent, keeps only its Bedrock models. One with none walks the tier's
  Bedrock chain.
- The stored chains are not touched, so switching Bedrock off brings the NVIDIA chains back as they were.
- While Bedrock is on, the Models and Agents tabs refuse an NVIDIA model in a chain.
- To give a tier other Claude models, set its chain to Bedrock ids on the Models tab, or
  `PUT tiers/judge { "chain": ["bedrock:anthropic.claude-opus-5-5", "bedrock:anthropic.claude-sonnet-5-5"] }`.
  An empty chain goes back to the default.

### What Claude is sent

- **Thinking.** Adaptive, summarized so the console can show it, steered by the tier's effort. Haiku 4.5 gets neither
  thinking nor effort. Fable 5.1 and Opus 5.5 cannot turn thinking off; the desk never asks them to.
- **No temperature.** The current models refuse sampling settings, so the 0.2 the AI Trader asks for is not sent.
- **Tools.** `tool_choice: auto`, or `none` once the tools close. A forced choice is refused by these models.
- **Prompt cache.** Explicit breakpoints on the agent's own system prompt and on the tool definitions, so an agent's
  repeated calls read them from the cache. The memories and the time after the prompt are not cached.
- **Tool loops are append-only.** An assistant turn goes back to the model that wrote it exactly as it came, its
  thinking blocks and their signatures unchanged. Thinking blocks are bound to their model, so another model, after a
  fallback, gets the turn's words and tool calls only.
- **The answer.** The finish reason is put in the desk's words: `end_turn` is `stop`, `max_tokens` is `length`,
  `tool_use` is `tool_calls`. The usage counts the cache reads and writes. The answer's cost at list price is
  `costUsd` on the stream's `done` event and on `POST ask`; it is null when NVIDIA answered.

### When Bedrock says no

| Bedrock says | The attempt | Then |
| --- | --- | --- |
| 429, throttling | `http 429: throttled by Bedrock` | Capacity: asked once more after `Ai:CapacityRetrySeconds`, then the next model |
| 503, model not ready, service unavailable, 529 overloaded | `http 503: …` | Capacity, as above |
| 403 or 404, or 401 "not available" | `http 403: Bedrock model access is not enabled for anthropic.claude-… in us-east-1` | The next model |
| 401 or 403 "not authorized to perform" (IAM) | `the server's AWS role is not allowed to call Bedrock (an IAM permission is missing)` | The next model |
| 401, or 403 for the signature | `Bedrock refused the server's AWS credentials` | The next model |
| A `refusal` stop | `refused: the model's safeguards declined (category: …)` | The next model. Bedrock has no server-side fallback, so this is it. A refusal does not cool the model. |

An AWS account number in the provider's words is masked. Neither credentials nor request bodies are logged. The open
models' refusals read the same way (`BedrockRefusals`, shared by both clients).

### Nothing calls Bedrock by itself

Every Bedrock call is billed, so:

- **The health probe never asks a model on Bedrock**, Claude or open. A cooled one waits out its cooling and is back in
  its place with its next real answer.
- **No test or CI job reaches AWS.** The tests stream from a scripted endpoint through Anthropic's own SDK client with
  signing switched off, and a scripted Titan (`AiBedrockTests`); the open models from a scripted OpenAI-compatible
  endpoint with a stand-in signer, and the real signer only with made-up credentials and nothing sent
  (`AiBedrockOpenTests`). The test settings switch the open models off unless a test is about them.
- **Off is off.** With `Enabled` false the SDK clients are never even made. The open models' signer looks for
  credentials only when a chain reaches one of them, never at start.

### Open models on Bedrock

The third provider (owner, 10 Oct 2026): in the three days to 10 Oct, 12% of calls failed on every NVIDIA model of
their chain, and answers took 37 s on average. These answered from the server in about 1 to 2 s on 10 Oct, through the
OpenAI-compatible endpoint in `us-east-1`. From 11 Oct each chat tier is four of them, from four makers, and NVIDIA is
not asked (owner: "leave NVIDIA, put everything on Bedrock"):

| Tier | Chain while Claude is off |
| --- | --- |
| Judge | `bedrock-oss:moonshotai.kimi-k2.5` → `bedrock-oss:zai.glm-5` → `bedrock-oss:deepseek.v3.2` → `bedrock-oss:qwen.qwen3-235b-a22b-2507` |
| Analyst | `bedrock-oss:deepseek.v3.2` → `bedrock-oss:moonshotai.kimi-k2.5` → `bedrock-oss:qwen.qwen3-235b-a22b-2507` → `bedrock-oss:zai.glm-5` |
| Extract | `bedrock-oss:openai.gpt-oss-120b` → `bedrock-oss:mistral.mistral-large-3-675b-instruct` → `bedrock-oss:deepseek.v3.2` → `bedrock-oss:nvidia.nemotron-super-3-120b` |

- The models a chain may name (`AiCatalog.BedrockOpenModels`): those six, `nvidia.nemotron-super-3-120b` and
  `minimax.minimax-m2.5`. Claude, OpenAI's GPT-5 and later models and Grok did not answer there for this account.
- `Ai:BedrockOpen:Enabled` (default `true`; `.env` `AI_BEDROCK_OPEN_ENABLED=false` turns it off): off, a `bedrock-oss:`
  model is skipped with its reason and the chains are NVIDIA's again. The region is `Ai:Bedrock:Region`.
- With Claude on, nothing changes: every chain is Claude, and the Models and Agents tabs refuse an open model too.
- An owner's chain may name `bedrock-oss:` models; it is walked as set.
- **Signing.** Each request's exact body is signed with SigV4 (service `bedrock-mantle`, the region above) by
  AWSSDK.Core's `AWSSigV4Signer`, with the default AWS credential chain: on the server, the EC2 instance profile. The
  credentials are asked for on every request, so a rotated role key is used at once. A machine with none fails the
  attempt as `unreachable (AWS: could not sign the request: …)` and the chain moves on.
- **Prices** (`Ai:BedrockOpen:Prices`): AWS's Standard on-demand prices in US East (N. Virginia), as its Bedrock
  pricing page gave them on 10 Oct, in US dollars per million tokens: Kimi K2.5 0.60 / 3.00, GLM-5 1.00 / 3.20,
  DeepSeek V3.2 0.62 / 1.85, gpt-oss-120b 0.15 / 0.60, Mistral Large 3 0.50 / 1.50, Nemotron 3 Super 0.15 / 0.65,
  MiniMax M2.5 0.30 / 1.20. The page gave no N. Virginia price for Qwen3 235B A22B 2507, so its cost is unknown, not
  guessed.

### Embeddings: Titan

Search and memory compare vectors, and two models' vectors cannot be compared, so one embedding model is active at a
time (`AiEmbedder`):

- It is NVIDIA's Nemotron Embed until the re-embedding has run. While Bedrock is off it is NVIDIA's again.
- **The re-embedding** (`POST embeddings/reembed`, admin, Bedrock on) embeds every memory and every doc passage with
  Titan Text Embeddings V2 on `bedrock-runtime` InvokeModel, one text a call, four at a time. It saves the vectors,
  their model and the switch (`system_settings` row `ai.embedding.model`) in one transaction, and only once all of them
  are made. A failure part-way changes nothing.
- It runs in the background and answers 202. It is one row on the Calls tab (agent `reembed`).
- Readers use only the active model's vectors. A passage or memory with another model's vector is embedded again when
  next needed: the doc indexer on its next run, a memory when it is next ranked.
- `GET embeddings` shows the active model, the target, and how many vectors each model holds.

### Not on Bedrock

Bedrock does not offer, so the desk does not use there: structured outputs; server-side tools (web search and fetch,
code execution); Message Batches; the Models API; server-side `fallbacks`; top-level automatic `cache_control` (the desk
sets explicit breakpoints). Tool use, thinking, prompt caching with explicit breakpoints and streaming work.

### What the owner does in AWS

1. **Model access.** In the Bedrock console, in `us-east-1`, enable access to Claude Fable 5.1, Opus 5.5, Sonnet 5.5
   and Haiku 5.5, and to Titan Text Embeddings V2.
2. **The instance role.** The role of the EC2 instance profile allows `bedrock-mantle:CreateInference` (Claude through
   the Messages API) and `bedrock:InvokeModel` on Titan Text Embeddings V2, and nothing else. No access key is made.
   The open models answered with the role as it was on 10 Oct: nothing more to enable for them.
3. **A budget alarm.** An AWS Budgets monthly cost budget with an e-mail alert, so a runaway loop is seen the same day.
4. Then `AI_BEDROCK_ENABLED=true` in the server's `.env` and a deploy. Test each Claude model once from the Models tab,
   then run the re-embedding.

## A call

`AiGateway` is the one door every call goes through. In order:

1. **The agent's switch.** A planned agent, or one switched off, is refused (409).
2. **The providers.** A model whose provider cannot be asked (no NVIDIA key; Bedrock off) is skipped. With nothing
   left in the chain the call is refused (503), with the reason: where the key goes, or that Bedrock is off.
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
| `get_strategy_history` | A strategy's, underlying's or account's period (this or last month, 7 or 30 days, from/to) in one call: totals, net by day, best and worst runs, closed-trade stats | `LiveRunHistoryBuilder` (paged), `paper_positions` |
| `get_run` | One run: settings, P&L, then a summary or one section (legs, orders, signals) in a time window | `PositionViewBuilder`, `RunPnl`, the run's rows |
| `get_open_positions` | Every open leg, marked, with its mark's age | `OpenPositionsBuilder` |
| `get_quotes` | Indices, large caps and commodities with the day's change; India VIX | `IMarketPulseService`, the NIFTY chain header |
| `get_option_chain_summary` | PCR, max pain, walls, ATM IV, OI totals for an underlying | `OptionChainService` (the header) |
| `get_incidents` | Sentinel's live or recent incidents | `incidents` |
| `get_latest_checkup` | The newest finished checkup, every item with what to do | `CheckupsController.ToDetail` |
| `get_forecasts` | A session's forecasts and scores | `ForecastsController.ToView` |
| `get_news` | Headlines and a stock's filings, with FinBERT sentiment | `MarketIntelligenceQueries` |
| `get_strategy_spec` | A strategy's written spec, or the list of strategies | `StrategyCatalogService`, `docs/strategies` |
| `search_docs` | The passages of the desk's docs closest to a question, with file and section | `AiDocIndex` (below) |

`get_run` starts with a summary on purpose. On 30 Sep one busy run had 344 legs, 688 orders and 9,636 signals. The
summary gives:

- counts;
- realized P&L by IST hour;
- the five best and worst legs;
- the open legs;
- the last signals.

A small run gets its full lists as well. For the rest, the model asks for a section and an `HH:mm` window.

Legs, orders and signals carry their times to the second, and rows in the same second come in the order they were
written. Each signal also carries `facts`: the top-level values of its metadata, such as spot, ATM strike, EMAs and OI
change, at most 16. Both came from the reviewer's false "deviated" verdicts of 1 Oct:
- To the minute, a Fulcrum roll's close and the next open looked simultaneous, and it saw two groups held at once.
- Without the spot and the strike, it called an ATM put 500 points out of the money.

### Limits

Each limit is an `Ai` setting.

| Limit | Default |
| --- | --- |
| Rounds in which tools are open | 4 |
| Tool calls per question | 8 |
| Characters of one tool's answer | 16,000 (past it the model is told to narrow down) |
| Time per tool | 30 s |

After the last tool round the tools close (`tool_choice: none`). If a model still asks for one, the question moves on
to the next model. The same call made twice in one question is read once. A model whose failure in an earlier round
cost a long wait is not asked again in the same question.

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
| `GET agents` | Every agent (built or planned) with its chain, switch, last call and today's use; whether the owner can talk with it (`chat`) and what it reads then (`chatTools`); the desk's rule-based parts |
| `PUT agents/{key}` | `{ enabled, chain, resetChain, reason }`: 409 for a planned agent |
| `PUT tiers/{tier}` | `{ chain, reason }`: an empty chain goes back to the default |
| `GET models?refresh=` | NVIDIA's catalog (cached 10 min) and the Claude models on Bedrock, in-use models first; each one's provider, whether it can be asked now, its list price (Bedrock), last test and today's attempts; local models (FinBERT) |
| `POST models/test` | `{ model }`: one tiny question to one model, logged as agent `model-test` |
| `GET embeddings` | The embedding model in use, the re-embedding's target, and the vectors each model holds |
| `POST embeddings/reembed` | Embeds every memory and doc passage with Titan, then switches to it; 202, Bedrock on only ([Embeddings](#embeddings-titan)) |
| `GET calls?agent=&outcome=&model=&take=&beforeId=` | The log, newest first, paged by id |
| `GET calls/{id}` | One call in full: system prompt, messages, attempts, reasoning, answer, usage, the request's parameters |
| `POST ask/stream`, `POST ask` | Ask: `{ messages, tier, system, maxTokens, temperature, conversationId, agent }` |

Since memory (1 Oct):
- A row of `GET calls` also carries `feedback` (1, -1 or null) and `memoryCount`.
- `GET calls/{id}` carries `memoryIds` and `feedback`.
- The stream's `done` event and `POST ask` carry `memoryIds`.

The memory endpoints are under [Memory](#memory).

## Scheduled agents (Phase 3)

The three Phase 3 agents **start switched off**: they call the provider by themselves, so the owner turns each one
on from AI → Agents. The Desk Assistant starts on, and so do the Phase 4 agents (owner, 10 Oct: every AI agent built
and on): the four desk analysts, the three checks around the AI Trader, and the three below, each of which reads only
what code measured, works outside the session, and makes a handful of calls a day or a week.

`AiAgentScheduler`, a hosted service, runs once a minute:
- It waits 90 s after a start before its first round.
- Each switched-on agent does one piece of its due work: one run, one batch, one incident.
- One agent failing is logged and the next still runs.
- A failed tick is caught, so the scheduler can never stop the API.
- Nothing runs without a key, or with `Ai:SchedulerEnabled` false (set it false on a second API, such as a local one).

Every output is a row of `ai_reports`, one per agent and subject, so an agent that runs again finds its work done:

| Status | Meaning |
| --- | --- |
| `ok` | Written, and its check passed |
| `invalid` | The model answered, but the answer failed its check. The text is kept. |
| `failed` | No model answered, or (a run review) it stopped on the desk's side: a tool or the database threw. Tried again after 15 minutes, at most `Ai:MaxReportAttempts` times (3). |

A good report is never overwritten by a later failure. Each report links to its model call.

A run review turned away for capacity is `failed` and waits the same 15 minutes, but it is **not counted** as one of
the run's tries: no model read the run. That covers a refusal by the desk's own rate limit (429), and a call in which
every model that failed refused for capacity (`AiGateway.IsCapacityRefusal`: HTTP 429, 502 or 503, or "overloaded",
"temporarily" or "capacity" in the reason). A failure of any other kind (a timeout, a 500, an answer not in the asked
shape) still counts. Until 1 Oct a busy hour could spend all three tries and leave the run unreviewed. The report's
error starts "Turned away for capacity, not counted as a try".

| Agent | Due | Input | Output |
| --- | --- | --- | --- |
| Trade Reviewer (judge) | A stopped run from the last two days, 10 minutes after it stopped and after `Ai:ReviewAfterIst` (15:45) on its day. So NSE runs after 15:45, MCX runs after MCX closes (23:30, or 23:55 from November to March). Manual books and alert runs are skipped. The market it is handed for the run's day is that exchange's session in the calendar, MCX's winter close and special sessions included. | The run's `get_run` summary and its spec, handed over up front. It can read more with `get_run`, `get_strategy_spec`, `get_quotes` and `get_option_chain_summary`. | Verdict (followed / deviated / unclear), what was kept and what was not, stale-quote fills, market context, one thing worth testing, a journal |
| News Analyst (extract) | Every `Ai:NewsEveryMinutes` (10). A batch of `Ai:NewsBatchSize` (8) items it has not read, filings first, from the last `Ai:NewsLookbackHours` (24) by publication time; never the 2020 backfills. | The items' text | One record per item: event, direction, symbols, numbers with quotes, confidence |
| Incident Explainer (analyst) | A live incident of medium or worse, first seen in the last day | The incident, its evidence masked. It can read with `get_incidents`, `get_latest_checkup`, `get_runs`, `get_open_positions` and `get_quotes`. | What happened, likely why, what to do, urgency |

A **deviated** verdict is asked a second time, separately, before it is stored. It stands only when the second
review also finds a deviation. Otherwise the report is **unclear**, says that the two reviews disagreed, and keeps the
first review's text. On 1 Oct the same model, given the same data, called run 308 followed and its twin, run 309,
deviated; a false deviation is the review that teaches the wrong lesson. Followed and unclear verdicts are asked once.

Each output is checked before it is stored as `ok`:
- **Reviews and explanations** must be the JSON asked for. `AiJson` accepts a fenced block or a sentence around it.
- **News records** must use the listed events and directions and a confidence from 0 to 1. Every number's quote must
  appear in the item's own text; case, spacing and typographic quotes are ignored. A number the model made up
  cannot pass. The share of `ok` among `ok` + `invalid` is the "JSON validity" the roadmap asks to keep above 98%;
  `GET reports/stats` gives it per day.
- **A news answer that is not one whole object** still counts record by record (`AiJson.ObjectsUnder`). One record
  the model broke is `invalid` on its own, and the rest of the batch is kept. Items after the point where an answer
  was cut off at the token limit are `failed`, so a later batch reads them again. Before 1 Oct this lost the whole
  batch: 48 of 203 items that day.

**The daily digest.** The AI sends one Telegram message a day to the system channel (`AiDailyDigest`,
`Ai:ReviewDigestToTelegram`), never one per review or per decision. It carries:
- the AI Trader's day, when it was on or looked that day (see [AI Trader](ai_trader.md#daily-digest));
- the run reviews written since the last digest: how many kept to their spec, and the ones that did not;
- one short section for each desk analyst that ran
  ([below](#where-their-reports-go)).
- a line each for the Phase 4 agents that wrote since the last digest, and only for those: the Data Curator's
  passes with their high findings, the circulars read with the ones that apply, the week's research note with its
  ideas. On a day with no NSE session such a line is reason enough to send one, so the Saturday triage and the
  Sunday note reach Telegram the same afternoon.

When it goes:
- After the NSE close and `Ai:ReviewAfterIst` (15:45), whichever is later, by which time the AI Trader's shadow book
  has been squared off.
- While the Trade Reviewer is on, it also waits for the reviews still owed, a failed one with tries left included, for
  at most two hours; past that it says how many are still owed. A review written after the digest, such as an MCX
  run's after the MCX close, goes in the next day's.
- On a day with no NSE session (the exchange calendar, not the weekday), only if the AI Trader looked that day or a
  Phase 4 agent wrote since the last digest; then after 15:45.
- A section with nothing to say is left out; with nothing at all, nothing is sent.

It is a scheduled job, not an agent: it asks no model and runs whichever agents are on. Before it goes, the time it
was sent (`ai.reviewer.lastDigestUtc`) and the day it was for (`ai.digest.lastDay`) are kept in `system_settings`, so
neither a restart nor the next minute sends a second one for that day. A day found with nothing to say once due is
kept too (`ai.digest.lastEmptyDay`); a look later that evening still goes that day.

**A missed day is sent late.** Until 1 Oct, if the API was down from the close until midnight, that day's digest was
never sent, and the next one covered only its own day. Now, on the next minute the scheduler runs (90 seconds after a
start):
- It looks back up to seven days for days that should have had a digest (an NSE trading day, or a day the AI Trader
  looked), after the last one sent or found empty.
- The latest of them is sent then, titled with its day and **"(sent late)"**, for example "AI day, Wed 30 Sep (sent
  late)". It carries that day's AI Trader section and the reviews written since the last digest. It does not wait for
  reviews still owed; it says how many.
- Older missed days are not sent: they are one line in it, "The digests for Mon 28 Sep–Tue 29 Sep were missed."
- It is kept like any digest, so it never goes twice. The day it is sent on still gets its own digest after its close.
  If the API comes back after that close, the late one goes first and that day's own the next minute.
- With nothing to say (no AI Trader look or position that day, no review written, no older day missed) nothing is
  sent, and the day is kept as empty. Reviews still owed are not on their own a reason to send one late: an MCX run
  stopped at the close is still owed after midnight.
- With no digest ever sent or found empty, there is nothing to catch up from, and nothing goes.

Endpoints, admin only:

| Endpoint | What |
| --- | --- |
| `GET reports?agent=&subjectType=&status=&date=&take=&beforeId=` | Reports, newest first |
| `GET reports/{id}` | One report: body and data |
| `GET reports/stats?days=` | Per agent and day: ok, invalid, failed, and the valid share |
| `POST agents/{key}/run` | `{ subjectId }` (a run or incident id, a replay's id for `ai-trader-reflect`, a filing's id for `fundamental-analyst`, an AI Trader order's id for `execution-reporter`, or none for the next due work; the technical, options and macro analysts take none and write about the minute they run). Runs in the background and answers 202; the report appears when the model has answered. |

## The desk analysts (Phase 4)

Four advisory agents built on 10 Oct (owner: every AI agent built and on). Each reads facts the **code** computed from
the desk's own records, answers one JSON object, and code checks the answer before it counts. None places, changes or
cancels anything, and nothing trades on them; the AI Trader's brief reads three of them.

- **On by default.** Unlike the Phase 3 agents, they start switched on. The switch on AI → Agents still turns each off.
- **Model.** The Analyst tier (Kimi K3 → Nemotron 3 Super → GLM-5.3). Temperature 0.2, at most 4,000 answer tokens; the
  Fundamental Analyst 0 and 6,000.
- **Trading days** are the NSE calendar's (`IMarketSessionService`), not the weekday: a holiday or a weekend has no
  slot.
- **Memory.** All four are in `Ai:MemoryAgents`, so a 👎 with a correction, or a `/remember` note, in their chat
  becomes their memory and is read in their scheduled work too.

### Slots

The Technical, Options/OI and Macro Analysts write at fixed IST times (`AnalystSchedule`, `SlotAnalystAgent`). Each
time is a **slot**: one report (subject type `check`, subject id the day and time, `2026-10-12 08:20`), written once.

- A slot is written while its **window** lasts (the table below). One missed through its whole window, the API down
  say, is not written late: its facts would be of another moment.
- A failed try (no model answered, or the facts could not be built) is tried again after 15 minutes while the window
  lasts, at most `Ai:MaxReportAttempts` (3) times. A refusal for capacity waits the same 15 minutes and is not counted.
- With nothing to read at all (no bars, no chain, no factor recorded), no model is asked: the slot is a failed try.
- An answer that is not the JSON asked for, or fails its check, is `invalid`, with the reason and the model's text.
- **Run now** on AI → Agents writes a report for the current minute, whatever the schedule says.
- The facts handed over are stored with the report (`facts` in its data), so a report shows what every number was
  checked against.

**The numbers check** (`FactNumbers`). Every number the answer states (in a reason, a line, a summary) must be one
the code handed over:
- the same value, or that value rounded to one place fewer (PCR 0.92 may be written 0.9, not 1);
- a price of 100 or more may be rounded or cut to fewer places, down to a whole number (25,310.5 as 25,310 or 25,311,
  never 25,312);
- never with more places than were given, and the sign is not read ("fell 0.13%");
- what is not a stated number is taken out first: times (09:30), dates (7 Oct), indicator periods (EMA 20, ATR(14),
  5-minute), index names with digits (NIFTY 50, S&P 500), ATM+1, ordinals, and a number written straight after
  letters (EMA20, Q2, FY27, m3).

| Analyst | Slots (IST) | Each written within |
|---|---|---|
| Technical | 08:20; and 09:30, 10:00 … 15:00 while an index option is open | 08:20 until the 09:15 open; each half hour within its half hour, inside the session |
| Options/OI | 08:30, 10:30, 13:00, 14:30 | 08:30 until 09:15; 10:30 until 13:00; 13:00 until 14:30; 14:30 until 15:30 |
| Macro | 08:45 | until the 09:15 open |

### Technical Analyst

- **Reads** (`TechnicalAnalystAgent`): for NIFTY, BANKNIFTY and SENSEX, the recorded 1-minute bars up to the moment
  (`ILiveDataService.GetBarsUntilAsync`, 2,500 minutes), the regular session only (`SituationMath.Session`). From
  them, in code: the last price and its bar, the previous close and the change, the session's open, high, low and
  range, the previous session's high, low and close, EMA 20 and EMA 50 and ATR(14) on 5-minute bars (the brief's own
  arithmetic), the last half hour in the session. VWAP is not available: an index trades no volume. **Levels**: the
  session's and the previous session's highs and lows, the previous close (before the open: the last close), the two
  EMAs, and up to three swing highs above the price and three swing lows below it, found on 15-minute bars
  (`MarketStructure`, fractal, two bars each side). One level per price, highest first, each with its distance from
  the price. Before the open the facts are the last session's and say so. An index with no bars is marked not
  available and is not graded.
- **When**: 08:20 IST on trading days; and on the half hour from 09:30 to 15:00, inside the session's hours (the
  calendar's, a half day included), only while an open NIFTY, BANKNIFTY or SENSEX option is held: in the AI Trader's
  shadow book that day, or as an open leg of a live strategy run (running or stopping; recaps left out), its own
  account's included.
- **Writes**: for each index, a bias (bullish, bearish, neutral), a grade A to D (A a clean trend with room, D
  conflicting or broken: how readable the setup is, not a forecast), 1 to 4 key levels and a one-line reason; a
  summary. Title: `Technical 08:20: NIFTY bullish B · BANKNIFTY neutral C · SENSEX bullish B`.
- **Checked**: every index with facts graded once, none without; a listed bias and grade; each key level one of that
  index's levels (by the numbers rule); no number in a reason or the summary that the facts do not hold.

### Options/OI Analyst

- **Reads** (`OptionsAnalystAgent`): each index's nearest-expiry chain as recorded, as of the slot
  (`OptionChainService.GetViewAsync` on the captures only, and `GetTrendAsync` for the session): the capture's time
  (at 08:30 the last session's, and the facts say so), expiry and days to it, the spot and ATM strike, PCR and the
  PCR of the day's OI change, max pain and the spot's distance from it, the two heaviest call strikes and the two
  heaviest put strikes with their OI in lakh and their distance from the spot, total OI each side and its change on
  the day, ATM IV; and against the session's first capture, the PCR then and its change, the call and put OI change,
  ATM IV then and its change. All computed in code, so the model never has to.
- **When**: 08:30, 10:30, 13:00 and 14:30 IST on trading days.
- **Writes**: for each index with a chain, a stance (bullish, bearish, neutral, range-bound) and 1 to 4 short lines:
  where the walls sit against the spot, what OI and PCR show, what IV says about option prices; a summary.
- **Checked**: each index with a chain read once, none without; a listed stance; every number in an index's lines one
  of **that index's** facts; the summary's numbers any index's.

### Macro Analyst

- **Reads** (`MacroAnalystAgent`): the Market factors page's data as of the slot, each fact a line written by code
  with an id (`m1`, `m2` …): NIFTY's last close and its move; GIFT Nifty's latest morning snapshot (NSE IX), its move
  and its distance from NIFTY's last close (a future: part of that is its premium); this morning's snapshots of the
  S&P 500 futures, the Nikkei, the Hang Seng and the KOSPI; the last closes of the S&P 500, Nasdaq 100, Dow, CBOE VIX,
  Nikkei, Hang Seng, KOSPI, Euro Stoxx 50, FTSE 100, Brent, gold, the dollar index, the US 10-year yield and USD/INR
  (only bars dated before today, which have all closed by 08:45); the latest FII/FPI and DII cash flows and FII's last
  five recorded sessions; India VIX's last close and its move; today's events, holidays and expiries
  (`MarketFactorsQueries.EventsAsync`). A snapshot taken after the slot is not known at it.
- **When**: 08:45 IST on trading days.
- **Writes**: a bias band (bearish, lean bearish, neutral, lean bullish, bullish), a position-size modifier, the facts
  it rests on by id, a reason and what would change the view. Title: `Macro 08:45: lean bullish, size ×0.80`.
- **Checked**: a listed band; a size modifier, which code **clamps to 0.5–1.0** whatever the model asked (the report
  keeps both and says it was clamped); one or more facts cited, each an id that was handed over; no number in the
  reason or the risks that the facts do not state (its own size modifier may be said in words).

### Fundamental Analyst

- **Reads** (`FundamentalAnalystAgent`): the corporate filings recorder's rows (`corporate_announcements`) of the last
  `Ai:NewsLookbackHours` (24), never the backfills. A filing is read only when code finds both:
  - it is a **results filing**: its subject says "financial result", or the scorer tagged it `results`, or a board
  meeting's outcome (or an integrated filing) says it approved financial results; never a trading-window notice, a
  board meeting intimation or other paperwork that only mentions results;
  - its stock is in the desk's **F&O universe**: a stock future still listed in the instrument master, or in the F&O
  bhavcopy of the last ten days; indices are not stocks.

  Every other filing is skipped and gets no report. The model reads the subject and the first 3,000 characters of the
  details.
- **When**: every `Ai:FundamentalEveryMinutes` (10), at most `Ai:FundamentalBatchSize` (3) filings a call, newest
  first. One report per filing (subject type `filing`, subject id the filing's id), like the News Analyst's.
- **Writes**: whether it is the results themselves; the period; each metric (revenue, net profit, EBITDA, EBITDA
  margin, net margin, EPS, net interest income, gross NPA, other) with its value, unit and the exact words it came
  from, the year-earlier figure with its own words, and the year-on-year change; the surprise (positive, negative,
  inline, unclear), judged from the stated changes alone, since no estimates are known; a summary.
- **Checked**: each quote is in the filing's text (case, spacing and typographic quotes ignored, as the News Analyst's)
  and the metric's value is a number written in it; a year-earlier figure the same; a year-on-year change written in
  the filing, or within 0.15 points of the change code computes from the two figures (the report keeps both); a
  surprise other than unclear needs at least one change behind it; no number in the summary that the filing and those
  changes do not hold. "Not the results themselves" is a valid answer. A cut-off or non-JSON answer leaves its
  filings to be read again.

### Where their reports go

- **AI → Reports**: every report, with the agent filter listing each analyst, its data laid out (the grades and
  levels, the lines, the bias and size with the facts cited, the metrics with their quotes) and the body.
- **The AI Trader's brief**: a `DESK ANALYSTS` section, after the news, with the latest `ok` report of the day of the
  Macro, Technical and Options/OI Analysts written at or before the brief's moment (`DeskAnalysts.BriefSectionAsync`).
  In a replay that moment is the replay's clock: a report written after the replayed moment never reaches it, and a
  day with none says "No analyst report was written for 7 Oct by 10:15 IST, the replay's clock." Live, with none yet:
  "None written yet today."
- **The daily digest**: one short section for each slot analyst that wrote anything about the digest's day (its
  reports, how many were ok, invalid or failed, and its latest view), and for the Fundamental Analyst the results
  filings read since the last digest (results come out after the close), with the positive and negative surprises.
- **Today**: each analyst in the AI section, on or off, with the day's report counts and its latest titles.
- **Their chats** (AI → Assistant, or `/technical`, `/options`, `/macro`, `/fundamental` on Telegram): see
  [Talking to the agents](#talking-to-the-agents).

## The checks around the AI Trader

Built 10 Oct, when the AI Trader began placing paper orders in its own account (owner: every AI part on). All three
**start switched on**: they call a model only when the AI Trader places something. Their records are `ai_reports` rows
like the scheduled agents', and each has memory (`Ai:MemoryAgents`) and a chat persona. The whole path is in
[AI Trader](ai_trader.md#how-a-plan-becomes-an-order).

| Agent | When | Reads | Writes | Its say |
| --- | --- | --- | --- | --- |
| Validator / Skeptic (`validator`, judge) | Before each AI Trader buy or strategy start the rules allowed, in live mode; asked three times, sampled (temperature 0.7) | The plan, the contract and its price, the rules' verdict, the market brief the plan rests on | ACCEPT or REJECT, the flaw, the brief's own words that show it, a confidence; one report per decision (subject `decision`) | Two REJECTs block the placement. No majority blocks it too: the desk **fails closed** (a provider down, answers not the JSON asked for, a REJECT whose quote is not in the brief, the agent switched off) |
| Risk Memo (`risk-memo`, judge) | After the Validator accepts, once | The plan, its price, the rules' verdict, what it loses at its stop, the owner's limits, the account's exposure | A memo, and fewer lots, a higher stop or a lower target; one report per decision | Code applies only a change that is tighter and still valid, and ignores anything looser. It never blocks: with no memo the plan goes on unchanged |
| Execution Reporter (`execution-reporter`, extract) | A scheduled agent: one AI Trader fill a minute, entry or exit, from the last two days | The fill, how it was priced, the position, the decision behind it | A note and a flag (clean, slipped, slow, check) beside code's numbers: slippage in rupees and per cent against the price the decision saw (the ask, its exit's bid, the stop or target), the time to fill, the stop and target as placed; one report per fill (subject `order`) | None: it reads fills after they happen. A note with a number not among the facts is `invalid`, code's numbers kept |

The Validator and the Risk Memo are asked by the AI Trader itself, outside its book gate, not by the scheduler, so
they have no "Run now"; the Execution Reporter has one (`POST agents/execution-reporter/run`, `{ subjectId }` an AI
Trader order's id, or none for the next fill due). Other users' fills are never reported. On Reports a decision's
records link to the AI Trader's panel and a fill's to its manual book.

## The Phase 4 agents: words over what code measured

Three agents whose input is a table code computed and whose answer is held to it. The model never reads the
database; it triages, judges or words what it is handed, and plain code checks every id, quote and number it writes
(`AiFactCheck`) before the report counts as `ok`:
- **Quotes** must be in the source's text, case, spacing and typographic quotes and dashes aside, as for the News
  Analyst.
- **Numbers, times and dates** the model writes must be in what it was handed (the system prompt and the question):
  the same value, its sign, rupee mark, Indian or Western grouping and a per cent sign aside, or a value that rounds
  to it at the decimals written ("75%" for 75.2%, "₹1,18,028" for −118028). A year cited from a date in the tables
  counts ("2020-23" from "2020-09-01"). The integers 0 to 10 always pass: they count the answer's own parts. A number
  the model computed or made up fails the report, which is kept as `invalid` with the model's text.

All three are on from the start, work outside the session (`AiAgentHours.InSession`: never 09:00–15:45 IST on an NSE
trading day by the exchange calendar, so a weekday holiday is free and a weekend special session is not), keep
`ai_reports` with subject type `check` and a subject of their own, have memory (`Ai:MemoryAgents`), a chat persona
with chat tools, and a Telegram command. A turn-away for capacity is not counted as a try, as for the Trade Reviewer.
`POST agents/{key}/run` runs each now (a day or a week for the curator and the researcher, a circular's id for the
triage).

### Data Curator

Finds broken inputs before anything relies on them (`DataCuratorAgent`, `DataCuratorChecks`, extract tier).

**When.**

| Pass | Subject | Due |
| --- | --- | --- |
| Night | `night:yyyy-MM-dd`, the trading day checked | 20 minutes after that day's archive is due (`ArchiveSchedule.DueAtIst`: 23:50 IST, or 20 minutes after MCX's close when later, so 00:10 or 00:35), once the archive's marker (`archive.candles.lastDay`) has reached the day; the India VIX check runs in the archive's tick, so its gaps are in. If the archive has not finished two hours later, the pass runs anyway and the lag is a finding. A night pass missed (the API down) is made at the next chance outside the session. |
| Morning | `morning:yyyy-MM-dd`, today | From `Ai:DataCuratorMorningIst` (08:00) to 09:00 on NSE trading days only; it checks the trading day before and today's expiries. Missed, it is skipped: it exists for before the open. |

**What code checks** (each query names its symbol, or underlying and expiry, and one day, on the indexes those tables
have for exactly these reads):

| Kind | Check | Finding when |
| --- | --- | --- |
| `archive` | The archive's marker | The day checked is not archived |
| `vix` | `archive.vix.reportedGaps`, kept by the nightly VIX check | Any day the vendors could not fill |
| `candles` | NIFTY 50, NIFTY BANK, SENSEX and India VIX candles at 1 and 5 minutes against the day's session, by the backfills' coverage rule (`VixBackfillPlan.Classify`: 90%, a bar in the first five minutes and the last fifteen) | A gap, or holes of 5 bars or more at 1 minute (2 at 5); a VIX day already reported is one finding |
| `replay` | The 1-minute `live_bars` of the indices a replay plays (`MarketReplayService.CoverageSymbols`, as the Replay days list counts them) | 5 or more session minutes missing, with the longest gaps |
| `chain` | The option chain's captures minute by minute (`Ai:DataCuratorChainUnderlyings`: NIFTY, BANKNIFTY, SENSEX), for the master's nearest expiry the poller captured most | 5 or more minutes missing or a gap of 5 minutes, listed; none at all while the day before had some. An underlying captured on neither day is not followed by the poller and is left out |
| `feed` | Feed heartbeats (`ingestor_statuses`) of the last 7 days | No heartbeat in the session; a feed that went silent mid-session and never came back; a feed in error in the last day |
| `quotes` | Recorded indices, stocks and futures (options left out: a far strike may not trade all day) | No quote in the session |
| `watchlist` | Recorded contracts against the instrument master | Expired and still recorded |
| `expiry` | NIFTY, BANKNIFTY and SENSEX's next option expiry in the master, for today | None in 60 days; one on a day the exchange calendar closes; one the expiry rule (`expiry_rules`, weekly or last weekday of the month, moved back over a holiday) does not expect |

Each finding has a stable id (`candles:NIFTY:1m:2026-09-30`), its facts in words with their numbers, what reads that
data (backtests on NIFTY, the 08:50 forecasts, ChainFlowBuy's historical chain, the AI Trader's brief…), and the fixes
allowed for it, from a fixed list (`DataCuratorChecks.Actions`: archive again, run the VIX check, backfill from the
broker, import Dhan's index history, restart the feed, check the chain poller, refresh the instrument masters, fix the
expiry rule, check the holiday calendar, prune the recording list, nothing).

**What the model does.** For each finding: a severity (high, medium, low), what breaks if it is ignored, naming the
finding's own readers, and one fix from the finding's own list. The answer fails its check when it names an id the
checks did not record, leaves one out, picks a fix not on the finding's list, or writes a number not in the findings.
A pass that finds nothing asks no model and is written as it is ("nothing wrong").

**Where it shows.** The Reports tab (findings, severity, what breaks, what to do, and the list of what was checked);
Today's **Needs a look**: the high findings of the latest good pass of the last 36 hours (the morning pass supersedes
the night's); a line in the next daily digest.

### Compliance Triage

Reads the week's circulars (`ComplianceTriageAgent`, extract tier), recorded once a day by the circulars recorder
([Market intelligence](market_intelligence.md#sebi-nse-and-bse-circulars)).

- **When.** From `Ai:ComplianceDay` (Saturday) at `Ai:ComplianceAfterIst` (10:00 IST), every circular recorded before
  then and within `Ai:ComplianceLookbackDays` (21) that has no settled report, `Ai:ComplianceBatchSize` (4) a
  scheduler minute. A circular recorded during the week waits for the next Saturday; a Saturday the API missed is
  caught up at the next chance outside the session.
- **What it reads.** Each circular's source, number, date, department, title and up to 2,500 characters of the text
  read from its document; a circular whose document gave no text is handed over with a note to judge on its title.
- **What it answers**, per circular (subject `circular:<id>`): applies to this desk (yes, no, unclear), the area
  (retail algo, F&O contracts, risk and margins, market hours, data and APIs, other), why, what would have to change,
  and the exact words it rests on. The prompt says what the desk is: paper orders that never reach an exchange, its
  own algorithms on its own server, broker data APIs, no clients, not a broker or an algo provider.
- **The check.** The verdict and area from the lists, a change named when it applies, and the quote found in the text
  as handed over (the title, the number and the excerpt). A record left out of the answer is invalid; a batch cut off
  at the token limit is failed and read again.
- **Where it shows.** The Reports tab, linked to the circular's page or document; Today's Needs a look lists those
  that apply, from the last 7 days; the digest's line counts them and names the ones that apply.

### Strategy Researcher

Reasons over backtest output, never a live change (`StrategyResearcherAgent`, `StrategyResearchData`, judge tier).

- **When.** On `Ai:ResearchDay` (Sunday) from `Ai:ResearchAfterIst` (11:00 IST), one note a week (subject
  `week:yyyy-MM-dd`, the Sunday). A Sunday the API missed is written at the next chance that week, never inside a
  session, so a Sunday special session waits too.
- **What code computes.** The week's finished backtests (`OfflineReplay` runs created in the 7 days before the cutoff,
  completed or stopped, at most 300): strategy, underlying, window, which periods it covers (holdout 2020-09-01 to
  2023-12-31, design 2024–2025, recent 2026 on), closed trades, net as the Backtests list shows it, per trade, win
  rate, tunable settings (the strategy's own parameters and each rule set in its risk, filters, limits, exits and
  contract blocks; lots, underlying and the cost model are not tunables) and a hash of them per configuration. Each
  strategy's paper runs of the last 30 and 7 days from the Trade → History builder, recaps and alert runs left out,
  net after charges. Each strategy's spec, its Idea and Limitations sections. And per strategy the overfitting checks:

  | Check | Concern when |
  | --- | --- |
  | `sample` | Fewer than 30 closed trades in the week's backtests |
  | `tunables` | Fewer than 10 trades per tunable setting |
  | `holdout` | None of its backtests touched 2020-09 to 2023-12 (the rule from [SmcStructureBreak](../strategies/SmcStructureBreak.md): a 2024–26 result is not believed until it is run, unchanged, there); or the holdout lost |
  | `in-vs-out` | Per trade on a later or held-out window under half the design years', or below zero |
  | `paper-vs-backtest` | Paper per trade after charges under half the backtests', or below zero (unknown under 20 paper trades) |
  | `configs` | 5 or more configurations tried in the week |

- **What the model writes.** Per strategy, a verdict (concern, fine, unknown) and a sentence on each of its checks, and
  at most 3 ideas, each a backtest to run (period, underlying, settings) with the numbers that suggest it.
- **The check.** Strategies and check ids from the tables, at most three ideas, none a live change ("go live",
  "deploy" and their like fail it), and every number in the tables. A week with no backtest and no paper run asks no
  model.
- **Where it shows.** The Reports tab, and the latest note's ideas above the list there; the digest's line on the day
  it is written.

## Docs search

`AiDocIndexer` keeps `ai_doc_chunks` in step with the repo's `docs/`: the public module docs and strategy specs.

- **When.** At start and every six hours.
- **Passages.** Each file is cut into passages at its sections, each under 1,500 characters. A passage carries its
  heading trail ("AI workspace › Desk tools"); HTML comments are left out.
- **What is embedded.** Only passages that are new or changed, with `nvidia/nemotron-3-embed-1b` as passages. A
  deleted doc's passages are removed.
- **Audit.** A run that did something is one row on the Calls tab (agent `doc-index`, source `index`).
- **Scale.** On 30 Sep: 1,297 passages from 56 docs, embedded in 45 s.

`AiDocIndex` holds the vectors in memory (about 16 MB) and ranks every passage by cosine against the question, which
is embedded as a query. `search_docs` gives the Assistant the closest passages; `GET search?q=` shows the same, with
what the index holds.

## The Assistant check

After 16:40 IST on NSE trading days (`Ai:AssistantCheckAfterIst`; exchange holidays are skipped), `AssistantCheckAgent` reads the desk through the
Assistant's own tools and turns what it finds into up to a dozen questions with known answers:

- the day's run count;
- the worst run's id, net and charges;
- the total net;
- open legs;
- live incidents;
- the latest checkup's verdict;
- NIFTY's PCR and max pain;
- the forecasts issued;
- one sum.

A question whose answer the desk does not have that day is left out.

Each question goes to the Assistant in its real configuration (source `check`). Plain code grades it:

- Numbers are read the way the desk writes money: ₹1,10,132.75, −110,132.75, 1.1 lakh, 59.8k.
- Citations, times and dates are removed first. Without that, a count of 10 would match a citation's "20:10".
- The answer is read from the tools again after the model answers. An MCX run still trading after the NSE close
  moves the day's money while the model reads it, so a figure anywhere between the two readings passes, and a
  worst run that changed passes as either id. The report shows both readings as "before → after".

The day's result is one report (agent `assistant-check`, subject `check`), which passes at 80%. A model or prompt
change that makes the Assistant misread the desk shows up the same evening. It runs while the Assistant is on and
`Ai:AssistantCheckEnabled` is true. `POST agents/assistant-check/run` runs it now.

## The Assistant exam

The check asks about today, so its answers move and its questions change daily: one day's score cannot be set
against another's, and a dozen questions cannot tell an improvement from luck. `AssistantExamAgent` is the measure
that compares week to week.

**The bank** (`ai_exam_questions`) holds questions about finished NSE trading days. The answers come from `get_runs`
for that day, the tool the Assistant uses, and are frozen once written. Each day gives about 15:

- its run count, net after charges and winners;
- the lowest and highest run by id (left out on a tie, or when the list is capped);
- each account's net, when there is more than one account;
- for the worst, middle and best run: its net, charges and closed trades.

Six fixed arithmetic questions join them, such as option P&L with a stated lot size, and net from gross and
charges. One day in four is **held out**, chosen by a hash of the date, so the choice never changes. Nothing is
learnt from the exam, so held-out days stay unseen by lessons, memories and examples.

**A sitting** (`ai_exams`, `ai_exam_answers`):
- It starts on `Ai:ExamDay` (Sunday) after `Ai:ExamAfterIst` (10:30), at most once a day, and never asks while NSE
  trades.
- New finished days in `Ai:ExamLookbackDays` (45) are added to the bank first.
- It takes up to `Ai:ExamMaxQuestions` (150): the questions with the lowest hash of their text, a stable sample. It
  asks each `Ai:ExamRepeats` (3) times: every question once, then every question again.
- It asks `Ai:ExamAsksPerTick` (2) a scheduler minute. At 2, the exam stays at 20 asks in ten minutes, under
  `Ai:PerUserPer10Min` (30), which counts the exam as one user; at 3 it would sit on the cap. The other agents keep
  their turn.
- Every ask is a row, so a restart carries on where it stopped.

Before each ask the day's answer is read again. A question whose answer reads differently now is set aside
(`moved`) and listed, not failed; recap runs leaving a day's totals did that on 1 Oct. A provider that gives no
answer (`no-answer`) counts neither way. Grading is the check's, after the day the question names ("1 Oct 2026") is
taken out of the answer, since a count could match its "1".

**The score** is one report (agent `assistant-exam`, subject `exam`, subject id the exam's id):
- pass@1 over all answered asks;
- pass^k over the questions answered right every time;
- each for practice and held-out separately, and per question type;
- the questions answered wrong at least once.

Today's learning section shows the latest one. `POST agents/assistant-exam/run` starts an exam, or carries on the
open one, and the scheduler asks the rest. It runs while the Assistant is on and `Ai:ExamEnabled` is true. About 450
asks, a few hours on the free tier.

## The Assistant on Telegram

The desk's bot answers its linked owner in a private chat, as the Assistant tab does (`TelegramAssistant`).

- **Linking.** The console hands the admin a six-digit code (`POST telegram/pair`), valid ten minutes and used once.
  Sent to the bot as `/pair CODE`, it links that Telegram account to that console user, kept in `system_settings`.
- **Everyone else.** Strangers get "This bot is private." to `/start` and nothing else. Group chats are ignored: the
  bot posts the desk's notices there.
- **Questions.** Each goes through the gateway as the Desk Assistant (source `telegram`), with the chat's last turns
  for half an hour. `/new` starts over and `/unlink` leaves. `/trader`, `/reviewer`, `/news`, `/incident`,
  `/technical`, `/options`, `/macro`, `/fundamental`, `/data`, `/compliance` or `/research` before a question asks
  that agent instead ([Talking to the agents](#talking-to-the-agents)).
- **Answers.** The bot shows "typing…" while the model works. It answers in Telegram HTML, with tables and code in
  monospace and only `& < >` escaped. If Telegram refuses the HTML it sends plain text. The model, the seconds and
  the call number go under each answer.
- **Polling.** Updates are read by long polling, with the offset kept in settings, so a restart does not answer
  twice. Only one program may read a bot's updates; nothing else on the desk reads this one.
- **Settings.** `Ai:TelegramAssistantEnabled` turns it off. `GET telegram` shows whether it runs, the bot's name and
  the linked accounts; `DELETE telegram/owners/{id}` unlinks one.
- **Teaching.** 👍 and 👎 sit under every answer. After a 👎 the bot asks "What should it have said?", and a reply to
  that message becomes a correction of the agent that answered. `/remember …` saves a note for the agent the chat is
  talking with, `/memory` lists what it reads, and `/forget N` retires memory MN. After the daily check, one message lists the lessons it tested, those learned and
  those dropped, with nothing to answer. A button pressed by anyone but a linked owner does nothing.

## Talking to the agents

The owner can question every built agent, not only the Desk Assistant (owner, 3 Oct): to test it, and to correct it.
Asked with their task prompts, the scheduled agents answered in JSON or not at all: each prompt demands one JSON
object, and most give no tools.

**As itself.** A question from the console or Telegram (source `console` or `telegram`, `AiCatalog.ChatSources`) to an
agent with a chat persona (`AiAgentDef.ChatPrompt`) gets that persona and its chat tools (`ChatTools`) instead. The
persona says:
- who it is and what its scheduled job is;
- what it can read in a chat, tool by tool;
- to explain its own past work from those records, never invent a decision, a number or a reason, and say plainly when
  the records do not show something;
- that it cannot save anything: a correction is kept as its memory with 👎 and the right answer under its reply, or
  as a note with `/remember`.

Its scheduled work (sources `schedule`, `check`, `preview`) keeps its task prompt and tools, and so does a script's
`POST ask` (source `api`). The Desk Assistant has no separate persona: its prompt already is one. A switched-off agent
cannot be asked (409), as the Desk Assistant cannot.

| Agent | Reads in a chat |
|---|---|
| AI Trader | `get_ai_trader_decisions`: a day's looks (that day's replays too, each marked), a replay's, or the latest; what it proposed, its reason, the verdict and the rule; the day's totals. `get_ai_trader_decision`: one look with the brief it read, its plan and result, the memories its call was given (with their status now) and the shadow position it opened. `get_ai_trader_book`, `get_ai_trader_scoreboard` and `get_ai_trader_lessons`, through the same builders as its panel; the lessons with what its next look reads (`readNow`). `ai_trader_look_now` (below). |
| Trade Reviewer | `get_trade_reviews`: a day's, a run's or the last days' reviews, by verdict, with the run's strategy and account, and the totals by verdict. `get_trade_review`: one in full (journal, deviations, stale fills, the lesson, a second review's verdict). And the tools its reviews use: `get_runs`, `get_run`, `get_strategy_spec`, `get_quotes`, `get_option_chain_summary`. |
| News Analyst | `get_news_events`: its records by the items' publication time (default the last 24 hours), by direction, event or symbol, with the items in the window it has not read yet counted. `get_news_event`: one record with the item and the exact text it read (`NewsAnalystAgent.HeadlineText`, `FilingText`). `get_news`: the items themselves. |
| Incident Explainer | `get_incidents`; `get_incident`: one incident with every evidence line, masked, and its explanation; `get_incident_explanations`: its explanations of the last days beside their incidents. And `get_latest_checkup`, `get_runs`, `get_open_positions`, `get_quotes`. |
| Technical Analyst | `get_analyst_reports`: its reports of a day (default today) or the last days, by status, each with what it concluded (bias, grade, key levels per index) and the day's totals; `analyst` defaults to the analyst asking. `get_analyst_report`: one report in full, with the facts the code handed it. And `get_quotes`, `get_open_positions`. |
| Options/OI Analyst | `get_analyst_reports`, `get_analyst_report` (each index's stance and lines; the chain facts it was handed). And `get_option_chain_summary`, `get_quotes`: these answer for now, not for a past report. |
| Macro Analyst | `get_analyst_reports`, `get_analyst_report` (the band, the clamped size, the facts it rested on and every fact it was handed). And `get_quotes`, `get_forecasts`, `get_news`. |
| Fundamental Analyst | `get_analyst_reports` (a stock's with `symbol`), `get_analyst_report` (the metrics with their quotes, and the filing's text exactly as handed over). And `get_news`. |
| Validator, Risk Memo, Execution Reporter | `get_ai_trader_checks`: the three's records for an IST day, or on one decision (its verdict, its memo and the notes on the fills it made), by agent, with each record's structured part. And `get_ai_trader_decisions`, `get_ai_trader_decision`: the decisions they were about, with what was placed. |
| Data Curator | `get_data_checks`: its passes of the last days with their high findings, or one in full (every finding's facts, readers, severity, impact and fix, and what was checked). And `get_incidents`, `get_latest_checkup`, `search_docs`. |
| Compliance Triage | `get_circulars`: the recorded circulars by source and by its verdict (yes, no, unclear, untriaged), or one with the text read from its document, its quote and what would change. And `search_docs`. |
| Strategy Researcher | `get_research_notes`: its weekly notes with their ideas, or one week with the backtests, paper results and checks it read. And `get_strategy_history`, `get_strategy_spec`, `search_docs`. |

Every chat tool only reads and is projected field by field, its free text masked like the desk tools'
(`AiAgentChatToolTests`). A call runs only the tools it offered: a model that names another agent's tool is told the
ones it has.

**What the AI Trader would do now** (`ai_trader_look_now`, `AiTraderAgent.PreviewAsync`):

| Step | Rule |
|---|---|
| Built | As a live look: the brief as of now (`MarketBriefBuilder`), the stop floors, its book (its account in live mode, else today's shadow book) and its last looks of the day |
| Asked | Its own decision prompt once, with the memories a look now reads (`AiTraderMemory`: lessons from earlier days, notes and corrections written before), not counted as used |
| Judged | `AiTraderGuard`, on the book as it is once the answer is in |
| Saves | Nothing: no `ai_trader_decisions` row, no shadow position, nothing placed. Its scoreboard, its lessons' tests and its next look never see it. The model call is a row on the Calls tab (agent `ai-trader`, source `preview`) |
| Who pays | The asker: the call is made in his name, so his rate limits count it (two in flight, 30 in ten minutes), never the AI Trader's own looks |
| Never | While a market replay it decides in is playing: it says so, and no model is asked |
| Time | Up to 3 minutes, the tool's own limit (`IAiTool.Timeout`); the other tools keep 30 s |
| Answers | Each section of the brief shortened to its first lines, the plan and its reason, the contract, the verdict and the rule, the memories it read |

**Memory in a chat.** Each agent reads its own memories, as any call does, and a correction or a `/remember` note in
its chat becomes its memory (owner, 1 Oct: "a correction on any agent's work becomes its memory"), read in its
scheduled work too. The AI Trader's chat is given what its next look reads: a lesson only once the day it was learned
from is over (`AiMemoryBook`), never one reflected from today's own session. A past decision it explains read only the
memories its record lists.

**On the console** (AI → Assistant):
- A picker above the conversation: Desk Assistant (the default), AI Trader, Trade Reviewer, News Analyst, Incident
  Explainer, Technical Analyst, Options Analyst, Macro Analyst, Fundamental Analyst, Data Curator, Compliance Triage, Strategy Researcher, and from 10 Oct the Validator, the Risk Memo and the Execution Reporter (those three console only, no Telegram prefix). The choice survives a reload; `/ai/assistant?agent=ai-trader` opens one, and each agent's panel on AI →
  Agents links to it ("Talk with it").
- One conversation per agent while the page is open: switching back finds it as it was, and an answer streams on in
  its own conversation. One question at a time across them.
- The header says what the agent does, when it runs, and what it reads; each agent has its own starter questions (AI
  Trader: "Why did you buy on 16 Sep?", "What would you do now?", "Which lessons are you using?").
- 👍, 👎 with a correction, and `/remember` save for the agent being talked with; the line under an answer names the
  memories it was given.

**On Telegram:** `/trader …`, `/reviewer …`, `/news …`, `/incident …`, `/technical …`, `/options …`, `/macro …`,
`/fundamental …`, `/data …`, `/compliance …` or `/research …` asks that agent; the chat stays with it, so a
follow-up needs no prefix, until `/assistant`, `/new` or half an hour of quiet brings back the Desk Assistant. A prefix
alone switches without asking. An answer from another agent than the Assistant names it under the text.

## Memory

The models do not learn: their weights are the provider's. What an agent learns is kept as **memories**, short
notes put into its system prompt on every call (`AiMemoryBook`). Each one can be read, edited, traced to where it
came from, and taken out. Every built agent has memory (`Ai:MemoryAgents`): the Desk Assistant from the morning of
1 Oct, the Trade Reviewer, News Analyst and Incident Explainer from that afternoon, once they were all switched on,
the AI Trader from 2 Oct, for the lessons it learns from its own days ([AI Trader](ai_trader.md#lessons)), and the four
desk analysts, the Validator, the Risk Memo, the Execution Reporter, the Data Curator, the Compliance Triage and the
Strategy Researcher from 10 Oct, when they were built.

| Kind | Written by | Used |
|---|---|---|
| `note` | The owner: `/remember` on Telegram, or Add a note on AI → Memory | At once |
| `correction` | The owner: 👎 on an answer with what it should have said; its question is kept as context | At once |
| `lesson` | The daily check: the Judge's one-line lesson from a question the Assistant got wrong. For the AI Trader, a reflection on one of its finished days | **Only after it passes its own test** |

A wrong lesson in the prompt would repeat its mistake on every answer, so each lesson is tested before it is used.
The Assistant is asked the question the lesson came from again, with the lesson on trial in its memory
(`AiAskInput.TrialMemoryIds`: read on that call alone). Two questions it got right are asked too.
- The lesson is used only if the first now passes and the other two still do.
- Otherwise it is dropped, with the reason: `check: did not fix its question` or `check: broke a right answer`.

An AI Trader lesson is tested on up to 16 of its past looks instead, each asked without it and with it, the answers
played on the recorded prices; it is used only if it adds ₹500 or more after charges, helps on as many looks as it
hurts and answers no worse ([AI Trader](ai_trader.md#lessons)).

Lessons once waited for the owner's approval. On 1 Oct the owner said he cannot keep answering yes or no, so the
evidence decides. He can still take any lesson out, and every lesson sits on Today and AI → Memory.

The check asks the Judge about at most `Ai:MaxLessonsPerCheck` (3) failed questions a day. It skips a kind of question
(the question with its numbers taken out) that already has a lesson, or had one dropped in the last 14 days.

**What reaches the prompt:**
- Only `active` memories of the agent that asks. Corrections come first, then notes, then lessons.
- While they fit `Ai:MemoryBudgetChars` (2,400) and `Ai:MemoryMaxItems` (12), all of them go.
- Past that, the question is embedded and the memories closest to it go. Any below `Ai:MemoryMinScore` (0.3) stay
  out, and if the embedding fails the newest go instead.
- They are introduced as guidance, not data: numbers still come from the tools, and a note that disagrees with a
  tool is out of date.
- A caller with its own system prompt, and a model's health test, get no memories.
- The AI Trader is that caller. The recall knows no day, and a replay of 16 Sep must not read a lesson learned on
  24 Sep. So it sends its own prompt with only what was known before the moment it decides (lessons from earlier days,
  notes written before), and names them (`AiAskInput.GivenMemoryIds`): its call keeps them like recalled ones.
- The owner's chat with the AI Trader goes through the recall, which then gives it what its next look reads: a
  lesson only once the day it was learned from is over.

**Outcomes.** Every call keeps the ids of the memories it was given (`ai_calls.MemoryIdsJson`). The owner's verdict
on an answer adds to `Ups` or `Downs` of each of those memories, and the check's grade adds to `CheckPasses` or
`CheckFails`. A memory is flagged for review in two cases:
- two 👎 and more 👎 than 👍;
- three failed check answers and more failed than passed.

The flag never retires anything; the owner decides. `GET memories/progress` shows by day the check's score, the
memories active that night and the owner's 👍 and 👎: whether memory is making the Assistant better, as numbers.

**Kept safe.** Texts are masked like the tools' output (a secret's shape, the server's name, addresses, home paths),
because a memory goes to the provider with every answer. A text is at most 600 characters. Embedding calls are rows
on the Calls tab under the pseudo-agent `memory`.

| Endpoint | What |
|---|---|
| `GET memories?agent=&status=` | The memories, their counts, the budget and the characters in use |
| `POST memories` | A note (active) |
| `PUT memories/{id}` | Edit the text; approve or restore (`active`), reject, retire |
| `POST calls/{id}/feedback` | 👍 `1`, 👎 `-1` with an optional correction, or `0` to take it back |
| `GET memories/progress?days=30` | The day-by-day effect |

## Adding a tool

1. Implement `IAiTool` in `src/AlgoTrading.Api/Services/AiTools/`:
   - take only query services;
   - return a projection;
   - pass free text through `AiToolFormat.Text`;
   - bound the rows.
2. Register it in `Program.cs`.
3. Add its name to `AiToolNames` and to the agent's `Tools` in `AiCatalog`, or its `ChatTools` for what it reads in a
   chat (name it in the agent's chat prompt too). `AiAgentChatTests` fails for a name with no tool or no registration.
4. Add it to `AiToolDataTests`, or `AiAgentChatToolTests` for a chat tool.

## Adding an agent

1. Build it: code that assembles its input from the desk's own data. The model reads that input; it never fetches
   anything itself.
2. In `AiCatalog.Agents`, set its entry to `Built: true`, with its system prompt, and a chat persona and chat tools
   so the owner can talk with it.
3. Call `AiGateway.AskAsync` with its key and `Source = "schedule"`, or `POST /api/Ai/ask` with the engine's service
   account (that path is not open to the Service role yet).

The switch, the chain, the rate limit and the audit row then apply to it without further work.
