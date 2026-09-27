namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One live run's P&amp;L at one minute: the figures its card showed then.
/// </summary>
/// <remarks>
/// <para>
/// Until 28 Sep a live run kept no P&amp;L over time. Its card and the history
/// could say where a run stood, never how it got there, so the Desk's "Day
/// P&amp;L" was a single number. The API's minute recorder writes one row per
/// live run per minute (Running or Stopping, manual books included), and one
/// last row at the minute a run ended, with its final figures.
/// </para>
/// <para>
/// The figures are the ones the run card and the run history show, from the
/// same code (<c>RunPnl</c> in the API): realized over every position,
/// unrealized over the open legs marked at the latest live quote, the
/// statutory charges of every fill so far, and
/// <see cref="Net"/> = realized + unrealized − charges. Rupees, to the paisa.
/// </para>
/// <para>
/// (<see cref="SimulationRunId"/>, <see cref="AtUtc"/>) is unique: a minute
/// written twice (a run that ended in the minute it was sampled, a restart
/// inside a minute) is updated, never duplicated.
/// </para>
/// </remarks>
public class RunPnlMinute
{
    public long Id { get; set; }

    public long SimulationRunId { get; set; }

    /// <summary>The minute, UTC, truncated to the minute (seconds and below are zero).</summary>
    public DateTime AtUtc { get; set; }

    /// <summary>Booked P&amp;L of every position of the run, before charges.</summary>
    public decimal Realized { get; set; }

    /// <summary>The open legs at the latest live quote (their stored mark when no quote is known); 0 once the run has ended.</summary>
    public decimal Unrealized { get; set; }

    /// <summary>Statutory charges of the run's fills so far.</summary>
    public decimal Charges { get; set; }

    /// <summary>Realized + unrealized − charges: the run card's net.</summary>
    public decimal Net { get; set; }
}
