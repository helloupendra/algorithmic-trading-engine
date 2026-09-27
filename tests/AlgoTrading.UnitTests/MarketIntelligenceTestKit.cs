using System.Net;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.Enums;
using AlgoTrading.Domain.ValueObjects;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Stand-ins shared by the market-intelligence tests: an in-memory database,
/// the real answers under Fixtures/MarketIntelligence, a web that serves them
/// by URL, and an NSE calendar with chosen holidays.
/// </summary>
/// <remarks>
/// The fixtures are trimmed copies of what each source answered on 27 Sep
/// 2026 (the RSS feeds, NSE's JSON, the archive CSVs, Yahoo's chart JSON):
/// the same bytes, fewer rows. The only hand-written sample is the Atom feed
/// in the parser tests, since none of the desk's feeds is Atom today.
/// </remarks>
internal static class MarketIntelligenceTestKit
{
    public static TradingDbContext Db() =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase($"intel-{Guid.NewGuid():N}").Options);

    public static string FixturePath(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "AlgoTrading.UnitTests", "Fixtures", "MarketIntelligence")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "tests", "AlgoTrading.UnitTests", "Fixtures", "MarketIntelligence", name);
    }

    public static string Fixture(string name) => File.ReadAllText(FixturePath(name));

    public static byte[] Zip(string entryName, string text) => MarketFactorsFixtures.Zip(entryName, text);

    /// <summary>Answers each URL from a table; anything else is a 404. Keeps every URL asked for.</summary>
    public sealed class FakeWeb : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);
        private readonly List<(Func<string, bool> Match, Func<HttpResponseMessage> Answer)> _matchers = [];

        public List<string> Requests { get; } = [];

        public List<HttpRequestMessage> Messages { get; } = [];

        public FakeWeb Serve(string url, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _routes[url] = () => new HttpResponseMessage(status) { Content = new StringContent(body) };
            return this;
        }

        public FakeWeb Serve(string url, byte[] body)
        {
            _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            return this;
        }

        public FakeWeb Serve(string url, Func<HttpResponseMessage> answer)
        {
            _routes[url] = answer;
            return this;
        }

        /// <summary>Answers every URL <paramref name="match"/> accepts that has no exact route; the first match wins.</summary>
        public FakeWeb ServeWhen(Func<string, bool> match, string body)
        {
            _matchers.Add((match, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            Requests.Add(url);
            Messages.Add(request);
            if (_routes.TryGetValue(url, out var answer)) return Task.FromResult(answer());
            var matcher = _matchers.FirstOrDefault(m => m.Match(url));
            return Task.FromResult(matcher.Answer is not null ? matcher.Answer() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    public sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>NSE's calendar with the given full-day holidays and nothing else loaded.</summary>
    public sealed class Calendar(params DateOnly[] holidays) : IMarketCalendar
    {
        public bool IsLoaded => true;

        public MarketHoliday? HolidayOn(string exchange, DateOnly date) =>
            holidays.Contains(date) ? new MarketHoliday { Exchange = exchange, Date = date, Closure = MarketClosure.FullDay, Name = "test holiday" } : null;

        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date) => null;
        public bool HasYear(string exchange, int year) => true;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>Weekdays trade, weekends and the given holidays do not.</summary>
    public sealed class Sessions(params DateOnly[] holidays) : IMarketSessionService
    {
        public MarketSessionInfo GetSessionInfo(DateTime utcNow, string exchange, string segment)
        {
            var day = AlgoTrading.Infrastructure.Services.IstTime.DateOf(utcNow);
            return new MarketSessionInfo
            {
                Exchange = exchange,
                Segment = segment,
                UtcNow = utcNow,
                IsTradingDay = day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidays.Contains(day),
            };
        }

        public bool IsMarketOpen(DateTime utcNow, string exchange, string segment) => false;
        public DateTime GetNextMarketOpenUtc(DateTime utcNow, string exchange, string segment) => utcNow;
    }

    /// <summary>An IST wall-clock time as UTC.</summary>
    public static DateTime Ist(int year, int month, int day, int hour, int minute, int second = 0) =>
        DateTime.SpecifyKind(new DateTime(year, month, day, hour, minute, second) - TimeSpan.FromMinutes(330), DateTimeKind.Utc);
}
