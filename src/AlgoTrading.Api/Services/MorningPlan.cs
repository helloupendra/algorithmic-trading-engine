// src/AlgoTrading.Api/Services/MorningPlan.cs
using System.Globalization;

namespace AlgoTrading.Api.Services;

/// <summary>
/// The morning plan, <c>config/morning-plan.txt</c>: which accounts get it,
/// and one line per strategy.
/// </summary>
/// <remarks>
/// <para>
/// Three readers now, and they must agree on what a line means:
/// <c>scripts/market-open.sh</c> deploys from it at 08:45 IST
/// (<c>parse_plan_line</c>), Sentinel checks it is running
/// (<c>sentinel/context.py</c>, <c>load_plan</c>), and the Desk shows it. The
/// grammar, from the file's own header:
/// </para>
/// <code>
/// accounts: admin coderforchange
/// Strategy  UNDERLYING[,UNDERLYING...]  lots  [legTargetPoints | -]  [@account[,account]]
/// </code>
/// <para>
/// A <c>#</c> starts a comment, to the end of the line. Lots left off (or not
/// a whole number) are the job's default, 2. The leg target is a number of
/// premium points, <c>-</c> for none, or left off for the job's default. An
/// <c>@</c> list limits the line to the accounts it names, any case; without
/// one the line runs in every account. Where the two scripts would read a
/// line differently, or the deploy would refuse it, the line is still read as
/// Sentinel reads it and a warning says why.
/// </para>
/// </remarks>
public sealed record MorningPlan(
    IReadOnlyList<string> Accounts,
    IReadOnlyList<MorningPlan.Line> Lines,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Lots for a line that leaves them out: MARKET_OPEN_LOTS's default in market-open.sh.</summary>
    public const int DefaultLots = 2;

    public const string LegTargetDefault = "default";
    public const string LegTargetNone = "none";
    public const string LegTargetPoints = "points";

    /// <summary>One strategy line.</summary>
    /// <param name="Number">1-based line number in the file.</param>
    /// <param name="Text">The line as written, comment removed.</param>
    /// <param name="LegTarget">One of <see cref="LegTargetDefault"/>, <see cref="LegTargetNone"/>, <see cref="LegTargetPoints"/>.</param>
    /// <param name="OnlyAccounts">The @ list; empty means every account.</param>
    public sealed record Line(
        int Number,
        string Text,
        string Strategy,
        IReadOnlyList<string> Underlyings,
        int Lots,
        string LegTarget,
        decimal? LegTargetPoints,
        IReadOnlyList<string> OnlyAccounts)
    {
        public bool AppliesTo(string account)
            => OnlyAccounts.Count == 0 || OnlyAccounts.Contains(account, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>One run the plan asks for.</summary>
    public sealed record ExpectedRun(string Account, string Strategy, string Underlying, int Lots);

    /// <summary>
    /// Every run the plan asks for: each account in order, each line that
    /// applies to it, each underlying — the "account|strategy|underlying" list
    /// market-open.sh hands its tally, without repeats.
    /// </summary>
    public IReadOnlyList<ExpectedRun> ExpectedRuns()
    {
        var seen = new HashSet<(string, string, string)>();
        var runs = new List<ExpectedRun>();
        foreach (var account in Accounts)
        {
            foreach (var line in Lines.Where(l => l.AppliesTo(account)))
            {
                foreach (var underlying in line.Underlyings)
                {
                    var key = (account.ToLowerInvariant(), line.Strategy.ToLowerInvariant(), underlying.ToUpperInvariant());
                    if (seen.Add(key)) runs.Add(new ExpectedRun(account, line.Strategy, underlying, line.Lots));
                }
            }
        }
        return runs;
    }

    public static MorningPlan Parse(string text)
    {
        var accounts = new List<string>();
        var lines = new List<Line>();
        var warnings = new List<string>();
        bool haveAccounts = false;

        var raw = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < raw.Length; i++)
        {
            int number = i + 1;
            int hash = raw[i].IndexOf('#');
            string line = (hash >= 0 ? raw[i][..hash] : raw[i]).Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("accounts:", StringComparison.OrdinalIgnoreCase))
            {
                // market-open.sh takes the first accounts line (head -1).
                if (haveAccounts)
                {
                    warnings.Add($"Line {number}: a second 'accounts:' line; the first one is used.");
                    continue;
                }
                haveAccounts = true;
                accounts.AddRange(line["accounts:".Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                continue;
            }

            if (hash > 0)
            {
                // Sentinel cuts the comment off; market-open.sh drops only
                // whole-line comments and would read these words as fields.
                warnings.Add($"Line {number}: a comment after the line; the morning job reads it as part of the line. Put it on a line of its own.");
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                warnings.Add($"Line {number}: '{line}' names no underlying; nothing runs from it.");
                continue;
            }

            var underlyings = parts[1]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(u => u.ToUpperInvariant())
                .ToList();

            int lots = DefaultLots;
            if (parts.Length > 2)
            {
                if (parts[2].All(char.IsAsciiDigit) && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                {
                    lots = n;
                }
                else
                {
                    warnings.Add($"Line {number}: lots '{parts[2]}' is not a whole number; read as {DefaultLots}, but the morning job would send it as written and the start would be refused.");
                }
            }

            string legTarget = LegTargetDefault;
            decimal? legTargetPoints = null;
            IReadOnlyList<string> only = Array.Empty<string>();
            foreach (var field in parts.Skip(3))
            {
                if (field.StartsWith('@'))
                {
                    only = field[1..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                }
                else if (field == "-")
                {
                    (legTarget, legTargetPoints) = (LegTargetNone, null);
                }
                else if (decimal.TryParse(field, NumberStyles.Number, CultureInfo.InvariantCulture, out var points))
                {
                    (legTarget, legTargetPoints) = (LegTargetPoints, points);
                }
                else
                {
                    warnings.Add($"Line {number}: leg target '{field}' is neither a number of points nor '-'.");
                }
            }

            lines.Add(new Line(number, line, parts[0], underlyings, lots, legTarget, legTargetPoints, only));
        }

        if (!haveAccounts || accounts.Count == 0)
        {
            warnings.Add("No 'accounts:' line: the morning job refuses to start anything without one.");
        }

        foreach (var line in lines)
        {
            foreach (var name in line.OnlyAccounts.Where(a => !accounts.Contains(a, StringComparer.OrdinalIgnoreCase)))
            {
                warnings.Add($"Line {line.Number}: @{name} is not in the accounts line, so {line.Strategy} does not run there.");
            }
        }

        return new MorningPlan(accounts, lines, warnings);
    }
}
