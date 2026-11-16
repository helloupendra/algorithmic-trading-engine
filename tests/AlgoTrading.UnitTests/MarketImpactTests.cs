using System.Text.Json.Nodes;
using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Api.Services.AiVoice;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.MarketData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The big news (owner, 16 Nov 2026): the Market Impact Analyst rates every new headline for how much it can move
/// NIFTY, code checks each record, and Ira tells the owners each new 2 or 3 in Hinglish, any hour, with the market now.
/// </summary>
public sealed class MarketImpactTests
{
    private const string Analyst1 = "moonshotai/kimi-k3";

    private static readonly HashSet<string> NoneTold = new(StringComparer.OrdinalIgnoreCase);

    // Monday 2 Nov 2026, 10:05 IST.
    private static readonly DateTime Seen = new(2026, 11, 2, 4, 35, 0, DateTimeKind.Utc);

    private const string Text = "Oil tumbles $6 as Iran signals peace talks\nBrent crude dropped more than $6 a barrel on Monday.";

    private static JsonObject Big(string what = "Iran ne peace talks ka ishaara diya, Brent $6 gira.", string? repeatOf = null,
        string channel = "oil", string nifty = "up", object? impact = null) => new()
    {
        ["id"] = "n1",
        ["impact"] = impact is string s ? s : JsonValue.Create((int?)impact ?? 3),
        ["scope"] = "global",
        ["channels"] = new JsonArray(channel, "geopolitics"),
        ["nifty"] = nifty,
        ["volatility"] = "higher",
        ["horizon"] = "day",
        ["confidence"] = 0.7,
        ["repeatOf"] = repeatOf,
        ["what"] = what,
        ["why"] = "Tel sasta hone se India ka import bill aur mehngai ghategi.",
        ["watch"] = "Deal sign hoti hai ya nahi.",
    };

    private static (string Status, string Title, string Body, string Data, string Error) Check(JsonObject record, IReadOnlySet<string>? told = null) =>
        MarketImpactAgent.Check(record, Text, told ?? NoneTold, "Reuters", "Oil tumbles $6 as Iran signals peace talks", Seen);

    // ======================================================== the check

    [Fact]
    public void A_big_record_with_every_field_from_its_list_and_no_made_up_number_is_good()
    {
        var (status, title, body, data, error) = Check(Big());

        Assert.True(status == AiReportStatus.Ok, error);
        Assert.Equal("Iran ne peace talks ka ishaara diya, Brent $6 gira.", title);
        var f = MarketImpactAgent.ReadFacts(data)!;
        Assert.Equal((3, "global", "up", "higher", "day"), (f.Impact, f.Scope, f.Nifty, f.Volatility, f.Horizon));
        Assert.Equal(["oil", "geopolitics"], f.Channels);
        Assert.Equal(("Reuters", Seen), (f.Source, f.FirstSeenUtc));
        Assert.Null(f.RepeatOf);
        Assert.Contains("- Kya hua: Iran ne peace talks", body);
    }

    [Fact]
    public void A_number_the_headline_does_not_state_keeps_the_record_from_the_owner()
    {
        var (status, _, _, _, error) = Check(Big(what: "Brent 8% gira, NIFTY 50 aur FY27 ki kamaai pe asar."));

        Assert.Equal(AiReportStatus.Invalid, status);
        Assert.Equal("what states 8, which is not in the headline", error);
    }

    [Fact]
    public void Every_field_must_come_from_its_list()
    {
        Assert.Contains("channel \"weather\" is not one of the listed channels", Check(Big(channel: "weather")).Error);
        Assert.Contains("nifty \"sideways\" is not up, down, mixed or unclear", Check(Big(nifty: "sideways")).Error);
        Assert.Contains("impact is not a whole number from 0 to 3", Check(Big(impact: 4)).Error);
        // A number written as text is the same answer.
        Assert.Equal(AiReportStatus.Ok, Check(Big(impact: "2")).Status);
    }

    [Fact]
    public void A_repeat_must_name_a_story_it_was_shown()
    {
        var told = new HashSet<string>(["t9"], StringComparer.OrdinalIgnoreCase);

        var repeat = Check(Big(repeatOf: "t9"), told);
        Assert.Equal(AiReportStatus.Ok, repeat.Status);
        Assert.Equal("t9", MarketImpactAgent.ReadFacts(repeat.Data)!.RepeatOf);
        Assert.Contains("repeatOf \"t5\" is not one of the stories it was shown", Check(Big(repeatOf: "t5"), told).Error);
    }

    [Fact]
    public void A_small_item_needs_only_its_impact()
    {
        var (status, title, _, data, error) = Check(new JsonObject { ["id"] = "n1", ["impact"] = 1 });

        Assert.True(status == AiReportStatus.Ok, error);
        Assert.Equal("Oil tumbles $6 as Iran signals peace talks", title);
        Assert.Equal((1, string.Empty), (MarketImpactAgent.ReadFacts(data)!.Impact, MarketImpactAgent.ReadFacts(data)!.What));
    }

    [Fact]
    public void The_stocks_and_sectors_named_are_kept_when_listed_and_of_a_known_effect()
    {
        var record = Big();
        record["stocks"] = JsonNode.Parse("""[{"symbol":"NSE:ONGC-EQ","effect":"down"},{"symbol":"indigo","effect":"up"},{"symbol":"MADEUP","effect":"up"},{"symbol":"BPCL","effect":"sideways"}]""");
        record["sectors"] = JsonNode.Parse("""[{"name":"Aviation","effect":"up"},{"name":"weather","effect":"down"},{"name":"oil-gas","effect":"down"}]""");
        var listed = new HashSet<string>(["ONGC", "INDIGO", "BPCL"], StringComparer.Ordinal);

        var (status, _, body, data, error) = MarketImpactAgent.Check(record, Text, NoneTold, "Reuters", "Oil tumbles", Seen, listed);

        Assert.True(status == AiReportStatus.Ok, error);
        var f = MarketImpactAgent.ReadFacts(data)!;
        // MADEUP is not listed, BPCL's effect is not one of up/down/mixed, "weather" is not a sector: each dropped quietly.
        Assert.Equal([("ONGC", "down"), ("INDIGO", "up")], f.Stocks);
        Assert.Equal([("aviation", "up"), ("oil-gas", "down")], f.Sectors);
        Assert.Contains("Asar: ONGC down, INDIGO up, aviation up, oil-gas down", body);
        Assert.Contains("Stocks: ONGC neeche, INDIGO upar. Sector: aviation upar, oil-gas neeche.", IraAlertText.News(f, new IraMarketNow(null, null)));
    }

    [Fact]
    public void A_small_item_may_repeat_a_told_story_and_a_name_it_was_not_shown_is_dropped()
    {
        var told = new HashSet<string>(["t9"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal("t9", MarketImpactAgent.ReadFacts(Check(new JsonObject { ["id"] = "n1", ["impact"] = 1, ["repeatOf"] = "t9" }, told).Data)!.RepeatOf);
        var unknown = Check(new JsonObject { ["id"] = "n1", ["impact"] = 0, ["repeatOf"] = "t5" }, told);
        Assert.Equal(AiReportStatus.Ok, unknown.Status);
        Assert.Null(MarketImpactAgent.ReadFacts(unknown.Data)!.RepeatOf);
    }

    [Fact]
    public async Task A_story_four_publishers_carry_gets_one_tally_update_feeds_of_one_publisher_counting_once()
    {
        var now = Seen.AddMinutes(30);
        using var db = NewDb();
        Report(db, 1, now.AddMinutes(-25), Big());
        var told = new[] { "t1" };
        foreach (var (id, source) in new[] { (2L, "CNBC · World markets"), (3L, "CNBC · Asia"), (4L, "Mint · Markets"), (5L, "Al Jazeera") })
        {
            ReportFrom(db, id, now.AddMinutes(-10), new JsonObject { ["id"] = "n1", ["impact"] = 1, ["repeatOf"] = "t1" }, source, told);
        }

        await db.SaveChangesAsync();
        var marks = new IraMarks();
        var desk = Desk(db, null);

        // Reuters, CNBC, Mint: three publishers (CNBC's two feeds count once). Not yet.
        db.AiReports.Remove(await db.AiReports.SingleAsync(r => r.Id == 5));
        await db.SaveChangesAsync();
        Assert.DoesNotContain(await desk.EventsAsync(marks, now, CancellationToken.None), e => e.Key.StartsWith("newsTally:"));

        // Al Jazeera makes four: one update, then never again.
        ReportFrom(db, 6, now.AddMinutes(-5), new JsonObject { ["id"] = "n1", ["impact"] = 1, ["repeatOf"] = "t1" }, "Al Jazeera", told);
        await db.SaveChangesAsync();
        var tally = Assert.Single(await desk.EventsAsync(marks, now.AddMinutes(1), CancellationToken.None), e => e.Key == "newsTally:1");
        Assert.Equal(IraDesk.NewsKind, tally.Kind);
        Assert.Equal("Update: yeh khabar ab 4 jagah hai (Reuters, CNBC, Mint, Al Jazeera): Iran ne peace talks ka ishaara diya, Brent $6 gira. " +
                     "Jitni zyaada jagah, utni badi khabar.", tally.Text);
        Assert.DoesNotContain(await desk.EventsAsync(marks, now.AddMinutes(2), CancellationToken.None), e => e.Key.StartsWith("newsTally:"));
    }

    [Fact]
    public async Task The_mausam_card_reads_the_day_s_forecasts_events_and_the_night_s_big_news()
    {
        // Tuesday 3 Nov 2026, 09:05 IST.
        var now = new DateTime(2026, 11, 3, 3, 35, 0, DateTimeKind.Utc);
        var since = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);
        using var db = NewDb();
        db.Forecasts.AddRange(
            new Forecast { ModelKey = "range.har-vix", Target = "range", Underlying = "NIFTY", SessionDate = new DateOnly(2026, 11, 3), IssuedUtc = now.AddMinutes(-15),
                PredictionJson = """{"median":1.42,"low80":0.9,"high80":2.1}""", BaselineJson = """{"median":1.10}""" },
            new Forecast { ModelKey = "trend.logit", Target = "trend", Underlying = "NIFTY", SessionDate = new DateOnly(2026, 11, 3), IssuedUtc = now.AddMinutes(-15),
                PredictionJson = """{"p":0.38}""", BaselineJson = """{"p":0.30}""" });
        Report(db, 1, now.AddHours(-3), Big(), firstSeen: now.AddHours(-3));
        Report(db, 2, now.AddHours(-2), Big(repeatOf: "t1"), firstSeen: now.AddHours(-2), told: ["t1"]);
        await db.SaveChangesAsync();

        var m = await Desk(db, null).MausamAsync(now, since, CancellationToken.None);
        string card = IraAlertText.Mausam(m, new DateOnly(2026, 11, 3));

        Assert.Equal((1.42m, 1.10m, 1), (m.RangeMedian!.Value, m.RangeBaseline!.Value, m.BigNews));
        Assert.StartsWith("Aaj ka mausam (Tue 3 Nov): TEZ. NIFTY ki range ka andaaza 1.42% (aam 1.10%), trend din ka chance 38% (aam 30%). " +
                          "Strategy: TrendRider default", card);
        Assert.Contains("Raat ki badi khabrein: 1, sabse badi: Iran ne peace talks ka ishaara diya, Brent $6 gira.", card);
        Assert.StartsWith("Aaj ka mausam (Tue 3 Nov): PATA NAHI", IraAlertText.Mausam(m with { RangeMedian = null }, new DateOnly(2026, 11, 3)));
    }

    [Theory]
    [InlineData("Rs 1,200 crore aaye", "Inflows of Rs 1200 crore", "")]
    [InlineData("NIFTY 50 aur S&P 500 gire", "Markets fall", "")]
    [InlineData("FY27 mein Q2 ki growth 5% rahegi", "growth seen at 5%", "")]
    [InlineData("Brent 3% gira", "Brent fell 2%", "3")]
    [InlineData("Repo 6.5% pe", "RBI holds the repo at 6.50%", "6.5")]
    public void Numbers_are_matched_against_the_headline_without_names_or_grouping(string line, string source, string missing)
    {
        Assert.Equal(missing, string.Join(",", MarketImpactAgent.NumbersNotIn(line, source)));
    }

    // ======================================================== the agent

    [Fact]
    public async Task The_agent_rates_the_newest_unread_headlines_and_shows_the_stories_already_told()
    {
        var ai = Build();
        var now = DateTime.UtcNow;
        ai.Db.NewsItems.AddRange(
            NewsItem(1, "Oil tumbles $6 as Iran signals peace talks", now.AddMinutes(-3)),
            NewsItem(2, "Five stocks to buy this Diwali", now.AddMinutes(-2)),
            NewsItem(3, "An older story", now.AddMinutes(-200)));
        ai.Db.AiReports.Add(new AiReport
        {
            Id = 77, AgentKey = AiCatalog.MarketImpact, SubjectType = AiReportSubject.News, SubjectId = "99", Status = AiReportStatus.Ok,
            DataJson = """{"impact":3,"what":"RBI ne repo rate badhaya."}""", CreatedUtc = now.AddHours(-1), UpdatedUtc = now.AddHours(-1),
        });
        await ai.Db.SaveChangesAsync();
        ai.Provider.On(Analyst1, Answer($$"""
            {"items":[{"id":"n2","impact":0},
              {"id":"n1","impact":3,"scope":"global","channels":["oil"],"nifty":"up","volatility":"higher","horizon":"day","confidence":0.7,
               "repeatOf":null,"what":"Iran ne peace talks ka ishaara diya, tel $6 gira.","why":"Sasta tel India ke liye achha.","watch":"Deal sign hoti hai ya nahi."}]}
            """));

        Assert.True(await Agent(ai).RunOnceAsync(now, CancellationToken.None));

        var reports = await ai.Db.AiReports.Where(r => r.Id != 77).OrderBy(r => r.SubjectId).ToListAsync();
        Assert.Equal(["1", "2"], reports.Select(r => r.SubjectId));
        Assert.All(reports, r => Assert.Equal(AiReportStatus.Ok, r.Status));
        Assert.Equal((3, "up"), (MarketImpactAgent.ReadFacts(reports[0].DataJson)!.Impact, MarketImpactAgent.ReadFacts(reports[0].DataJson)!.Nifty));
        Assert.Equal(0, MarketImpactAgent.ReadFacts(reports[1].DataJson)!.Impact);
        // The model saw the story told an hour ago, by its id, to name a repeat.
        string asked = ai.Provider.Requests.Single(r => r.Model == Analyst1).Body;
        Assert.Contains("t77", asked);
        Assert.Contains("RBI ne repo rate badhaya.", asked);
        Assert.DoesNotContain("An older story", asked);
    }

    // ======================================================== Ira tells it

    [Fact]
    public async Task Ira_tells_a_new_big_story_in_hinglish_with_the_market_now()
    {
        var now = Seen.AddMinutes(3);
        using var db = NewDb();
        Report(db, 1, now.AddMinutes(-1), Big());
        await db.SaveChangesAsync();
        var pulse = new Pulse(now, nifty: 0.618m, crude: -3.1m);

        var told = await Desk(db, pulse).EventsAsync(new IraMarks(), now, CancellationToken.None);

        var e = Assert.Single(told);
        Assert.Equal((IraDesk.NewsKind, "news:1"), (e.Kind, e.Key));
        Assert.Equal(
            "Bahut badi khabar, asar 3/3 (Reuters, 10:05 IST): Iran ne peace talks ka ishaara diya, Brent $6 gira. NIFTY pe asar: upar, " +
            "din zyaada hil sakta hai. Abhi aaj: NIFTY +0.62%, crude -3.10%. Kyon: Tel sasta hone se India ka import bill aur mehngai ghategi. " +
            "Dekhna: Deal sign hoti hai ya nahi.",
            e.Text);
        Assert.True(e.Text.Length <= IraAlertText.MaxChars);
    }

    [Fact]
    public async Task A_small_story_a_repeat_old_news_and_an_invalid_record_are_not_told_and_a_quote_gone_stale_is_left_out()
    {
        var now = Seen.AddMinutes(3);
        using var db = NewDb();
        Report(db, 1, now.AddMinutes(-1), new JsonObject { ["id"] = "n1", ["impact"] = 1 });
        Report(db, 2, now.AddMinutes(-1), Big(repeatOf: "t9"), told: ["t9"]);
        Report(db, 3, now.AddMinutes(-1), Big(), firstSeen: now.AddMinutes(-61));
        Report(db, 4, now.AddMinutes(-1), Big(nifty: "sideways"));
        Report(db, 5, now.AddMinutes(-1), Big(nifty: "down", impact: 2));
        await db.SaveChangesAsync();
        var stale = new Pulse(now.AddMinutes(-20), nifty: -0.4m, crude: 1m);

        var told = await Desk(db, stale).EventsAsync(new IraMarks(), now, CancellationToken.None);

        var e = Assert.Single(told);
        Assert.Equal("news:5", e.Key);
        Assert.StartsWith("Badi khabar, asar 2/3 (Reuters, 10:05 IST)", e.Text);
        Assert.Contains("NIFTY pe asar: neeche, din zyaada hil sakta hai. Kyon:", e.Text);
        Assert.DoesNotContain("Abhi aaj", e.Text);
    }

    [Fact]
    public async Task A_try_that_failed_holds_the_mark_and_its_story_is_told_once_a_retry_reads_it()
    {
        var now = Seen.AddMinutes(3);
        using var db = NewDb();
        db.AiReports.Add(new AiReport
        {
            Id = 1, AgentKey = AiCatalog.MarketImpact, SubjectType = AiReportSubject.News, SubjectId = "1", Status = AiReportStatus.Failed,
            Attempts = 1, DataJson = "{}", CreatedUtc = now.AddMinutes(-2), UpdatedUtc = now.AddMinutes(-2),
        });
        await db.SaveChangesAsync();
        var marks = new IraMarks();
        var desk = Desk(db, null);

        Assert.Empty(await desk.EventsAsync(marks, now, CancellationToken.None));
        Assert.Empty(await desk.EventsAsync(marks, now.AddMinutes(1), CancellationToken.None));
        Assert.Equal(0, marks.News.Through);

        var row = await db.AiReports.SingleAsync();
        var (status, title, body, data, _) = MarketImpactAgent.Check(Big(), Text, NoneTold, "Reuters", "Oil tumbles", Seen);
        (row.Status, row.Title, row.Body, row.DataJson, row.UpdatedUtc) = (status, title, body, data, now.AddMinutes(16));
        await db.SaveChangesAsync();

        var told = await desk.EventsAsync(marks, now.AddMinutes(16), CancellationToken.None);
        Assert.Equal("news:1", Assert.Single(told).Key);
        Assert.Empty(await desk.EventsAsync(marks, now.AddMinutes(17), CancellationToken.None));
    }

    [Fact]
    public async Task With_news_alerts_off_the_stories_are_rated_and_nothing_is_told()
    {
        var now = Seen.AddMinutes(3);
        using var db = NewDb();
        Report(db, 1, now.AddMinutes(-1), Big());
        await db.SaveChangesAsync();
        var settings = new AiTestKit.Monitor(Settings(s => s.Voice.Alerts.News = false));

        Assert.Empty(await Desk(db, null, settings).EventsAsync(new IraMarks(), now, CancellationToken.None));
        Assert.DoesNotContain("market news", IraAlertText.AlertsOn(settings.CurrentValue.Voice.Alerts));
        Assert.Contains("big market news comes (at any hour)", IraAlertText.AlertsOn(new AiVoiceAlertsSettings()));
    }

    [Fact]
    public async Task A_fresh_desk_starts_its_news_mark_at_the_newest_record_so_history_is_never_told()
    {
        var now = Seen.AddMinutes(3);
        using var db = NewDb();
        Report(db, 41, now.AddMinutes(-1), Big());
        await db.SaveChangesAsync();
        var desk = Desk(db, null);

        var marks = await desk.StartAsync(CancellationToken.None);

        Assert.Equal(41, marks.News.Through);
        Assert.Empty(await desk.EventsAsync(marks, now, CancellationToken.None));
    }

    // ======================================================== helpers

    private static MarketImpactAgent Agent(Services ai) => new(
        ai.Db, ai.Gateway, new AiReportWriter(ai.Db, ai.Options), new AiSchedulerState(), ai.Options, NullLogger<MarketImpactAgent>.Instance);

    private static IraDesk Desk(TradingDbContext db, IMarketPulseService? pulse, AiTestKit.Monitor? settings = null)
    {
        var lots = new PositionGreeksTests.FixedLots(65);
        return new IraDesk(db, new RunPnl(db, lots, new RunCharges(db, lots)), null, null, pulse, settings ?? new AiTestKit.Monitor(Settings()));
    }

    /// <summary>A Market Impact report on headline <paramref name="id"/>, checked as the agent checks it.</summary>
    private static void Report(TradingDbContext db, long id, DateTime atUtc, JsonObject record, DateTime? firstSeen = null, string[]? told = null)
    {
        var (status, title, body, data, error) = MarketImpactAgent.Check(record, Text,
            new HashSet<string>(told ?? [], StringComparer.OrdinalIgnoreCase), "Reuters", "Oil tumbles $6 as Iran signals peace talks", firstSeen ?? Seen);
        db.AiReports.Add(new AiReport
        {
            Id = id, AgentKey = AiCatalog.MarketImpact, SubjectType = AiReportSubject.News, SubjectId = id.ToString(), Status = status,
            Attempts = 1, Title = title, Body = body, DataJson = data, Error = error, CreatedUtc = atUtc, UpdatedUtc = atUtc,
        });
    }

    /// <summary>A Market Impact report from <paramref name="source"/>, checked as the agent checks it.</summary>
    private static void ReportFrom(TradingDbContext db, long id, DateTime atUtc, JsonObject record, string source, string[] told)
    {
        var (status, title, body, data, error) = MarketImpactAgent.Check(record, Text, new HashSet<string>(told, StringComparer.OrdinalIgnoreCase),
            source, "Another publisher's headline", atUtc);
        db.AiReports.Add(new AiReport
        {
            Id = id, AgentKey = AiCatalog.MarketImpact, SubjectType = AiReportSubject.News, SubjectId = id.ToString(), Status = status,
            Attempts = 1, Title = title, Body = body, DataJson = data, Error = error, CreatedUtc = atUtc, UpdatedUtc = atUtc,
        });
    }

    private static NewsItem NewsItem(long id, string title, DateTime firstSeen) => new()
    {
        Id = id, Source = "test", Category = "global", Title = title, Summary = string.Empty, Link = $"https://example.com/{id}",
        LinkHash = $"h{id}", PublishedUtc = firstSeen, FirstSeenUtc = firstSeen,
    };

    /// <summary>The Market pulse with NIFTY 50 and crude oil quoted at <paramref name="atUtc"/>.</summary>
    private sealed class Pulse(DateTime atUtc, decimal nifty, decimal crude) : IMarketPulseService
    {
        public Task<MarketPulseResponse> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MarketPulseResponse
        {
            Groups =
            [
                new MarketPulseGroup { Key = "index", Items = [new MarketPulseItem { Symbol = "NSE:NIFTY50-INDEX", Name = "NIFTY 50", ChangePercent = nifty, UpdatedUtc = atUtc }] },
                new MarketPulseGroup { Key = "commodity", Items = [new MarketPulseItem { Symbol = "MCX:CRUDEOIL26NOVFUT", Name = "Crude oil", ChangePercent = crude, UpdatedUtc = atUtc }] },
            ],
        });

        public Task<IReadOnlyList<string>> EnsureSubscribedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
