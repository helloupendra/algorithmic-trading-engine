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

    /// <summary>Send one Telegram digest to the desk's system channel when a batch of reviews is done.</summary>
    public bool ReviewDigestToTelegram { get; set; } = true;

    /// <summary>Minutes between the News Analyst's batches.</summary>
    public int NewsEveryMinutes { get; set; } = 10;

    /// <summary>Headlines and filings in one News Analyst call.</summary>
    public int NewsBatchSize { get; set; } = 12;

    /// <summary>How far back the News Analyst looks for items it has not read: never the 2020 backfills.</summary>
    public int NewsLookbackHours { get; set; } = 24;

    /// <summary>Tries for one report before a scheduled agent gives up on its subject.</summary>
    public int MaxReportAttempts { get; set; } = 3;

    public bool KeyConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
