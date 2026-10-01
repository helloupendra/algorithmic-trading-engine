using AlgoTrading.Contracts.LiveData;

namespace AlgoTrading.Application.Interfaces;

/// <summary>
/// The prices of a market replay: the quotes and the minute in progress of a
/// past session the desk is playing again, held apart from the live ones.
/// </summary>
/// <remarks>
/// <para>
/// <c>live_quotes_latest</c> prices every live fill, mark, risk check, option
/// chain and pulse. A replay that wrote a past day's prices there would mark
/// live positions at that day's prices, and could fire the stop of a position
/// carried overnight. So a replay's prices live here, in the API's memory, and
/// only the recap runs of the replayed day read them: their fills, marks, risk
/// guard, P&amp;L, clock, and the runner's own quote and bar calls.
/// </para>
/// <para>
/// Lost when the API restarts. The player sends every tick again as it plays,
/// so the quotes are back within a second; the minute in progress restarts
/// from the next tick.
/// </para>
/// </remarks>
public interface IMarketReplayBook
{
    /// <summary>The replayed IST day while a replay is on; null otherwise.</summary>
    DateOnly? Day { get; }

    /// <summary>The newest exchange time applied: the replay's clock.</summary>
    DateTime? ClockUtc { get; }

    /// <summary>Starts a replay of <paramref name="day"/>, empty.</summary>
    void Begin(DateOnly day);

    /// <summary>Ends the replay and forgets its prices.</summary>
    void End();

    /// <summary>Applies a batch of replayed ticks; answers how many were taken (with a price, stamped no later than the replayed day).</summary>
    int Apply(IReadOnlyList<UpsertLiveTickRequest> ticks);

    LiveQuoteResponse? Quote(string symbol);

    IReadOnlyList<LiveQuoteResponse> AllQuotes();

    /// <summary>The 1m bar the replay is in the middle of for <paramref name="symbol"/>, built from its ticks so far.</summary>
    LiveBarResponse? CurrentMinute(string symbol);

    /// <summary>
    /// Whether a run with these parameters is priced from the replay: a recap
    /// run of the day being replayed. Every other run, recaps of other days
    /// included, reads the live prices as before.
    /// </summary>
    bool Prices(string? parametersJson);
}
