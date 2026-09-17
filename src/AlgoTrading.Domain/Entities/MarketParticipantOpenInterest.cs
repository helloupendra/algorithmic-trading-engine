namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One participant group's open positions in NSE equity derivatives at the end
/// of a trading day, as NSE publishes them in its participant-wise open interest
/// file (<c>fao_participant_oi_DDMMYYYY.csv</c>).
/// </summary>
/// <remarks>
/// Counts are contracts, not lots or rupees. The file has one row each for
/// Client, DII, FII and Pro, and a TOTAL row where every long column equals its
/// short column; that row is kept too, because it is how a parse is checked.
/// Desks read the FII index-futures long/short split as the big money's
/// positioning; this table is what the Market factors page draws that from.
/// </remarks>
public class MarketParticipantOpenInterest
{
    public long Id { get; set; }

    /// <summary>The trading day the file reports, in IST.</summary>
    public DateOnly Date { get; set; }

    /// <summary>"Client", "DII", "FII", "Pro" or "TOTAL", as NSE names them.</summary>
    public string ClientType { get; set; } = string.Empty;

    public long FutureIndexLong { get; set; }
    public long FutureIndexShort { get; set; }
    public long FutureStockLong { get; set; }
    public long FutureStockShort { get; set; }
    public long OptionIndexCallLong { get; set; }
    public long OptionIndexPutLong { get; set; }
    public long OptionIndexCallShort { get; set; }
    public long OptionIndexPutShort { get; set; }
    public long OptionStockCallLong { get; set; }
    public long OptionStockPutLong { get; set; }
    public long OptionStockCallShort { get; set; }
    public long OptionStockPutShort { get; set; }
    public long TotalLong { get; set; }
    public long TotalShort { get; set; }

    /// <summary>The URL the row was read from.</summary>
    public string Source { get; set; } = string.Empty;

    public DateTime FetchedUtc { get; set; } = DateTime.UtcNow;
}
