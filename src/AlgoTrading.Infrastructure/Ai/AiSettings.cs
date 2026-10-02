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
    /// Whether the AI Trader places what its rules allow (paper orders in its own account). Off: it decides and
    /// places nothing (shadow), which is how it starts (owner, 1 Oct 2026: one shadow session first).
    /// </summary>
    public bool AiTraderExecute { get; set; } = false;

    /// <summary>Minutes between the AI Trader's looks at the market.</summary>
    public int AiTraderEveryMinutes { get; set; } = 10;

    /// <summary>IST times between which it looks (new positions are further limited by its rules).</summary>
    public string AiTraderFromIst { get; set; } = "09:20";

    public string AiTraderUntilIst { get; set; } = "15:00";

    /// <summary>Tries for one report before a scheduled agent gives up on its subject.</summary>
    public int MaxReportAttempts { get; set; } = 3;

    /// <summary>Whether agents read their memories (<see cref="AiMemoryBook"/>) before answering.</summary>
    public bool MemoryEnabled { get; set; } = true;

    /// <summary>
    /// The agents that have memory, comma-separated. The Desk Assistant first (owner, 1 Oct morning), then every
    /// built agent the same day, once they were all on: a correction on any agent's report is its memory. The AI
    /// Trader from 2 Oct, for its lessons; it reads them bounded by the day it decides on, never through the
    /// gateway's recall (<c>AiTraderMemory</c>).
    /// </summary>
    public string MemoryAgents { get; set; } =
        $"{AiCatalog.DeskAssistant},{AiCatalog.TradeReviewer},{AiCatalog.NewsAnalyst},{AiCatalog.IncidentExplainer},{AiCatalog.AiTrader}";

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

    public bool KeyConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Whether <paramref name="agentKey"/> reads memories.</summary>
    public bool HasMemory(string agentKey) =>
        MemoryEnabled && MemoryAgents.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(agentKey, StringComparer.OrdinalIgnoreCase);
}
