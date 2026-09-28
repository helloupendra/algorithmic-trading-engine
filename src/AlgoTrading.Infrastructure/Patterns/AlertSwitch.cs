using Microsoft.Extensions.Configuration;

namespace AlgoTrading.Infrastructure.Patterns;

/// <summary>
/// Reads the scanners' on/off settings (<c>PatternAlerts:Enabled</c>,
/// <c>IndicatorAlerts:Enabled</c>) without letting a spelling take the API down.
/// </summary>
/// <remarks>
/// They were read with <c>GetValue&lt;bool&gt;</c>, which throws on anything
/// but true or false. An operator borrowing the indicator file's own wording
/// (<c>telegram: off</c>) and setting <c>IndicatorAlerts__Enabled=off</c> in
/// .env made the scanner's ExecuteAsync throw before its first scan, and a
/// background service that throws stops the host: the whole API, orders and
/// runs with it, not just the scanner.
/// </remarks>
public static class AlertSwitch
{
    /// <summary>true/on/yes/1 or false/off/no/0, any case; null for anything else.</summary>
    public static bool? Parse(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "true" or "on" or "yes" or "1" => true,
        "false" or "off" or "no" or "0" => false,
        _ => null,
    };

    /// <summary>
    /// The first of <paramref name="keys"/> that is set decides, and none set
    /// is <paramref name="fallback"/>. A value that is neither on nor off is
    /// off, and <paramref name="problem"/> says so, for the log and the page.
    /// </summary>
    public static bool Read(IConfiguration configuration, bool fallback, out string? problem, params string[] keys)
    {
        problem = null;
        foreach (var key in keys)
        {
            var raw = configuration[key];
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (Parse(raw) is { } on) return on;

            problem = $"{key} is \"{raw}\", which is neither on nor off (true/false, on/off, yes/no, 1/0); treated as off.";
            return false;
        }

        return fallback;
    }
}
