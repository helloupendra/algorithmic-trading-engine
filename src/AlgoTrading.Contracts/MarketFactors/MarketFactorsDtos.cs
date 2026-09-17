namespace AlgoTrading.Contracts.MarketFactors;

/// <summary>How a daily dataset's last fetch went.</summary>
public class MarketFactorsDatasetStatusDto
{
    public string Dataset { get; set; } = string.Empty;
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateOnly? NewestDay { get; set; }
    public string? LastMessage { get; set; }
    public bool LastFailed { get; set; }
}

/// <summary>One side of a day's cash-market figures, in rupees crore.</summary>
public class CashFlowSideDto
{
    public decimal Buy { get; set; }
    public decimal Sell { get; set; }
    public decimal Net { get; set; }
}

public class CashFlowDayDto
{
    public DateOnly Date { get; set; }
    public CashFlowSideDto? Fii { get; set; }
    public CashFlowSideDto? Dii { get; set; }
}

/// <summary>One participant group's derivatives positions on one day, with what a desk reads from them.</summary>
public class ParticipantPositionDto
{
    /// <summary>"FII", "DII", "Pro" or "Client".</summary>
    public string ClientType { get; set; } = string.Empty;

    public long FutureIndexLong { get; set; }
    public long FutureIndexShort { get; set; }

    /// <summary>Index futures long minus short.</summary>
    public long FutureIndexNet { get; set; }

    /// <summary>Long as a share of long + short, in percent.</summary>
    public decimal? FutureIndexLongPercent { get; set; }

    /// <summary>Change of <see cref="FutureIndexNet"/> since the previous stored day; null when that day is missing.</summary>
    public long? FutureIndexNetChange { get; set; }

    public long OptionIndexCallLong { get; set; }
    public long OptionIndexCallShort { get; set; }
    public long OptionIndexPutLong { get; set; }
    public long OptionIndexPutShort { get; set; }
    public long FutureStockLong { get; set; }
    public long FutureStockShort { get; set; }
}

public class ParticipantDayDto
{
    public DateOnly Date { get; set; }
    public List<ParticipantPositionDto> Groups { get; set; } = [];
}

public class MarketFlowsResponse
{
    public DateTime ServerUtc { get; set; }

    /// <summary>Newest first.</summary>
    public List<CashFlowDayDto> Cash { get; set; } = [];

    /// <summary>Newest first.</summary>
    public List<ParticipantDayDto> Participants { get; set; } = [];

    public List<MarketFactorsDatasetStatusDto> Status { get; set; } = [];
}

/// <summary>A futures contract's day, read as a build-up.</summary>
public class FuturesDayDto
{
    public DateOnly Date { get; set; }
    public DateOnly Expiry { get; set; }
    public decimal Close { get; set; }
    public decimal PreviousClose { get; set; }
    public decimal? PriceChangePercent { get; set; }
    public long OpenInterest { get; set; }
    public long OpenInterestChange { get; set; }
    public decimal? OpenInterestChangePercent { get; set; }

    /// <summary>"Long build-up", "Short build-up", "Short covering", "Long unwinding" or "Flat".</summary>
    public string BuildUp { get; set; } = string.Empty;
}

/// <summary>The same reading during the session: the live quote against the last stored close and OI.</summary>
public class FuturesLiveDto
{
    public string Symbol { get; set; } = string.Empty;
    public decimal? LastPrice { get; set; }
    public long? OpenInterest { get; set; }
    public DateTime? AsOfUtc { get; set; }
    public string? SourceKey { get; set; }

    /// <summary>The quote was written within the freshness limit.</summary>
    public bool IsFresh { get; set; }

    public DateOnly? BaselineDate { get; set; }
    public decimal? PriceChangePercent { get; set; }
    public long? OpenInterestChange { get; set; }
    public decimal? OpenInterestChangePercent { get; set; }
    public string? BuildUp { get; set; }
}

public class IndexFuturesDto
{
    public string Underlying { get; set; } = string.Empty;

    /// <summary>The nearest contract's newest stored day.</summary>
    public FuturesDayDto? Latest { get; set; }

    /// <summary>The nearest contract on each stored day, newest first (rolls to the next contract after an expiry).</summary>
    public List<FuturesDayDto> History { get; set; } = [];

    public FuturesLiveDto? Live { get; set; }
}

public class StockBuildUpDto
{
    public string Underlying { get; set; } = string.Empty;
    public decimal? PriceChangePercent { get; set; }
    public decimal? OpenInterestChangePercent { get; set; }
    public decimal Close { get; set; }
}

public class MarketFuturesResponse
{
    public DateTime ServerUtc { get; set; }
    public DateOnly? LatestDay { get; set; }
    public List<IndexFuturesDto> Indices { get; set; } = [];

    /// <summary>Near-month stock futures on the latest day, by build-up, largest OI change first (ten each).</summary>
    public Dictionary<string, List<StockBuildUpDto>> Stocks { get; set; } = [];

    /// <summary>How many near-month stock futures fell in each build-up on the latest day.</summary>
    public Dictionary<string, int> StockCounts { get; set; } = [];

    public List<MarketFactorsDatasetStatusDto> Status { get; set; } = [];
}

public class GlobalCueDto
{
    public string Symbol { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string? Currency { get; set; }
    public decimal? LastPrice { get; set; }
    public decimal? PreviousClose { get; set; }
    public decimal? Change { get; set; }
    public decimal? ChangePercent { get; set; }
    public DateTime? AsOfUtc { get; set; }
    public string? Error { get; set; }
}

public class GlobalCuesResponse
{
    public DateTime ServerUtc { get; set; }
    public DateTime FetchedUtc { get; set; }
    public GlobalCueDto Gift { get; set; } = new();
    public DateOnly? GiftExpiry { get; set; }
    public long? GiftContractsTraded { get; set; }

    /// <summary>NSE's NIFTY future of the same expiry at its last stored close, for the gap GIFT Nifty points to.</summary>
    public decimal? NseFutureClose { get; set; }
    public DateOnly? NseFutureCloseDate { get; set; }
    public decimal? IndicatedGapPoints { get; set; }
    public decimal? IndicatedGapPercent { get; set; }

    public List<GlobalCueDto> Markets { get; set; } = [];
    public string SourceNote { get; set; } = string.Empty;
}

public class MarketEventDto
{
    /// <summary>The stored row's id; null for a derived line (holiday, expiry).</summary>
    public long? Id { get; set; }
    public DateOnly Date { get; set; }

    /// <summary>"HH:mm" IST, when scheduled.</summary>
    public string? TimeIst { get; set; }

    public string Region { get; set; } = "IN";
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int Importance { get; set; }
    public string? Notes { get; set; }
    public string? Source { get; set; }

    /// <summary>"event", "holiday" or "expiry".</summary>
    public string Kind { get; set; } = "event";
}

public class MarketEventsResponse
{
    public DateTime ServerUtc { get; set; }
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public List<MarketEventDto> Events { get; set; } = [];
}

public class SaveMarketEventRequest
{
    public DateOnly Date { get; set; }
    public string? TimeIst { get; set; }
    public string Region { get; set; } = "IN";
    public string Category { get; set; } = "Other";
    public string Title { get; set; } = string.Empty;
    public int Importance { get; set; } = 2;
    public string? Notes { get; set; }
    public string? Source { get; set; }
}

public class MarketFactorsSyncResponse
{
    public List<string> Lines { get; set; } = [];
    public List<MarketFactorsDatasetStatusDto> Status { get; set; } = [];
}
