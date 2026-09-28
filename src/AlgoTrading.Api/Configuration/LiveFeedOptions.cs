namespace AlgoTrading.Api.Configuration;

/// <summary>
/// How the live feed hub (<c>/hubs/livefeed</c>) pushes prices to browsers.
/// </summary>
public class LiveFeedOptions
{
    public const string SectionName = "LiveFeed";

    /// <summary>
    /// How often the latest price of every symbol that changed is pushed, in
    /// milliseconds. A symbol that ticks ten times inside one interval reaches
    /// a browser once, at its last price.
    /// </summary>
    /// <remarks>
    /// 250 ms is four frames a second: faster than anyone reads a premium,
    /// slow enough that an open-bell burst costs every tab a handful of
    /// messages rather than hundreds. Values under
    /// <see cref="MinPushIntervalMs"/> are raised to it.
    /// </remarks>
    public int PushIntervalMs { get; set; } = 250;

    /// <summary>The shortest interval honoured, whatever the setting says.</summary>
    public const int MinPushIntervalMs = 50;
}
