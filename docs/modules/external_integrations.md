# Self-hosting and external integrations

OpenFNO runs its API, trading engine, database and console on infrastructure you control.
Self-hosting does not mean all processing stays on that host. Enabled integrations make
outbound requests, and some features are enabled by default when their credentials are available.
The installation does not provide a single switch that guarantees zero outbound traffic.

## What can leave the host

| Integration | Data sent | Where to inspect or control it |
| --- | --- | --- |
| Market-data and broker connectors (Dhan, FYERS, Angel One, TrueData, as configured) | Authentication credentials for that provider, instrument identifiers, subscriptions and history/quote requests; portfolio requests for linked accounts | Connectors shows configured providers and sessions; Data shows running feeds. Stopping a feed stops that stream, not other history, sign-in or portfolio requests. Disable the relevant jobs and remove unused provider credentials to stop those paths. |
| Hosted AI (Amazon Bedrock or NVIDIA) | Questions, conversation context, selected tool results, market data, run/trade summaries, strategy descriptions or configuration, news and document excerpts used by that agent; document/search text for embeddings | AI → Models shows providers and model chains; AI → Agents controls individual agents; AI → Calls records calls. `Ai:SchedulerEnabled=false` stops scheduled agents only. It does not disable interactive chat, probes, embeddings or Python scripts. Turning off one provider may route calls to another. |
| Voice (Amazon Transcribe, or configured Bedrock transcription; Amazon Polly for speech) | Recorded audio for transcription and reply text for speech synthesis; transcribed questions may then go to the configured AI model | `Ai:Voice:Enabled=false` disables the application's voice transcription and speech paths. Typed chat remains separate. Regions and transcription provider are configured under `Ai:Voice`. |
| Telegram | Chat identifiers, user messages, voice files and assistant replies; configured alert, incident and digest messages can include trading summaries | Disable `Ai:TelegramAssistantEnabled` for the assistant and `Ai:ReviewDigestToTelegram` for its digest. Alerts and other bots have separate settings/credentials. Disabling the assistant does not disable all Telegram notifications. |
| Google Drive archive through rclone | Compressed exports of `live_ticks`, `option_chain_snapshots`, `live_bars` and `run_pnl_minutes` by default; selected archive jobs may export other supported tables | System → Health shows the archive manifest. `ARCHIVE_REMOTE` selects the destination. Stop scheduled/manual archive jobs and remove or restrict that rclone remote to prevent uploads. Changing local database retention is not an upload-disable switch. |
| News, filings and other external research/data sources | Requested identifiers, URLs or search terms, and provider authentication where configured; remote services also receive normal connection metadata | Disable the corresponding collectors/jobs and credentials. A broker or AI switch does not control these sources. |

Broker secrets are not intended as AI context. Structured AI tool results use selected fields and
redaction, but that is not a guarantee for arbitrary text pasted into chat or imported documents.
Do not paste credentials into prompts. The NVIDIA API key is sent to NVIDIA as authentication;
it is not returned to the browser. AWS requests use the configured AWS credentials/role to sign requests.

## Storage and retention

AI call records, tool results, reports and applicable conversation/memory records are stored by the
installation. The Calls page is an audit view; clearing a browser conversation does not guarantee
that these server records or provider copies have been deleted. Operator-controlled database and
backup retention must cover these records separately.

The application does not set or guarantee a universal retention period for external providers.
Review the actual provider account, region and applicable retention settings before sending sensitive
content. Removing credentials or disabling a feature stops the relevant future requests; it does not
request deletion of content already sent. Drive exports remain in the configured remote until removed
under the operator's remote-storage policy. Local retention and deletion do not delete those exports.

For an installation requiring no external processing, disable the relevant application paths and
separately enforce outbound network restrictions. Verify those restrictions in that deployment; this
page does not claim a tested local-only operating mode.

## Implementation references

- Hosted AI transports and configuration: `src/AlgoTrading.Infrastructure/Ai/`, especially
  `BedrockOpenChatClient`, `BedrockClaudeClient`, `NvidiaChatClient` and `AiSettings`.
- AI tool projections/redaction and call auditing: [AI workspace](ai.md#what-leaves-the-server).
- Archive tables and destination: `scripts/archive_to_drive.py`; scheduled runner:
  `src/AlgoTrading.Api/Services/NightlyArchiveService.cs`.
- [Data storage and retention](data_module.md), [connectors](connectors_module.md),
  and [portfolio privacy](portfolio.md#privacy).
