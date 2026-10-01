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
    public const string Marker = "\"session\":\"recap\"";

    public const string SpacedMarker = "\"session\": \"recap\"";

    /// <summary>The runs that are not recaps: live trading.</summary>
    public static IQueryable<SimulationRun> WithoutRecaps(this IQueryable<SimulationRun> runs) =>
        runs.Where(r => r.ParametersJson == null || (!r.ParametersJson.Contains(Marker) && !r.ParametersJson.Contains(SpacedMarker)));

    /// <summary>The recap runs alone.</summary>
    public static IQueryable<SimulationRun> OnlyRecaps(this IQueryable<SimulationRun> runs) =>
        runs.Where(r => r.ParametersJson != null && (r.ParametersJson.Contains(Marker) || r.ParametersJson.Contains(SpacedMarker)));
}
