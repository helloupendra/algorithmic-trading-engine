using System.Text.RegularExpressions;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.Api.Services.AiTools;

/// <summary>How the desk tools write times, money and free text for a model.</summary>
internal static partial class AiToolFormat
{
    /// <summary>An instant as an IST wall-clock time, "2026-09-30 10:05".</summary>
    public static string? Ist(DateTime? utc) =>
        utc is DateTime at ? IstTime.ToIst(DateTime.SpecifyKind(at, DateTimeKind.Utc)).ToString("yyyy-MM-dd HH:mm") : null;

    /// <summary>
    /// An instant to the second, "2026-09-30 10:05:07": a run's legs, orders
    /// and signals. To the minute, a roll's close and the next open looked
    /// simultaneous, and the reviewer read run 317 as holding two groups.
    /// </summary>
    public static string? IstSeconds(DateTime? utc) =>
        utc is DateTime at ? IstTime.ToIst(DateTime.SpecifyKind(at, DateTimeKind.Utc)).ToString("yyyy-MM-dd HH:mm:ss") : null;

    /// <summary>Rupees to the paisa.</summary>
    public static decimal Rs(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static decimal? Rs(decimal? value) => value is decimal v ? Rs(v) : null;

    /// <summary>
    /// Free text from the desk (a stop reason, an incident's evidence, a
    /// checkup item) with anything secret-shaped masked, the server's name,
    /// addresses and file paths taken out, and cut to <paramref name="max"/>
    /// characters. It is leaving the server.
    /// </summary>
    public static string? Text(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string masked = Hosts(IncidentRedaction.Mask(text.Trim()));
        return masked.Length <= max ? masked : masked[..(max - 1)] + "…";
    }

    /// <summary>
    /// The machine the desk runs on, as its texts name it: the host name
    /// (EC2's ip-a-b-c-d form and this machine's own), IPv4 addresses, and
    /// absolute paths under a home directory.
    /// </summary>
    public static string Hosts(string text)
    {
        string machine = Environment.MachineName;
        if (machine.Length >= 4) text = text.Replace(machine, "[host]", StringComparison.OrdinalIgnoreCase);
        text = Ec2Host().Replace(text, "[host]");
        text = Ipv4().Replace(text, "[ip]");
        return HomePath().Replace(text, "[path]");
    }

    [GeneratedRegex(@"\bip-\d{1,3}(?:-\d{1,3}){3}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Ec2Host();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"(?:/home|/Users|/root)/[^\s""'`,;)]+")]
    private static partial Regex HomePath();
}
