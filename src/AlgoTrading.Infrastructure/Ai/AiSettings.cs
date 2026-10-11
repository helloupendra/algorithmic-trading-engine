namespace AlgoTrading.Infrastructure.Ai;

/// <summary>
/// The <c>Ai</c> section: where the hosted models are and how hard the desk may
/// lean on them. The key comes from <c>NVIDIA_API_KEY</c> in the server's .env
/// (scripts/_gen_local_settings.py) and is never sent to a browser.
/// </summary>
public sealed class AiSettings
{
    public string ApiKey { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = AiCatalog.DefaultBaseUrl;

    /// <summary>Questions one user may ask in any ten minutes.</summary>
    public int PerUserPer10Min { get; set; } = 30;

    /// <summary>
    /// Calls the whole desk may start in any minute, below the free tier's
    /// unpublished limit (about 40) so a burst is refused here, in words,
    /// rather than by the provider as a 429 on every model of the chain.
    /// </summary>
    public int GlobalPerMinute { get; set; } = 30;

    /// <summary>Calls in flight at once, all users together.</summary>
    public int MaxConcurrent { get; set; } = 4;

    /// <summary>Calls one user may have in flight at once.</summary>
    public int MaxConcurrentPerUser { get; set; } = 2;

    /// <summary>Seconds a model may take to start answering (headers and the first token) before the next is tried.</summary>
    public double FirstTokenTimeoutSeconds { get; set; } = 90;

    /// <summary>Seconds a model may go silent mid-answer before the next is tried.</summary>
    public double IdleTimeoutSeconds { get; set; } = 60;

    /// <summary>Seconds one model may take in all.</summary>
    public double AttemptTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// A model that refuses at once for capacity ("Service temporarily
    /// overloaded", 429, 502, 503) is asked once more after this many seconds
    /// before the chain moves on; 0 turns it off. On 30 Sep the free tier's
    /// Nemotrons refused like this for a moment at a time, while the next
    /// models in the chain queued for 90 s.
    /// </summary>
    public double CapacityRetrySeconds { get; set; } = 2;

    /// <summary>
    /// A model whose failure took at least this long (a timeout, a queue) is
    /// not asked again in later rounds of the same question; one that failed
    /// quickly is, since it may have capacity again by then.
    /// </summary>
    public double SlowFailureSeconds { get; set; } = 10;

    /// <summary>Whether the docs are indexed for the Assistant's <c>search_docs</c> (embedding calls at start and every 6 hours, only for passages that changed).</summary>
    public bool DocSearchEnabled { get; set; } = true;

    /// <summary>Whether the Desk Assistant answers its linked owner on the desk's Telegram bot (private chats only).</summary>
    public bool TelegramAssistantEnabled { get; set; } = true;

    /// <summary>Whether <see cref="AiHealthProbe"/> asks a model whose cooling has ended one tiny question.</summary>
    public bool HealthProbeEnabled { get; set; } = true;

    /// <summary>Rounds in which a model may ask for tools before it must answer with what it has.</summary>
    public int MaxToolRounds { get; set; } = 4;

    /// <summary>Tool calls one question may make in all.</summary>
    public int MaxToolCalls { get; set; } = 8;

    /// <summary>Characters of one tool's answer sent to the model; past it the tool is asked to narrow down.</summary>
    public int MaxToolResultChars { get; set; } = 16_000;

    /// <summary>Seconds one tool may take (the strategy catalog can take 20 s on a cold start).</summary>
    public double ToolTimeoutSeconds { get; set; } = 30;

    /// <summary>Whether the scheduled agents (reviewer, news, incidents) run at all on this API. Each also has its switch on the AI page.</summary>
    public bool SchedulerEnabled { get; set; } = true;

    /// <summary>IST time after which a trading day's stopped runs are reviewed ("15:45": after the NSE close settles).</summary>
    public string ReviewAfterIst { get; set; } = "15:45";

    /// <summary>
    /// Send the AI's one Telegram digest a day to the desk's system channel: the run reviews and the AI Trader's
    /// day (<c>AiDailyDigest</c>). Named when it carried the reviews alone.
    /// </summary>
    public bool ReviewDigestToTelegram { get; set; } = true;

    /// <summary>Minutes between the News Analyst's batches.</summary>
    public int NewsEveryMinutes { get; set; } = 10;

    /// <summary>
    /// Headlines and filings in one News Analyst call. 12 was too many on 1 Oct: Nemotron Super's thinking and
    /// records ran past the 6,000-token answer limit in 3 of 17 batches, and it spent the whole limit thinking in one more.
    /// </summary>
    public int NewsBatchSize { get; set; } = 8;

    /// <summary>How far back the News Analyst looks for items it has not read: never the 2020 backfills.</summary>
    public int NewsLookbackHours { get; set; } = 24;

    /// <summary>Minutes between the Fundamental Analyst's polls for new results filings.</summary>
    public int FundamentalEveryMinutes { get; set; } = 10;

    /// <summary>Results filings in one Fundamental Analyst call: a filing's text is longer than a headline's.</summary>
    public int FundamentalBatchSize { get; set; } = 3;

    /// <summary>Whether the Desk Assistant's daily check runs (it asks only while the Assistant is on).</summary>
    public bool AssistantCheckEnabled { get; set; } = true;

    /// <summary>IST time after which the weekday check runs: after the NSE close has settled.</summary>
    public string AssistantCheckAfterIst { get; set; } = "16:40";

    /// <summary>Whether the Desk Assistant's weekly exam runs (it asks only while the Assistant is on).</summary>
    public bool ExamEnabled { get; set; } = true;

    /// <summary>The IST day of the week the exam starts on: a day with no session, so it competes with nothing.</summary>
    public DayOfWeek ExamDay { get; set; } = DayOfWeek.Sunday;

    /// <summary>IST time after which the exam starts on <see cref="ExamDay"/>.</summary>
    public string ExamAfterIst { get; set; } = "10:30";

    /// <summary>Questions in one exam: a stable sample of the bank.</summary>
    public int ExamMaxQuestions { get; set; } = 150;

    /// <summary>Asks per question; pass^k is measured on this k.</summary>
    public int ExamRepeats { get; set; } = 3;

    /// <summary>
    /// Asks per scheduler minute, so the other agents keep their turn during a long exam. 2 keeps it at 20 in ten
    /// minutes, under <see cref="PerUserPer10Min"/> (30), which counts the exam as one user: 3 would sit on the cap.
    /// </summary>
    public int ExamAsksPerTick { get; set; } = 2;

    /// <summary>How far back, in calendar days, the bank takes finished trading days from.</summary>
    public int ExamLookbackDays { get; set; } = 45;

    /// <summary>
    /// Whether the AI Trader places what its rules and the Validator allow: paper orders in its own manual book and
    /// strategy starts in its own account (live mode). On from 10 Oct 2026 (owner: every AI part on). Off: it decides
    /// and places nothing, keeping its allowed buys in its shadow book (shadow mode), as it began on 1 Oct.
    /// </summary>
    public bool AiTraderExecute { get; set; } = true;

    /// <summary>Minutes between the AI Trader's looks at the market.</summary>
    public int AiTraderEveryMinutes { get; set; } = 10;

    /// <summary>IST times between which it looks (new positions are further limited by its rules).</summary>
    public string AiTraderFromIst { get; set; } = "09:20";

    public string AiTraderUntilIst { get; set; } = "15:00";

    /// <summary>Tries for one report before a scheduled agent gives up on its subject.</summary>
    public int MaxReportAttempts { get; set; } = 3;

    /// <summary>
    /// IST time of the Data Curator's pre-open pass on trading days. It runs from then until 09:00, never later: the
    /// session is the live desk's.
    /// </summary>
    public string DataCuratorMorningIst { get; set; } = "08:00";

    /// <summary>The option chains the Data Curator checks minute by minute (the chain poller's underlyings).</summary>
    public string DataCuratorChainUnderlyings { get; set; } = "NIFTY,BANKNIFTY,SENSEX";

    /// <summary>The IST day of the week the Compliance Triage reads the week's circulars.</summary>
    public DayOfWeek ComplianceDay { get; set; } = DayOfWeek.Saturday;

    /// <summary>IST time on <see cref="ComplianceDay"/> after which it reads them.</summary>
    public string ComplianceAfterIst { get; set; } = "10:00";

    /// <summary>Circulars in one Compliance Triage call; each carries up to 2,500 characters of its text.</summary>
    public int ComplianceBatchSize { get; set; } = 4;

    /// <summary>How far back, by when they were recorded, circulars are still triaged.</summary>
    public int ComplianceLookbackDays { get; set; } = 21;

    /// <summary>The IST day of the week the Strategy Researcher writes its note: a day with no session.</summary>
    public DayOfWeek ResearchDay { get; set; } = DayOfWeek.Sunday;

    /// <summary>IST time on <see cref="ResearchDay"/> after which it writes it.</summary>
    public string ResearchAfterIst { get; set; } = "11:00";

    /// <summary>Whether agents read their memories (<see cref="AiMemoryBook"/>) before answering.</summary>
    public bool MemoryEnabled { get; set; } = true;

    /// <summary>
    /// The agents that have memory, comma-separated. The Desk Assistant first (owner, 1 Oct morning), then every
    /// built agent the same day, once they were all on: a correction on any agent's report is its memory. The AI
    /// Trader from 2 Oct, for its lessons; it reads them bounded by the day it decides on, never through the
    /// gateway's recall (<c>AiTraderMemory</c>). The Validator, the Risk Memo and the Execution Reporter from 10 Oct,
    /// when they were built: a correction on one of their records is read by their next call. The Data Curator,
    /// Strategy Researcher and Compliance Triage from 10 Oct too.
    /// </summary>
    /// <remarks>The four desk analysts from 10 Oct, when they were built: a correction on their reports is their memory too.</remarks>
    public string MemoryAgents { get; set; } =
        $"{AiCatalog.DeskAssistant},{AiCatalog.TradeReviewer},{AiCatalog.NewsAnalyst},{AiCatalog.IncidentExplainer},{AiCatalog.AiTrader}," +
        $"{AiCatalog.TechnicalAnalyst},{AiCatalog.OptionsAnalyst},{AiCatalog.MacroAnalyst},{AiCatalog.FundamentalAnalyst}," +
        $"{AiCatalog.Validator},{AiCatalog.RiskMemo},{AiCatalog.ExecutionReporter}," +
        $"{AiCatalog.DataCurator},{AiCatalog.StrategyResearcher},{AiCatalog.ComplianceTriage}";

    /// <summary>Characters of memories one call may carry in its system prompt; past it the closest to the question win.</summary>
    public int MemoryBudgetChars { get; set; } = 2400;

    /// <summary>Memories one call may carry at most.</summary>
    public int MemoryMaxItems { get; set; } = 12;

    /// <summary>
    /// Once the memories outgrow the budget, the lowest cosine similarity to
    /// the question a memory may have and still be read. Unrelated passages
    /// score about 0.1 to 0.3 with Nemotron 3 Embed; a matching one 0.5 or more.
    /// </summary>
    public double MemoryMinScore { get; set; } = 0.3;

    /// <summary>Whether the daily check proposes a lesson for a question the Assistant got wrong (for the owner to approve).</summary>
    public bool LessonsFromCheck { get; set; } = true;

    /// <summary>Lessons the daily check may propose in one day: each is one Judge call on the free tier.</summary>
    public int MaxLessonsPerCheck { get; set; } = 3;

    /// <summary>
    /// Whether the AI Trader learns from its own days (owner, 2 Oct): a reflection on each finished replay or live
    /// day proposes lessons, and each is tested on past looks before it is used. Runs while the AI Trader is on.
    /// </summary>
    public bool AiTraderLessons { get; set; } = true;

    /// <summary>Active AI Trader lessons at most: past it, the one with the weakest evidence makes way.</summary>
    public int AiTraderMaxLessons { get; set; } = 8;

    /// <summary>Past looks a lesson is tested on, each asked twice (without it and with it).</summary>
    public int AiTraderLessonPoints { get; set; } = 16;

    /// <summary>Rupees, after charges, by which a lesson must beat the looks without it over its test to be used.</summary>
    public decimal AiTraderLessonMinGain { get; set; } = 500m;

    /// <summary>Claude on Amazon Bedrock, the second provider (<see cref="BedrockClaudeClient"/>); off until the owner turns it on.</summary>
    public AiBedrockSettings Bedrock { get; set; } = new();

    /// <summary>Open models on Amazon Bedrock, the third provider (<see cref="BedrockOpenChatClient"/>); on unless the owner turns it off.</summary>
    public AiBedrockOpenSettings BedrockOpen { get; set; } = new();

    /// <summary>Voice notes to the Telegram assistant and its spoken replies ("Ira"); on unless the owner turns it off.</summary>
    public AiVoiceSettings Voice { get; set; } = new();

    public bool KeyConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// Whether any provider can be asked: NVIDIA's key is there, or Claude or the open models on Bedrock are on (their
    /// credentials are the server's AWS role, which the desk cannot see). What starts the agents and the Telegram
    /// Assistant.
    /// </summary>
    public bool ProviderConfigured => KeyConfigured || Bedrock.Enabled || BedrockOpen.Enabled;

    /// <summary>Whether <paramref name="agentKey"/> reads memories.</summary>
    public bool HasMemory(string agentKey) =>
        MemoryEnabled && MemoryAgents.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(agentKey, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The <c>Ai:Bedrock</c> section: Claude through Amazon Bedrock's Messages API, and Titan for embeddings. No key here
/// or anywhere in the desk's files, and none is read from its settings: the SDKs sign each request with the default
/// AWS credential chain, which on the server is the EC2 instance profile (IMDSv2). Off, nothing here calls AWS.
/// </summary>
public sealed class AiBedrockSettings
{
    /// <summary>
    /// Off: a <c>bedrock:</c> model in a chain is skipped with its reason, and the chains are NVIDIA's. On: every
    /// chain is Claude on Bedrock (<see cref="AiTierDef.BedrockChain"/>; an owner's chain keeps only its Bedrock
    /// models), so no answer comes from NVIDIA (owner, 10 Oct 2026).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The AWS region asked, for Claude, for Titan and for the open models (<see cref="AiBedrockOpenSettings"/>, which
    /// has no region of its own). N. Virginia: in Mumbai (ap-south-1) Bedrock's Messages API
    /// answered "model does not exist" for these models (10 Oct 2026). The model ids carry no region or <c>global.</c>
    /// prefix; the Global endpoint type routes across regions with no price premium.
    /// </summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// How hard Claude thinks, per tier (<c>output_config.effort</c>: low, medium, high, xhigh, max). A model's
    /// health test asks at low.
    /// </summary>
    public Dictionary<string, string> EffortByTier { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["judge"] = "high",
        ["analyst"] = "high",
        ["extract"] = "low",
    };

    /// <summary>
    /// The fewest tokens a Claude answer may use. Its thinking counts against the limit, and a caller's limit was set
    /// for the answer alone (the Desk Assistant asks 4,096): Fable at high effort could spend it all thinking and say
    /// nothing. The answer's length is still the prompt's to set.
    /// </summary>
    public int MinMaxTokens { get; set; } = 16_000;

    /// <summary>The embedding model the re-embedding switches to: Amazon Titan Text Embeddings V2, on <c>bedrock-runtime</c> InvokeModel.</summary>
    public string EmbeddingModel { get; set; } = "amazon.titan-embed-text-v2:0";

    /// <summary>Titan V2's vector size: 1024 (its default), 512 or 256.</summary>
    public int EmbeddingDimensions { get; set; } = 1024;

    /// <summary>Embedding calls in flight at once: Titan takes one text per call.</summary>
    public int EmbeddingConcurrency { get; set; } = 4;

    /// <summary>
    /// US dollars per million tokens, by Bedrock model id: Anthropic's list prices (Oct 2026), for an estimate of what
    /// a call cost. Bedrock bills per AWS's own price page; Haiku 5.5's are for prompts up to 100k tokens.
    /// </summary>
    public Dictionary<string, AiModelPrice> Prices { get; set; } = new(StringComparer.Ordinal)
    {
        ["anthropic.claude-fable-5-1"] = new() { Input = 10m, Output = 50m, CacheRead = 0.25m, CacheWrite = 12.5m },
        ["anthropic.claude-opus-5-5"] = new() { Input = 4m, Output = 20m, CacheRead = 0.20m, CacheWrite = 5m },
        ["anthropic.claude-sonnet-5-5"] = new() { Input = 2m, Output = 10m, CacheRead = 0.10m, CacheWrite = 2.5m },
        ["anthropic.claude-haiku-5-5"] = new() { Input = 0.10m, Output = 0.50m, CacheRead = 0.01m, CacheWrite = 0.125m },
        ["anthropic.claude-haiku-4-5"] = new() { Input = 1m, Output = 5m, CacheRead = 0.10m, CacheWrite = 1.25m },
    };
}

/// <summary>
/// The <c>Ai:BedrockOpen</c> section: open models (Kimi, GLM, DeepSeek, Qwen, gpt-oss, Mistral, Nemotron, MiniMax) on
/// Amazon Bedrock's OpenAI-compatible endpoint (<see cref="BedrockOpenChatClient"/>), in
/// <see cref="AiBedrockSettings.Region"/>. As with Claude, no key here or anywhere in the desk's files, and none is
/// read from its settings: each request is signed with the default AWS credential chain (the EC2 instance profile).
/// </summary>
public sealed class AiBedrockOpenSettings
{
    /// <summary>
    /// On (the default, owner 10–11 Oct 2026): while Claude is off, every chat tier is these models alone
    /// (<see cref="AiTierDef.OpenChain"/>); NVIDIA is not asked. Off: a <c>bedrock-oss:</c> model in a chain is skipped with its
    /// reason. Either way nothing calls AWS until a chain reaches one of these models.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// US dollars per million tokens, by Bedrock model id: AWS's Standard on-demand prices in US East (N. Virginia), as
    /// its Bedrock pricing page gave them on 10 Oct 2026. A model the page gave no N. Virginia price for (Qwen3 235B
    /// A22B 2507) is left out, so its cost shows as unknown rather than guessed. Another region bills differently.
    /// </summary>
    public Dictionary<string, AiModelPrice> Prices { get; set; } = new(StringComparer.Ordinal)
    {
        ["moonshotai.kimi-k2.5"] = new() { Input = 0.60m, Output = 3.00m },
        ["zai.glm-5"] = new() { Input = 1.00m, Output = 3.20m },
        ["deepseek.v3.2"] = new() { Input = 0.62m, Output = 1.85m },
        ["openai.gpt-oss-120b"] = new() { Input = 0.15m, Output = 0.60m },
        ["mistral.mistral-large-3-675b-instruct"] = new() { Input = 0.50m, Output = 1.50m },
        ["nvidia.nemotron-super-3-120b"] = new() { Input = 0.15m, Output = 0.65m },
        ["minimax.minimax-m2.5"] = new() { Input = 0.30m, Output = 1.20m },
    };
}

/// <summary>
/// The <c>Ai:Voice</c> section: voice notes to the desk's Telegram assistant and questions spoken on the console's
/// AI → Assistant, written down by Mistral's Voxtral on Bedrock's OpenAI-compatible endpoint (in
/// <see cref="AiBedrockSettings.Region"/>), and answers spoken back by Amazon Polly. As for Bedrock, no key here or
/// anywhere in the desk's files: both are signed with the server's AWS role.
/// </summary>
public sealed class AiVoiceSettings
{
    /// <summary>
    /// Off: a voice note gets one line asking for the question typed, the console's voice endpoints answer 503, and
    /// nothing calls AWS for either.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The audio model, by Bedrock's id. Voxtral Small: Voxtral Mini (<c>mistral.voxtral-mini-3b-2507</c>) answered the
    /// question in the voice note instead of writing it down (11 Oct 2026).
    /// </summary>
    public string TranscribeModel { get; set; } = "mistral.voxtral-small-24b-2507";

    /// <summary>
    /// Whether answers may be spoken by Amazon Polly: a voice note's answer on Telegram, as a voice message after the
    /// text, and on the console when the viewer has "Speak replies" on. Off, nothing asks Polly.
    /// </summary>
    public bool SpeakReplies { get; set; } = true;

    public string PollyVoice { get; set; } = "Kajal";

    public string PollyEngine { get; set; } = "neural";

    public string PollyLanguage { get; set; } = "en-IN";

    /// <summary>Where Polly is asked: Mumbai has the neural Kajal voice.</summary>
    public string PollyRegion { get; set; } = "ap-south-1";

    /// <summary>
    /// US dollars per million characters spoken: Polly's list price for neural voices (Oct 2026), for an estimate of
    /// what a reply cost; AWS bills per its own page.
    /// </summary>
    public decimal PollyUsdPerMillionChars { get; set; } = 16m;

    /// <summary>The longest voice note listened to, in seconds; a longer one is politely refused.</summary>
    public int MaxSeconds { get; set; } = 120;

    /// <summary>The largest voice note downloaded, in bytes (2 MB).</summary>
    public int MaxBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>
    /// The largest recording the console may send, in bytes (4 MB). The console sends 16 kHz mono 16-bit WAV, 32 KB a
    /// second, so <see cref="MaxSeconds"/> of it (3.75 MB at 120 s) would not fit in <see cref="MaxBytes"/>, which is
    /// sized for Telegram's compressed voice notes.
    /// </summary>
    public int ConsoleMaxBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Characters of the spoken reply at most, cut at a sentence; the full answer is in the text above it.</summary>
    public int MaxSpokenChars { get; set; } = 600;

    /// <summary>
    /// The name the owner may call the assistant in a voice note, until he gives it another with /name on Telegram
    /// (<see cref="AiSettingsStore.VoiceNameKey"/>). A voice question's prompt gets one line saying it may answer to it;
    /// empty, no line.
    /// </summary>
    public string Name { get; set; } = "Ira";

    /// <summary>
    /// Other spellings that count as the name in a hands-free utterance, beyond what the matcher already hears as it
    /// (case, punctuation, Devanagari, a misheard vowel or a trailing h): for example a nickname, or a mishearing seen on
    /// the Calls tab. Empty by default.
    /// </summary>
    public List<string> NameAliases { get; set; } = [];

    /// <summary>
    /// Hands-free utterances one user may have written down in any minute (console, AI → Assistant). Each is one
    /// Voxtral call, whether or not it was said to the assistant; past this the API answers 429 with Retry-After.
    /// They have this budget of their own instead of <see cref="AiSettings.PerUserPer10Min"/>, which six a minute would
    /// spend in five minutes of talk at the desk. 0 switches hands-free off.
    /// </summary>
    public int HandsFreePerMinute { get; set; } = 6;

    /// <summary>Ira speaking first: alerts and the two briefings to the linked owners' private chats (<c>Ai:Voice:Alerts</c>).</summary>
    public AiVoiceAlertsSettings Alerts { get; set; } = new();
}

/// <summary>
/// The <c>Ai:Voice:Alerts</c> section: what Ira tells the linked owners without being asked (the AI Trader acting live,
/// a run's risk rules stopping it or closing a group, a high or critical Sentinel incident) and the two spoken
/// briefings of an NSE trading day. Each owner can turn them off for himself with <c>/alerts off</c>.
/// </summary>
public sealed class AiVoiceAlertsSettings
{
    /// <summary>The desk-wide switch: off, Ira tells nobody anything first, whatever each owner chose.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The morning briefing's time, IST (HH:mm), on NSE trading days.</summary>
    public string MorningAt { get; set; } = "09:10";

    /// <summary>
    /// The close briefing's time, IST (HH:mm), on NSE trading days; on a day the session closes later (a special
    /// session), ten minutes after that close.
    /// </summary>
    public string CloseAt { get; set; } = "15:40";

    /// <summary>Event messages one owner gets in an hour at most; past it they fold into one "and N more" line. Briefings are not counted.</summary>
    public int MaxPerHour { get; set; } = 8;
}

/// <summary>A model's price in US dollars per million tokens. A cache write is the five-minute one (1.25 × input).</summary>
public sealed class AiModelPrice
{
    public const string Note = "list price; Bedrock bills per AWS's page";

    public decimal Input { get; set; }

    public decimal Output { get; set; }

    public decimal CacheRead { get; set; }

    public decimal CacheWrite { get; set; }

    /// <summary>What <paramref name="usage"/> cost at this price: uncached input, cache reads and writes, and output.</summary>
    public decimal Cost(AiUsage usage)
    {
        long read = usage.CacheReadTokens ?? 0;
        long write = usage.CacheWriteTokens ?? 0;
        long input = Math.Max(0, (usage.PromptTokens ?? 0) - read - write);
        long output = usage.CompletionTokens ?? 0;
        return (input * Input + read * CacheRead + write * CacheWrite + output * Output) / 1_000_000m;
    }
}
