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

    public bool KeyConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
