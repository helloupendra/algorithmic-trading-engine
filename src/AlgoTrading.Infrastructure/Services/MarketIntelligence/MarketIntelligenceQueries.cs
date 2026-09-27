using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.MarketIntelligence;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services.MarketFactors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Services.MarketIntelligence;

/// <summary>The NSE daily datasets the backfill fills, defined once for the service and the status endpoint.</summary>
public static class MarketIntelligenceDatasets
{
    /// <summary>Where every history backfill starts.</summary>
    public static readonly DateOnly HistoryFrom = new(2020, 1, 1);

    public static DailyDataset Breadth(BreadthRecorder recorder) =>
        new(MarketIntelligenceNames.Breadth, HistoryFrom, recorder.DatesPresentAsync, recorder.FetchDayAsync);

    public static DailyDataset ParticipantOi(MarketFactorsSync sync) =>
        new(MarketIntelligenceNames.ParticipantOi, HistoryFrom, sync.ParticipantDatesPresentAsync, sync.FetchParticipantDayAsync);

    /// <summary>
    /// The latest session whose evening files can exist: today from 18:00 IST,
    /// yesterday before. Missing-session counts stop here, so a day NSE has not
    /// published yet is not counted as missing.
    /// </summary>
    public static DateOnly LatestPublishedDay(DateTime nowUtc)
    {
        var ist = IstTime.ToIst(nowUtc);
        var day = DateOnly.FromDateTime(ist);
        return ist.TimeOfDay >= MarketIntelligenceSchedule.EveningFrom ? day : day.AddDays(-1);
    }
}

/// <summary>
/// The read side of the market-intelligence tables: the lists the future
/// dashboard pages through, and the status the desk checkup reads.
/// </summary>
public sealed class MarketIntelligenceQueries
{
    public const int DefaultTake = 50;
    public const int MaxTake = 500;

    private readonly TradingDbContext _db;
    private readonly MarketIntelligenceStatus _status;
    private readonly DailyBackfillRunner _backfill;
    private readonly BreadthRecorder _breadth;
    private readonly MarketFactorsSync _participants;
    private readonly IMarketSessionService _sessions;
    private readonly MarketIntelligenceOptions _options;

    public MarketIntelligenceQueries(TradingDbContext db, MarketIntelligenceStatus status, DailyBackfillRunner backfill,
        BreadthRecorder breadth, MarketFactorsSync participants, IMarketSessionService sessions, IOptions<MarketIntelligenceOptions> options)
    {
        _db = db;
        _status = status;
        _backfill = backfill;
        _breadth = breadth;
        _participants = participants;
        _sessions = sessions;
        _options = options.Value;
    }

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>Headlines first seen between two instants, newest first; <paramref name="text"/> matches title or summary.</summary>
    public async Task<PagedResponse<NewsHeadlineDto>> NewsAsync(DateTime? fromUtc, DateTime? toUtc, string? category, string? text, int skip, int take, CancellationToken ct)
    {
        var query = _db.NewsItems.AsNoTracking().AsQueryable();
        if (fromUtc is not null) query = query.Where(n => n.FirstSeenUtc >= fromUtc);
        if (toUtc is not null) query = query.Where(n => n.FirstSeenUtc <= toUtc);
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(n => n.Category == category.Trim());
        if (!string.IsNullOrWhiteSpace(text))
        {
            // Case-insensitive substring; EF escapes the text, so % and _ match themselves.
            string needle = text.Trim().ToLowerInvariant();
            query = query.Where(n => n.Title.ToLower().Contains(needle) || n.Summary.ToLower().Contains(needle));
        }

        return await PageAsync(query.OrderByDescending(n => n.FirstSeenUtc).ThenByDescending(n => n.Id), skip, take, n => new NewsHeadlineDto
        {
            Id = n.Id, Source = n.Source, Category = n.Category, Title = n.Title, Summary = n.Summary, Link = n.Link,
            PublishedUtc = n.PublishedUtc, FirstSeenUtc = n.FirstSeenUtc,
            Sentiment = n.Sentiment, Importance = n.Importance, Symbols = n.Symbols, Topics = n.Topics, ScoredUtc = n.ScoredUtc, ScoreModel = n.ScoreModel,
        }, ct);
    }

    /// <summary>Filings broadcast between two instants (first-seen time where NSE gave none), newest first.</summary>
    public async Task<PagedResponse<AnnouncementDto>> AnnouncementsAsync(string? symbol, DateTime? fromUtc, DateTime? toUtc, int skip, int take, CancellationToken ct)
    {
        var query = _db.CorporateAnnouncements.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(symbol)) query = query.Where(a => a.Symbol == symbol.Trim().ToUpperInvariant());
        if (fromUtc is not null) query = query.Where(a => (a.AnnouncedUtc ?? a.FirstSeenUtc) >= fromUtc);
        if (toUtc is not null) query = query.Where(a => (a.AnnouncedUtc ?? a.FirstSeenUtc) <= toUtc);

        return await PageAsync(query.OrderByDescending(a => a.AnnouncedUtc ?? a.FirstSeenUtc).ThenByDescending(a => a.Id), skip, take, a => new AnnouncementDto
        {
            Id = a.Id, Exchange = a.Exchange, Symbol = a.Symbol, Company = a.Company, Subject = a.Subject, Details = a.Details,
            AttachmentUrl = a.AttachmentUrl, AnnouncedUtc = a.AnnouncedUtc, FirstSeenUtc = a.FirstSeenUtc,
            Sentiment = a.Sentiment, Importance = a.Importance, Symbols = a.Symbols, Topics = a.Topics, ScoredUtc = a.ScoredUtc, ScoreModel = a.ScoreModel,
        }, ct);
    }

    /// <summary>Board meetings between two IST dates, soonest first.</summary>
    public async Task<List<CalendarEventDto>> CalendarAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        await _db.CorporateCalendar.AsNoTracking()
            .Where(e => e.EventDate >= from && e.EventDate <= to)
            .OrderBy(e => e.EventDate).ThenBy(e => e.Symbol)
            .Select(e => new CalendarEventDto
            {
                Id = e.Id, Exchange = e.Exchange, Symbol = e.Symbol, Company = e.Company, Purpose = e.Purpose,
                EventDate = e.EventDate, FirstSeenUtc = e.FirstSeenUtc,
            })
            .ToListAsync(ct);

    public async Task<List<GlobalDailyBarDto>> GlobalDailyAsync(string? symbol, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var query = _db.MarketGlobalDaily.AsNoTracking().Where(x => x.Date >= from && x.Date <= to);
        if (!string.IsNullOrWhiteSpace(symbol)) query = query.Where(x => x.Symbol == symbol.Trim().ToUpperInvariant());
        return await query.OrderBy(x => x.Symbol).ThenBy(x => x.Date)
            .Select(x => new GlobalDailyBarDto
            {
                Symbol = x.Symbol, Date = x.Date, Open = x.Open, High = x.High, Low = x.Low, Close = x.Close,
                Volume = x.Volume, Source = x.Source, FetchedUtc = x.FetchedUtc,
            })
            .ToListAsync(ct);
    }

    /// <summary>Every snapshot row taken on one IST date, in time order.</summary>
    public async Task<List<QuoteSnapshotDto>> SnapshotsAsync(string? key, DateOnly istDate, CancellationToken ct)
    {
        var start = IstTime.StartOfDayUtc(istDate);
        var end = IstTime.StartOfDayUtc(istDate.AddDays(1));
        var query = _db.MarketQuoteSnapshots.AsNoTracking().Where(x => x.FetchedUtc >= start && x.FetchedUtc < end);
        if (!string.IsNullOrWhiteSpace(key)) query = query.Where(x => x.Key == key.Trim().ToUpperInvariant());
        return await query.OrderBy(x => x.FetchedUtc).ThenBy(x => x.Key)
            .Select(x => new QuoteSnapshotDto
            {
                Key = x.Key, Price = x.Price, PreviousClose = x.PreviousClose, ChangePct = x.ChangePct,
                AsOfUtc = x.AsOfUtc, FetchedUtc = x.FetchedUtc, Source = x.Source,
            })
            .ToListAsync(ct);
    }

    public async Task<List<BreadthDayDto>> BreadthAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        await _db.MarketBreadthDaily.AsNoTracking()
            .Where(x => x.Exchange == "NSE" && x.Date >= from && x.Date <= to)
            .OrderBy(x => x.Date)
            .Select(x => new BreadthDayDto
            {
                Date = x.Date, Advances = x.Advances, Declines = x.Declines, Unchanged = x.Unchanged, Traded = x.Traded,
                TurnoverCr = x.TurnoverCr, Highs52w = x.Highs52w, Lows52w = x.Lows52w, Source = x.Source,
            })
            .ToListAsync(ct);

    /// <summary>Every recorder and backfill: last success, rows today, last error, and how far each history reaches.</summary>
    public async Task<MarketIntelligenceStatusResponse> StatusAsync(CancellationToken ct)
    {
        var now = Clock();
        var today = IstTime.DateOf(now);
        var todayStartUtc = IstTime.StartOfDayUtc(today);

        var news = await Stamps(_db.NewsItems.AsNoTracking().Select(x => x.FirstSeenUtc), todayStartUtc, ct);
        var announcements = await Stamps(_db.CorporateAnnouncements.AsNoTracking().Select(x => x.FirstSeenUtc), todayStartUtc, ct);
        var calendar = await Stamps(_db.CorporateCalendar.AsNoTracking().Select(x => x.FirstSeenUtc), todayStartUtc, ct);
        var snapshots = await Stamps(_db.MarketQuoteSnapshots.AsNoTracking().Select(x => x.FetchedUtc), todayStartUtc, ct);
        var global = await Stamps(_db.MarketGlobalDaily.AsNoTracking().Select(x => x.FetchedUtc), todayStartUtc, ct);
        int breadthToday = await _db.MarketBreadthDaily.AsNoTracking().CountAsync(x => x.Exchange == "NSE" && x.Date == today, ct);
        var scored = await Stamps(
            _db.NewsItems.AsNoTracking().Where(x => x.ScoredUtc != null).Select(x => x.ScoredUtc!.Value)
                .Concat(_db.CorporateAnnouncements.AsNoTracking().Where(x => x.ScoredUtc != null).Select(x => x.ScoredUtc!.Value)),
            todayStartUtc, ct);

        var response = new MarketIntelligenceStatusResponse { ServerUtc = now, StartedUtc = _status.StartedUtc };
        response.Recorders.Add(Recorder(MarketIntelligenceNames.News, _options.NewsEnabled, "every 5 minutes, all day, every day", news, now));
        response.Recorders.Add(Recorder(MarketIntelligenceNames.Announcements, _options.AnnouncementsEnabled, "every 10 minutes, 06:00-23:30 IST, every day", announcements, now));
        response.Recorders.Add(Recorder(MarketIntelligenceNames.Calendar, _options.AnnouncementsEnabled, "every 4 hours, 06:00-23:30 IST, every day", calendar, now));
        response.Recorders.Add(Recorder(MarketIntelligenceNames.QuoteSnapshots, _options.QuoteSnapshotsEnabled, "every 15 minutes incl. 08:45, 06:00-16:00 IST, weekdays", snapshots, now));
        response.Recorders.Add(Recorder(MarketIntelligenceNames.GlobalDaily, _options.GlobalDailyEnabled, "once a day after 07:00 IST", global, now));
        response.Recorders.Add(Recorder(MarketIntelligenceNames.Breadth, _options.BreadthEnabled, "every 30 minutes, 18:00-23:30 IST, for the last 10 sessions", (breadthToday, null), now));
        response.Recorders.Add(Recorder(MarketIntelligenceNames.NewsScoring, _options.NewsScoringEnabled, "python -m analysis news-score every 10 minutes, 06:00-23:30 IST", scored, now));

        var lastPublished = MarketIntelligenceDatasets.LatestPublishedDay(now);
        foreach (var dataset in new[] { MarketIntelligenceDatasets.Breadth(_breadth), MarketIntelligenceDatasets.ParticipantOi(_participants) })
        {
            var coverage = await _backfill.CoverageAsync(dataset, dataset.Earliest, lastPublished, ct);
            response.Backfills.Add(Backfill(dataset.Name, dataset.Earliest, coverage.FirstDate, coverage.LastDate, coverage.Days, coverage.MissingSessions, coverage.NoFileDays));
        }

        var symbols = await _db.MarketGlobalDaily.AsNoTracking()
            .GroupBy(x => x.Symbol)
            .Select(g => new { Symbol = g.Key, First = g.Min(x => x.Date), Last = g.Max(x => x.Date), Rows = g.Count() })
            .ToListAsync(ct);
        foreach (var market in GlobalMarketKeys.All)
        {
            var row = symbols.FirstOrDefault(s => s.Symbol == market.Key);
            response.GlobalSymbols.Add(new GlobalSymbolCoverageDto
            {
                Symbol = market.Key, SourceSymbol = market.YahooSymbol, Name = market.Name,
                FirstDate = row?.First, LastDate = row?.Last, Rows = row?.Rows ?? 0,
            });
        }

        response.Backfills.Add(Backfill(MarketIntelligenceNames.GlobalDaily, GlobalMarketKeys.HistoryFrom,
            symbols.Count > 0 ? symbols.Min(s => s.First) : null,
            symbols.Count > 0 ? symbols.Max(s => s.Last) : null,
            symbols.Count > 0 ? symbols.Max(s => s.Rows) : 0,
            null, 0));

        return response;
    }

    private RecorderStatusDto Recorder(string name, bool enabled, string schedule, (int Today, DateTime? Latest) rows, DateTime now)
    {
        var health = _status.Recorder(name);
        return new RecorderStatusDto
        {
            Name = name,
            Enabled = enabled,
            Schedule = schedule,
            LastAttemptUtc = health.LastAttemptUtc,
            LastSuccessUtc = health.LastSuccessUtc,
            LastMessage = health.LastMessage,
            LastError = health.LastError,
            LastErrorUtc = health.LastErrorUtc,
            FailingSources = health.FailingSources.Select(kv => $"{kv.Key}: {kv.Value}").OrderBy(x => x).ToList(),
            RowsToday = rows.Today,
            LatestRowUtc = rows.Latest,
            Overdue = enabled && MarketIntelligenceSchedule.IsOverdue(name, now, _status.StartedUtc, health.LastSuccessUtc, IsTradingDay),
        };
    }

    private BackfillStatusDto Backfill(string dataset, DateOnly from, DateOnly? first, DateOnly? last, int days, int? missing, int noFile)
    {
        var progress = _status.Backfill(dataset);
        return new BackfillStatusDto
        {
            Dataset = dataset,
            Enabled = dataset == MarketIntelligenceNames.GlobalDaily ? _options.GlobalDailyEnabled : _options.BackfillEnabled,
            State = progress.State,
            From = from,
            FirstDate = first,
            LastDate = last,
            Days = days,
            MissingSessions = missing,
            NoFileDays = noFile,
            Requested = progress.Requested,
            RemainingAtStart = progress.RemainingAtStart,
            StoredThisRun = progress.StoredThisRun,
            LastStartedUtc = progress.LastStartedUtc,
            LastFinishedUtc = progress.LastFinishedUtc,
            LastMessage = progress.LastMessage,
            LastError = progress.LastError,
        };
    }

    private bool IsTradingDay(DateOnly date) => _sessions.GetSessionInfo(IstTime.MiddayUtc(date), "NSE", "CM").IsTradingDay;

    private static async Task<(int Today, DateTime? Latest)> Stamps(IQueryable<DateTime> stamps, DateTime todayStartUtc, CancellationToken ct) =>
        (await stamps.CountAsync(s => s >= todayStartUtc, ct), await stamps.OrderByDescending(s => s).Select(s => (DateTime?)s).FirstOrDefaultAsync(ct));

    private static async Task<PagedResponse<TDto>> PageAsync<TRow, TDto>(IOrderedQueryable<TRow> query, int skip, int take,
        System.Linq.Expressions.Expression<Func<TRow, TDto>> shape, CancellationToken ct)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, MaxTake);
        return new PagedResponse<TDto>
        {
            Total = await query.CountAsync(ct),
            Skip = skip,
            Take = take,
            Items = await query.Skip(skip).Take(take).Select(shape).ToListAsync(ct),
        };
    }
}
