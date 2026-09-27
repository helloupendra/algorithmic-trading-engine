namespace AlgoTrading.Api.Configuration;

/// <summary>
/// The automatic switch from a silent Dhan feed to FYERS
/// (<see cref="AlgoTrading.Api.Services.FeedFailoverService"/>), bound from the
/// "FeedFailover" section and re-read at every check, so a change in
/// appsettings.Local.json takes effect without a restart.
/// </summary>
public class FeedFailoverOptions
{
    public const string SectionName = "FeedFailover";

    /// <summary>False: the service does nothing at all, not even log.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// True (the default, and the committed setting): it watches, logs what it
    /// would do and sends one System-channel message per incident, and changes
    /// nothing. Set <c>FeedFailover:DryRun=false</c> to let it stop Dhan and
    /// start FYERS. Chosen by the owner on 27 Sep 2026 so the rule is judged on
    /// live sessions before it is trusted with the feed.
    /// </summary>
    public bool DryRun { get; set; } = true;
}
