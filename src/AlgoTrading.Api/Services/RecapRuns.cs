using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Recap runs replay a past session through the live machinery: started in
/// the evening, they trade the day that just ended as if it were live (owner,
/// 1 Oct: a good way to test a strategy). Their P&amp;L is a test, not
/// trading, so the run history, the track records, the day's P&amp;L series,
/// the AI tools and the Trade Reviewer leave them out unless asked for them.
/// </summary>
/// <remarks>
/// Until 1 Oct they were counted as live: twelve recaps of 11 Sep, started
/// that evening and on Saturday the 12th, added ₹1.57 lakh to the admin
/// account's Ghost month, and the AI Assistant reported their "best day".
/// Everything that runs a run (the stop pipeline, the close, settlement) still
/// treats a recap like any other run; only what reports results leaves it out.
/// </remarks>
public static class RecapRuns
{
    /// <summary>
    /// How a recap run's stored parameters say so. They are written by
    /// <see cref="LiveRunParameters.Merge"/>, compact JSON; the spaced form is
    /// matched as well in case a row was written by hand.
    /// </summary>
    /// <remarks>
    /// Matched in any case, lower-cased in SQL (<c>lower()</c> on PostgreSQL),
    /// as <see cref="AlgoTrading.Infrastructure.Services.RecapClock"/> and the runner read the value: a run stored
    /// before 1 Oct with "Recap" traded as a test and was counted as live.
    /// <see cref="LiveRunParameters.Merge"/> writes it lower-case since.
    /// </remarks>
    public const string Marker = "\"session\":\"recap\"";

    public const string SpacedMarker = "\"session\": \"recap\"";

    /// <summary>The runs that are not recaps: live trading.</summary>
    public static IQueryable<SimulationRun> WithoutRecaps(this IQueryable<SimulationRun> runs) =>
        runs.Where(r => r.ParametersJson == null
                        || (!r.ParametersJson.ToLower().Contains(Marker) && !r.ParametersJson.ToLower().Contains(SpacedMarker)));

    /// <summary>The recap runs alone.</summary>
    public static IQueryable<SimulationRun> OnlyRecaps(this IQueryable<SimulationRun> runs) =>
        runs.Where(r => r.ParametersJson != null
                        && (r.ParametersJson.ToLower().Contains(Marker) || r.ParametersJson.ToLower().Contains(SpacedMarker)));
}
