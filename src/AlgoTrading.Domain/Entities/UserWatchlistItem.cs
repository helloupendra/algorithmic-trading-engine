namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One symbol on one trader's personal watchlist.
/// </summary>
/// <remarks>
/// Separate from <see cref="LiveWatchlistItem"/> on purpose. That one is the
/// <em>ingestor's subscription list</em>: what the live feed connects to, and
/// what every running strategy depends on. A trader removing a symbol from their
/// own view must never unsubscribe the feed — that would silently starve
/// somebody else's live run of data.
/// <para>
/// So: adding here also ensures the ingestor subscribes (otherwise no quotes
/// would arrive), but removing here removes only this row.
/// </para>
/// </remarks>
public class UserWatchlistItem
{
    public long Id { get; set; }

    public long UserId { get; set; }

    public string Symbol { get; set; } = string.Empty;

    /// <summary>Lower sorts first, so a trader can keep their indices on top.</summary>
    public int SortOrder { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public AppUser? User { get; set; }
}
