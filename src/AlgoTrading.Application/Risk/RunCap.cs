namespace AlgoTrading.Application.Risk;

/// <summary>
/// How many strategy runs may be open at once.
/// </summary>
/// <remarks>
/// Zero (or a negative number) means "no cap". The platform limit started at
/// 10, which is a sensible guard for a shared host and a nuisance for the
/// owner running their own desk, so the cap is now opt-in: it applies only
/// when someone has set a positive number.
/// </remarks>
public static class RunCap
{
    /// <summary>True when one more run would breach <paramref name="cap"/>.</summary>
    public static bool Blocks(int cap, int currentOpenRuns) => cap > 0 && currentOpenRuns >= cap;

    /// <summary>The cap for display: null when there is none.</summary>
    public static int? Describe(int cap) => cap > 0 ? cap : null;
}
