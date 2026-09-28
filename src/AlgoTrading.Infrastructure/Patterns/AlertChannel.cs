using AlgoTrading.Infrastructure.Services;
using Microsoft.Extensions.Configuration;

namespace AlgoTrading.Infrastructure.Patterns;

/// <summary>
/// Which Telegram channel the market alerts go to: <c>PatternAlerts:TelegramChannel</c>
/// for the candle patterns, and <c>IndicatorAlerts:TelegramChannel</c> for the
/// indicator alerts, which follow the patterns' setting when it is not set.
/// "trades" (the default) or "system", any case.
/// </summary>
/// <remarks>
/// On 27 Sep 2026 the owner put the patterns in the Desk System channel ("market
/// information, not trades"); on 28 Sep he moved them to the channel the live
/// trade alerts go to. A setting rather than a constant, so the next change is
/// a line in .env (<c>PatternAlerts__TelegramChannel=system</c>), not a build.
/// Read like <see cref="AlertSwitch"/>: a spelling nobody anticipated must not
/// throw in a background service and stop the host, so it means the trades
/// channel and <c>problem</c> says so.
/// </remarks>
public static class AlertChannel
{
    public const string PatternsKey = "PatternAlerts:TelegramChannel";
    public const string IndicatorsKey = "IndicatorAlerts:TelegramChannel";

    /// <summary>The trades channel, where the owner wants the market alerts since 28 Sep 2026.</summary>
    public const TelegramChannel Default = TelegramChannel.Trades;

    /// <summary>"trades" or "system", any case, spaces around ignored; null for anything else.</summary>
    public static TelegramChannel? Parse(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "trades" => TelegramChannel.Trades,
        "system" => TelegramChannel.System,
        _ => null,
    };

    /// <summary>
    /// The first of <paramref name="keys"/> that is set decides, and none set
    /// is <see cref="Default"/>. A value that is neither channel is the trades
    /// channel, and <paramref name="problem"/> says so, for the log and the page.
    /// </summary>
    public static TelegramChannel Read(IConfiguration configuration, out string? problem, params string[] keys)
    {
        problem = null;
        foreach (var key in keys)
        {
            var raw = configuration[key];
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (Parse(raw) is { } channel) return channel;

            problem = $"{key} is \"{raw}\", which is neither trades nor system; sent to the trades channel.";
            return Default;
        }

        return Default;
    }

    /// <summary>The candle patterns' channel: <c>PatternAlerts:TelegramChannel</c>.</summary>
    public static TelegramChannel ForPatterns(IConfiguration configuration, out string? problem) =>
        Read(configuration, out problem, PatternsKey);

    /// <summary>The indicator alerts' channel: <c>IndicatorAlerts:TelegramChannel</c>, else the patterns'.</summary>
    public static TelegramChannel ForIndicators(IConfiguration configuration, out string? problem) =>
        Read(configuration, out problem, IndicatorsKey, PatternsKey);

    /// <summary>"trades" or "system": how the API names a channel to the console.</summary>
    public static string Name(TelegramChannel channel) => channel == TelegramChannel.System ? "system" : "trades";
}
