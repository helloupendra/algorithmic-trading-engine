using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Api.Services.AiTools;
using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.Enums;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Services.MarketFactors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The four desk analysts (owner, 10 Oct 2026): when each writes (the exchange calendar's trading days, its IST times,
/// never past its window), the facts the code hands it, the check that no level or number in its answer was made up,
/// and where its reports go: the AI Trader's brief (never one written after a replay's clock), the daily digest, Today,
/// the Reports tab and its own chat.
/// </summary>
public sealed class AiDeskAnalystsTests
{
    // Tuesday 6 and Wednesday 7 Oct 2026 are sessions; Thursday 8 Oct is the morning before the open; Saturday 10 Oct has none.
    private static readonly DateOnly Tue = new(2026, 10, 6);
    private static readonly DateOnly Wed = new(2026, 10, 7);
    private static readonly DateOnly Thu = new(2026, 10, 8);
    private static readonly DateOnly Sat = new(2026, 10, 10);

    private const string NiftySpot = "NSE:NIFTY50-INDEX";
    private const string Analyst1 = "moonshotai/kimi-k3";

    private static DateTime At(DateOnly day, int hour, int minute) => IstTime.FromIst(day.ToDateTime(new TimeOnly(hour, minute)));

    // ---------- when they write ----------

    [Theory]
    [InlineData(8, 19, false)]
    [InlineData(8, 20, true)]
    [InlineData(9, 14, true)]   // a missed 08:20 is still written before the open
    [InlineData(9, 15, false)]  // not after it: the facts would be of another moment
    [InlineData(10, 0, false)]  // the half hours only while an index option is open
    public async Task The_technical_analyst_writes_at_08_20_until_the_open(int hour, int minute, bool due)
    {
        var ai = Build();
        var agent = Technical(ai);

        var slot = await agent.DueAsync(At(Thu, hour, minute), default);

        Assert.Equal(due ? ((DateOnly, TimeOnly)?)(Thu, new TimeOnly(8, 20)) : null, slot);
    }

    [Fact]
    public async Task On_the_half_hour_it_writes_while_the_AI_Trader_or_a_strategy_run_holds_an_index_option()
    {
        var ai = Build();
        var agent = Technical(ai);
        Assert.Null(await agent.DueAsync(At(Wed, 10, 0), default));

        // The AI Trader's shadow book holds a NIFTY call.
        ai.Db.AiTraderShadowPositions.Add(new AiTraderShadowPosition
        {
            Day = Wed, Underlying = "NIFTY", Symbol = "NSE:NIFTY26O1325000CE", OptionType = "CE", Strike = 25000m, Lots = 1, LotSize = 65,
            EntryUtc = At(Wed, 9, 40), EntryPrice = 100m, StopLoss = 70m, Target = 140m, CreatedUtc = At(Wed, 9, 40),
        });
        await ai.Db.SaveChangesAsync();
        Assert.Equal((Wed, new TimeOnly(10, 0)), await agent.DueAsync(At(Wed, 10, 0), default));
        Assert.Equal((Wed, new TimeOnly(10, 0)), await agent.DueAsync(At(Wed, 10, 29), default));
        Assert.Equal((Wed, new TimeOnly(15, 0)), await agent.DueAsync(At(Wed, 15, 10), default));
        // Outside the session the half hours are not written, positions or not.
        Assert.Null(await agent.DueAsync(At(Wed, 15, 30), default));
        Assert.Null(await agent.DueAsync(At(Wed, 9, 20), default));
    }

    [Fact]
    public async Task An_open_leg_of_a_live_strategy_run_counts_and_a_recap_s_or_a_stock_s_does_not()
    {
        var ai = Build();
        long Run(string parameters, string status = "Running")
        {
            var run = new SimulationRun { UserId = 1, Mode = "LivePaper", Status = status, StrategyName = "Ghost", Symbol = NiftySpot, ParametersJson = parameters, CreatedUtc = At(Wed, 9, 15) };
            ai.Db.SimulationRuns.Add(run);
            ai.Db.SaveChanges();
            return run.Id;
        }

        void Leg(long runId, string symbol, string status = "Open") => ai.Db.PaperPositions.Add(new PaperPosition
        {
            SimulationRunId = runId, StrategyName = "Ghost", Symbol = symbol, Direction = "LONG", Quantity = 65, AveragePrice = 100m, Status = status,
            OpenedUtc = At(Wed, 9, 30),
        });

        Leg(Run("""{"session":"recap"}"""), "NSE:NIFTY26O1325000CE");
        Leg(Run("{}"), "NSE:RELIANCE26OCT1400CE");
        Leg(Run("{}", status: "Stopped"), "NSE:BANKNIFTY26OCT56000PE");
        Leg(Run("{}"), "NSE:BANKNIFTY26OCT56000PE", status: "Closed");
        await ai.Db.SaveChangesAsync();
        Assert.Empty(await TechnicalAnalystAgent.OpenIndexOptionsAsync(ai.Db, Wed, default));

        Leg(Run("{}"), "BSE:SENSEX26O1582000PE");
        await ai.Db.SaveChangesAsync();
        Assert.Equal(new[] { "SENSEX" }, await TechnicalAnalystAgent.OpenIndexOptionsAsync(ai.Db, Wed, default));
    }

    [Theory]
    [InlineData(8, 29, null)]
    [InlineData(8, 30, "08:30")]
    [InlineData(9, 14, "08:30")]
    [InlineData(10, 30, "10:30")]
    [InlineData(12, 59, "10:30")]
    [InlineData(13, 0, "13:00")]
    [InlineData(14, 30, "14:30")]
    [InlineData(15, 29, "14:30")]
    [InlineData(15, 30, null)]
    public async Task The_options_analyst_writes_at_08_30_10_30_13_00_and_14_30(int hour, int minute, string? slot)
    {
        var ai = Build();

        var due = await Options(ai).DueAsync(At(Wed, hour, minute), default);

        Assert.Equal(slot, due?.At.ToString("HH:mm", CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(8, 44, false)]
    [InlineData(8, 45, true)]
    [InlineData(9, 14, true)]
    [InlineData(9, 15, false)]
    public async Task The_macro_analyst_writes_at_08_45_until_the_open(int hour, int minute, bool due)
    {
        var ai = Build();

        Assert.Equal(due, await Macro(ai).DueAsync(At(Thu, hour, minute), default) is not null);
    }

    [Fact]
    public async Task No_analyst_writes_on_a_holiday_or_a_weekend_whatever_the_clock_says()
    {
        var ai = Build();
        var holiday = new MarketSessionService(new Calendar(Thu));

        Assert.Null(await Technical(ai, holiday).DueAsync(At(Thu, 8, 20), default));
        Assert.Null(await Options(ai, holiday).DueAsync(At(Thu, 8, 30), default));
        Assert.Null(await Macro(ai, holiday).DueAsync(At(Thu, 8, 45), default));
        Assert.Null(await Technical(ai).DueAsync(At(Sat, 8, 20), default));
        Assert.Null(await Macro(ai).DueAsync(At(Sat, 8, 45), default));
    }

    [Fact]
    public async Task A_slot_is_written_once_and_a_failed_one_again_after_the_retry_wait()
    {
        var ai = Build();
        var macro = Macro(ai, reportClock: new FixedClock(At(Thu, 8, 45)));
        string subject = AnalystSchedule.SubjectId(Thu, new TimeOnly(8, 45));
        Assert.Equal("2026-10-08 08:45", subject);
        ai.Db.AiReports.Add(new AiReport
        {
            AgentKey = AiCatalog.MacroAnalyst, SubjectType = AiReportSubject.Check, SubjectId = subject, SessionDate = Thu,
            Status = AiReportStatus.Failed, Attempts = 1, CreatedUtc = At(Thu, 8, 45), UpdatedUtc = At(Thu, 8, 45),
        });
        await ai.Db.SaveChangesAsync();

        Assert.Null(await Macro(ai, reportClock: new FixedClock(At(Thu, 8, 50))).DueAsync(At(Thu, 8, 50), default));
        Assert.NotNull(await Macro(ai, reportClock: new FixedClock(At(Thu, 9, 1))).DueAsync(At(Thu, 9, 1), default));

        var row = await ai.Db.AiReports.SingleAsync();
        row.Status = AiReportStatus.Ok;
        await ai.Db.SaveChangesAsync();
        Assert.Null(await macro.DueAsync(At(Thu, 9, 1), default));
    }

    [Fact]
    public void The_desk_analysts_start_on_read_their_memories_and_run_on_the_scheduler()
    {
        var settings = new AiSettings();
        var state = new AiState([], AiCatalog.Agents.Select(a => new AiAgentState(a, a.Built && a.StartsOn, [], false, null, null, null)).ToList());

        foreach (var key in DeskAnalysts.Keys)
        {
            Assert.True(AiCatalog.Agent(key)!.Built);
            Assert.True(settings.HasMemory(key), $"{key} has no memory");
            Assert.True(AiAgentScheduler.IsOn(key, state, settings), $"{key} is not on by default");
        }
    }

    // ---------- the technical analyst: facts and check ----------

    [Fact]
    public void The_technical_facts_are_the_desks_own_arithmetic_with_levels_either_side_of_the_price()
    {
        var minutes = Session(NiftySpot, Tue, 25000m).Concat(Session(NiftySpot, Wed, 25100m)).ToList();
        var asOf = At(Wed, 11, 0);

        var nifty = TechnicalAnalystAgent.Build("NIFTY", minutes, asOf);

        Assert.Null(nifty.NotAvailable);
        var last = minutes.Last(b => b.BarStartUtc < asOf);
        Assert.Equal(last.Close, nifty.Json["price"]!.GetValue<decimal>());
        Assert.Equal(minutes.Last(b => IstTime.DateOf(b.BarStartUtc) == Tue).Close, nifty.Json["previousClose"]!.GetValue<decimal>());
        Assert.NotNull(nifty.Json["ema20"]);
        Assert.NotNull(nifty.Json["ema50"]);
        Assert.NotNull(nifty.Json["atr14"]);
        Assert.Equal("not available: an index trades no volume", nifty.Json["vwap"]!.GetValue<string>());
        Assert.Contains(nifty.Levels, l => l.What.StartsWith("previous close (Tue 6 Oct)", StringComparison.Ordinal));
        Assert.Contains(nifty.Levels, l => l.What == "today's high");
        Assert.Contains(nifty.Levels, l => l.What.StartsWith("swing", StringComparison.Ordinal));
        Assert.Contains(nifty.Levels, l => l.Side == "below");
        Assert.Equal(nifty.Levels.Select(l => l.Price).OrderByDescending(p => p), nifty.Levels.Select(l => l.Price));
        Assert.All(nifty.Levels, l => Assert.True(nifty.Numbers.Allows(l.Price)));

        // Before the open the facts are the last session's, and say so.
        var morning = TechnicalAnalystAgent.Build("NIFTY", minutes, At(Thu, 8, 20));
        Assert.Contains("today's has not begun", morning.Json["session"]!.GetValue<string>());
        Assert.Contains(morning.Levels, l => l.What == "close of Wed 7 Oct");
        Assert.Null(morning.Json["last30MinPct"]);

        Assert.Equal("no session bars recorded", TechnicalAnalystAgent.Build("SENSEX", [], asOf).NotAvailable);
    }

    [Fact]
    public void A_grade_must_name_levels_the_code_found_and_state_only_numbers_it_handed_over()
    {
        var facts = TechnicalFactsOf(At(Wed, 11, 0));
        var nifty = facts.Indices[0];
        var level = nifty.Levels.First(l => l.Side == "above");
        decimal price = nifty.Json["price"]!.GetValue<decimal>();
        string Answer(string underlying, string grade, string levels, string reason) =>
            $$"""{"indices":[{"underlying":"{{underlying}}","bias":"bullish","grade":"{{grade}}","keyLevels":[{{levels}}],"reason":"{{reason}}"}],"summary":"Only NIFTY has bars."}""";
        string Px(decimal v) => v.ToString(CultureInfo.InvariantCulture);

        // A level rounded to the whole point is still that level; the reason cites the price as handed over.
        var ok = TechnicalAnalystAgent.CheckAnswer(
            Answer("NIFTY", "B", Px(Math.Round(level.Price, 0, MidpointRounding.AwayFromZero)), $"Price {Px(price)} is above both EMAs, with {level.What} overhead."), facts);
        Assert.True(ok.Status == AiReportStatus.Ok, ok.Error);
        Assert.Equal("NIFTY bullish B", ok.Title);
        Assert.Equal(level.Id, ok.Data["indices"]![0]!["keyLevels"]![0]!["id"]!.GetValue<string>());

        var madeUpLevel = TechnicalAnalystAgent.CheckAnswer(Answer("NIFTY", "B", "12345", "A clean trend."), facts);
        Assert.Equal(AiReportStatus.Invalid, madeUpLevel.Status);
        Assert.Contains("key level 12345 is not one of its levels", madeUpLevel.Error);

        var madeUpNumber = TechnicalAnalystAgent.CheckAnswer(Answer("NIFTY", "B", Px(level.Price), "Target 26150 by the close."), facts);
        Assert.Equal(AiReportStatus.Invalid, madeUpNumber.Status);
        Assert.Contains("states 26150", madeUpNumber.Error);

        Assert.Contains("grade \"E\"", TechnicalAnalystAgent.CheckAnswer(Answer("NIFTY", "E", Px(level.Price), "A clean trend."), facts).Error);
        var notHanded = TechnicalAnalystAgent.CheckAnswer(Answer("SENSEX", "B", Px(level.Price), "A clean trend."), facts);
        Assert.Contains("SENSEX was graded, but no facts were handed over for it", notHanded.Error);
        Assert.Contains("NIFTY was not graded", notHanded.Error);
        Assert.Equal(AiReportStatus.Invalid, TechnicalAnalystAgent.CheckAnswer("NIFTY looks bullish.", facts).Status);
    }

    [Fact]
    public async Task A_technical_run_hands_the_model_the_facts_and_keeps_its_checked_grade()
    {
        var ai = Build();
        await SeedBarsAsync(ai.Db, NiftySpot, Session(NiftySpot, Tue, 25000m).Concat(Session(NiftySpot, Wed, 25100m)));
        var facts = TechnicalFactsOf(At(Thu, 8, 20));
        var level = facts.Indices[0].Levels[0];
        ai.Provider.On(Analyst1, Answer(
            $$"""{"indices":[{"underlying":"NIFTY","bias":"bullish","grade":"B","keyLevels":[{{level.Price.ToString(CultureInfo.InvariantCulture)}}],"reason":"Above both EMAs."}],"summary":"Only NIFTY has bars."}"""));
        var agent = Technical(ai);

        Assert.True(await agent.RunOnceAsync(At(Thu, 8, 20), default));
        Assert.False(await agent.RunOnceAsync(At(Thu, 8, 21), default));

        var report = await ai.Db.AiReports.SingleAsync();
        Assert.Equal((AiCatalog.TechnicalAnalyst, AiReportSubject.Check, "2026-10-08 08:20", AiReportStatus.Ok), (report.AgentKey, report.SubjectType, report.SubjectId, report.Status));
        Assert.Equal("Technical 08:20: NIFTY bullish B", report.Title);
        var data = JsonNode.Parse(report.DataJson)!;
        Assert.Equal("08:20", data["slot"]!.GetValue<string>());
        Assert.Equal("no session bars recorded", data["facts"]!["indices"]![1]!["notAvailable"]!.GetValue<string>());
        string question = JsonNode.Parse(ai.Provider.Requests.Single().Body)!["messages"]!.AsArray().Last()!["content"]!.GetValue<string>();
        Assert.Contains("TECHNICAL FACTS, Thu 8 Oct 2026, 08:20 IST", question);
        Assert.Contains("\"levels\"", question);
        var call = await ai.Db.AiCalls.SingleAsync();
        Assert.Equal(("schedule", AiCatalog.TechnicalAnalyst), (call.Source, call.RequestedBy));
    }

    [Fact]
    public async Task With_no_bars_at_all_no_model_is_asked_and_the_slot_is_a_failed_try()
    {
        var ai = Build();

        Assert.True(await Technical(ai).RunOnceAsync(At(Thu, 8, 20), default));

        Assert.Empty(ai.Provider.Requests);
        var report = await ai.Db.AiReports.SingleAsync();
        Assert.Equal((AiReportStatus.Failed, 1), (report.Status, report.Attempts));
        Assert.Contains("No index has bars to read", report.Error);
    }

    // ---------- the options analyst ----------

    [Fact]
    public async Task The_chain_facts_are_the_chain_page_s_numbers_with_the_session_s_change()
    {
        var db = NewDb();
        SeedChain(db, At(Wed, 9, 16), spot: 25120m, oiScale: 1.0m, iv: 14.0m);
        SeedChain(db, At(Wed, 10, 29), spot: 25130m, oiScale: 1.2m, iv: 15.2m);

        var nifty = await OptionsAnalystAgent.BuildAsync(Chains(db), "NIFTY", At(Wed, 10, 30), default);

        Assert.Null(nifty.NotAvailable);
        var j = nifty.Json;
        Assert.Equal("today's latest capture", j["captureNote"]!.GetValue<string>());
        Assert.Equal(25300m, j["callWalls"]![0]!["strike"]!.GetValue<decimal>());
        Assert.Equal(10.8m, j["callWalls"]![0]!["oiLakh"]!.GetValue<decimal>());
        Assert.Equal(170m, j["callWalls"]![0]!["distanceFromSpot"]!.GetValue<decimal>());
        Assert.Equal(25000m, j["putWalls"]![0]!["strike"]!.GetValue<decimal>());
        Assert.Equal(15.2m, j["atmIv"]!.GetValue<decimal>());
        Assert.Equal(14.0m, j["sinceFirstCapture"]!["atmIvThen"]!.GetValue<decimal>());
        Assert.Equal(1.2m, j["sinceFirstCapture"]!["atmIvChange"]!.GetValue<decimal>());
        Assert.NotNull(j["pcr"]);
        Assert.NotNull(j["maxPain"]);

        Assert.Equal("no option chain recorded", (await OptionsAnalystAgent.BuildAsync(Chains(db), "SENSEX", At(Wed, 10, 30), default)).NotAvailable);
        // At 08:30 the newest capture is the last session's: the facts say so.
        var morning = await OptionsAnalystAgent.BuildAsync(Chains(db), "NIFTY", At(Thu, 8, 30), default);
        Assert.StartsWith("the last capture of an earlier session", morning.Json["captureNote"]!.GetValue<string>());
    }

    [Fact]
    public async Task Every_number_in_an_options_line_must_be_one_of_that_index_s_facts()
    {
        var db = NewDb();
        SeedChain(db, At(Wed, 9, 16), spot: 25120m, oiScale: 1.0m, iv: 14.0m);
        SeedChain(db, At(Wed, 10, 29), spot: 25130m, oiScale: 1.2m, iv: 15.2m);
        var facts = new OptionsFacts { AsOfUtc = At(Wed, 10, 30), Indices = [await OptionsAnalystAgent.BuildAsync(Chains(db), "NIFTY", At(Wed, 10, 30), default)] };
        string pcr = facts.Indices[0].Json["pcr"]!.GetValue<decimal>().ToString(CultureInfo.InvariantCulture);
        string Answer(string line) => $$"""{"indices":[{"underlying":"NIFTY","stance":"range-bound","lines":["{{line}}"]}],"summary":"Walls either side."}""";

        var ok = OptionsAnalystAgent.CheckAnswer(Answer($"Call wall 25,300 with 10.8 lakh OI, 170 points above the spot; PCR {pcr}; ATM IV up 1.2 to 15.2."), facts);
        Assert.True(ok.Status == AiReportStatus.Ok, ok.Error);
        Assert.Equal("NIFTY range-bound", ok.Title);

        var bad = OptionsAnalystAgent.CheckAnswer(Answer("Max pain 25,400 pulls the spot up."), facts);
        Assert.Equal(AiReportStatus.Invalid, bad.Status);
        Assert.Contains("states 25,400", bad.Error);
        Assert.Contains("stance", OptionsAnalystAgent.CheckAnswer(Answer("PCR " + pcr).Replace("range-bound", "up"), facts).Error);
    }

    // ---------- the macro analyst ----------

    [Fact]
    public async Task The_macro_facts_are_the_market_factors_recorded_by_the_slot_each_with_an_id()
    {
        var db = NewDb();
        SeedMacro(db);

        var facts = await MacroAnalystAgent.BuildAsync(db, Live(db), new MarketFactorsQueries(db, new MarketFactorsStatus()), At(Thu, 8, 45), default);

        string all = string.Join('\n', facts.Facts.Select(f => $"{f.Id} {f.Text}"));
        Assert.Contains("m1 NIFTY's last close (Wed 7 Oct): 25200 (+0.8% on that session).", all);
        Assert.Contains("GIFT Nifty this morning: 25310.5 (+0.42% against its own previous close), taken 08:30 IST; +110.5 points (+0.44%) from NIFTY's last close", all);
        Assert.Contains("S&P 500 last close (Wed 7 Oct): 6532.5 (+0.5% on the day).", all);
        Assert.Contains("FII/FPI cash on Wed 7 Oct: net -1234.56 crore.", all);
        Assert.Contains("DII cash on Wed 7 Oct: net +2345.67 crore.", all);
        Assert.Contains("India VIX's last close (Wed 7 Oct): 12.6 (+5% on that session).", all);
        Assert.Contains("Today at 10:00 IST: RBI policy decision (Policy, importance 3).", all);
        // A snapshot taken after the slot is not known at it.
        Assert.DoesNotContain("25999", all);
    }

    [Fact]
    public async Task The_macro_answer_cites_facts_by_id_its_numbers_are_the_facts_and_its_size_is_clamped()
    {
        var db = NewDb();
        SeedMacro(db);
        var facts = await MacroAnalystAgent.BuildAsync(db, Live(db), new MarketFactorsQueries(db, new MarketFactorsStatus()), At(Thu, 8, 45), default);
        string Answer(string bias, string size, string rests, string reason) =>
            $$"""{"bias":"{{bias}}","sizeModifier":{{size}},"restsOn":[{{rests}}],"reason":"{{reason}}","risks":"An RBI surprise at 10:00."}""";

        var ok = MacroAnalystAgent.CheckAnswer(Answer("lean bullish", "1.4", "\"m1\",\"m2\"", "GIFT Nifty is 110.5 points over NIFTY's 25,200 close; S&P 500 rose 0.5%."), facts);
        Assert.True(ok.Status == AiReportStatus.Ok, ok.Error);
        Assert.Equal("lean bullish, size ×1.00", ok.Title);
        Assert.Equal((1.0m, 1.4m), (ok.Data["sizeModifier"]!.GetValue<decimal>(), ok.Data["sizeModifierAsked"]!.GetValue<decimal>()));
        Assert.Contains("clamped to 0.5–1.0", ok.Body);
        Assert.Contains("- m2: GIFT Nifty this morning", ok.Body);
        Assert.Equal(0.5m, MacroAnalystAgent.CheckAnswer(Answer("neutral", "0.2", "\"m7\"", "Mixed."), facts).Data["sizeModifier"]!.GetValue<decimal>());

        var unknown = MacroAnalystAgent.CheckAnswer(Answer("neutral", "0.8", "\"m99\"", "Mixed."), facts);
        Assert.Contains("rests on \"m99\", which is not one of the facts", unknown.Error);
        var madeUp = MacroAnalystAgent.CheckAnswer(Answer("bullish", "0.8", "\"m2\"", "GIFT Nifty points to a 300 point gap up."), facts);
        Assert.Contains("states 300", madeUp.Error);
        Assert.Contains("bias \"very bullish\"", MacroAnalystAgent.CheckAnswer(Answer("very bullish", "0.8", "\"m2\"", "Up."), facts).Error);
    }

    // ---------- the fundamental analyst ----------

    [Theory]
    [InlineData("Financial Result Updates", "", "", true)]
    [InlineData("Outcome of Board Meeting", "Approved the unaudited financial results for the quarter ended 30 September 2026", "", true)]
    [InlineData("Outcome of Board Meeting", "Appointment of a director", "", false)]
    [InlineData("Updates", "Press release", "results,dividend", true)]
    [InlineData("Trading Window-XBRL", "Closure of trading window for financial results", "results", false)]
    [InlineData("Board Meeting Intimation for Financial Results", "To consider results on 20 Oct", "", false)]
    public void A_results_filing_is_told_by_its_subject_or_topics_and_paperwork_never_is(string subject, string details, string topics, bool results)
    {
        Assert.Equal(results, FundamentalAnalystAgent.IsResultsFiling(subject, details, topics));
    }

    [Fact]
    public async Task Only_results_filings_of_F_and_O_stocks_are_read_each_once()
    {
        var ai = Build();
        var now = At(Wed, 16, 0);
        ai.Db.Instruments.Add(new Instrument { Symbol = "NSE:TCS26OCTFUT", Exchange = "NSE", Segment = "FO", InstrumentType = "FUT", Underlying = "TCS", ExpiryDate = new DateOnly(2026, 10, 27) });
        ai.Db.Instruments.Add(new Instrument { Symbol = "NSE:NIFTY26OCTFUT", Exchange = "NSE", Segment = "FO", InstrumentType = "FUT", Underlying = "NIFTY", ExpiryDate = new DateOnly(2026, 10, 27) });
        ai.Db.MarketFuturesDaily.Add(new MarketFuturesDaily { Date = Tue, Underlying = "INFY", InstrumentKind = "STF", ExpiryDate = new DateOnly(2026, 10, 27) });
        long tcs = Filing(ai.Db, "TCS", "Financial Result Updates", "Revenue from operations at ₹65,000 crore", At(Wed, 15, 40));
        long infy = Filing(ai.Db, "INFY", "Financial Result Updates", "Net profit ₹7,000 crore", At(Wed, 15, 41));
        Filing(ai.Db, "SMALLCO", "Financial Result Updates", "Net profit ₹7 crore", At(Wed, 15, 42));     // not in the F&O universe
        Filing(ai.Db, "TCS", "Trading Window-XBRL", "Closure of trading window for financial results", At(Wed, 15, 43));
        Filing(ai.Db, "TCS", "Financial Result Updates", "Old results", At(Wed, 15, 40).AddDays(-3)); // past the lookback

        Assert.Equal(new[] { "INFY", "TCS" }, (await FundamentalAnalystAgent.FnoStocksAsync(ai.Db, Wed, default)).Order());
        var batch = await Fundamental(ai).NextBatchAsync(now, default);
        Assert.Equal(new[] { infy, tcs }, batch.Select(b => b.FilingId));

        ai.Db.AiReports.Add(new AiReport { AgentKey = AiCatalog.FundamentalAnalyst, SubjectType = AiReportSubject.Filing, SubjectId = infy.ToString(CultureInfo.InvariantCulture),
            Status = AiReportStatus.Ok, CreatedUtc = now, UpdatedUtc = now });
        await ai.Db.SaveChangesAsync();
        Assert.Equal(new[] { tcs }, (await Fundamental(ai).NextBatchAsync(now, default)).Select(b => b.FilingId));
    }

    [Fact]
    public void Each_metric_is_written_in_its_quote_its_quote_in_the_filing_and_a_change_is_stated_or_follows_from_the_figures()
    {
        const string text = "Financial Result Updates\nRevenue from operations of ₹65,250.4 crore against ₹60,000 crore a year ago. " +
                            "Net profit rose 8.5% to ₹12,100 crore.";
        JsonObject Record(string metrics, string surprise = "positive", string summary = "Revenue and profit grew.") =>
            (JsonObject)JsonNode.Parse($$"""{"id":"f1","results":true,"period":"Q2 FY27","metrics":[{{metrics}}],"surprise":"{{surprise}}","summary":"{{summary}}"}""")!;
        const string revenue = """{"metric":"revenue","value":65250.4,"unit":"crore","quote":"Revenue from operations of ₹65,250.4 crore","previous":60000,"previousQuote":"against ₹60,000 crore a year ago","yoyPct":8.8}""";
        const string profit = """{"metric":"net profit","value":12100,"unit":"crore","quote":"Net profit rose 8.5% to ₹12,100 crore","yoyPct":8.5}""";

        var ok = FundamentalAnalystAgent.Check(Record($"{revenue},{profit}", summary: "Revenue up 8.8% and profit up 8.5% on the year."), text, "TCS");
        Assert.True(ok.Status == AiReportStatus.Ok, ok.Error);
        Assert.Equal("TCS results (Q2 FY27): positive, revenue 65,250.4 crore, +8.8% YoY", ok.Title);
        Assert.Equal(8.8m, ok.Data["metrics"]![0]!["yoyComputed"]!.GetValue<decimal>());

        var notInText = FundamentalAnalystAgent.Check(Record("""{"metric":"EBITDA","value":15000,"unit":"crore","quote":"EBITDA of ₹15,000 crore"}"""), text, "TCS");
        Assert.Contains("is not in the filing's text", notInText.Error);
        var notInQuote = FundamentalAnalystAgent.Check(Record("""{"metric":"net profit","value":12500,"unit":"crore","quote":"Net profit rose 8.5% to ₹12,100 crore"}"""), text, "TCS");
        Assert.Contains("12500 is not written in its quote", notInQuote.Error);
        var madeUpChange = FundamentalAnalystAgent.Check(Record("""{"metric":"net profit","value":12100,"unit":"crore","quote":"Net profit rose 8.5% to ₹12,100 crore","yoyPct":11}"""), text, "TCS");
        Assert.Contains("a year-on-year change of 11% is neither written in the filing", madeUpChange.Error);
        var noChange = FundamentalAnalystAgent.Check(Record("""{"metric":"net profit","value":12100,"unit":"crore","quote":"Net profit rose 8.5% to ₹12,100 crore"}"""), text, "TCS");
        Assert.Contains("needs a year-on-year change behind it", noChange.Error);
        Assert.Equal(AiReportStatus.Ok,
            FundamentalAnalystAgent.Check((JsonObject)JsonNode.Parse("""{"id":"f1","results":false,"summary":"A board meeting date."}""")!, text, "TCS").Status);
    }

    [Fact]
    public async Task A_results_poll_reads_a_batch_every_ten_minutes_and_keeps_each_filing_s_checked_record()
    {
        var ai = Build();
        ai.Db.Instruments.Add(new Instrument { Symbol = "NSE:TCS26OCTFUT", Exchange = "NSE", Segment = "FO", InstrumentType = "FUT", Underlying = "TCS", ExpiryDate = new DateOnly(2026, 10, 27) });
        long id = Filing(ai.Db, "TCS", "Financial Result Updates", "Net profit rose 8.5% to ₹12,100 crore.", At(Wed, 15, 40));
        ai.Provider.On(Analyst1, Answer($$"""{"items":[{"id":"f{{id}}","results":true,"period":"","metrics":[{"metric":"net profit","value":12100,"unit":"crore","quote":"Net profit rose 8.5% to ₹12,100 crore","yoyPct":8.5}],"surprise":"positive","summary":"Profit up 8.5%."}]}"""));
        var state = new AiSchedulerState();

        Assert.True(await Fundamental(ai, state).RunOnceAsync(At(Wed, 16, 0), default));
        Assert.False(await Fundamental(ai, state).RunOnceAsync(At(Wed, 16, 5), default));

        var report = await ai.Db.AiReports.SingleAsync();
        Assert.Equal((AiReportSubject.Filing, id.ToString(CultureInfo.InvariantCulture), AiReportStatus.Ok, Wed),
            (report.SubjectType, report.SubjectId, report.Status, report.SessionDate));
        Assert.Equal("TCS", JsonNode.Parse(report.DataJson)!["symbol"]!.GetValue<string>());
    }

    // ---------- the brief, the digest, Today, the reports and the chat ----------

    [Fact]
    public async Task The_brief_reads_the_day_s_latest_checked_reports_written_by_its_clock_and_never_later_ones()
    {
        var db = NewDb();
        SeedSlotReports(db);

        string atTen = await DeskAnalysts.BriefSectionAsync(db, At(Wed, 10, 15), replay: true, default);
        Assert.Contains("- Macro (written 08:46): lean bullish, size ×0.80. GIFT Nifty is up.", atTen);
        Assert.Contains("- Technical (written 10:01) NIFTY: bullish, grade B, key levels 25120 / 24980.5. Above both EMAs.", atTen);
        // Written at 10:31 and 13:01: after the replayed moment.
        Assert.DoesNotContain("Options", atTen);
        Assert.DoesNotContain("bearish", atTen);

        string afternoon = await DeskAnalysts.BriefSectionAsync(db, At(Wed, 13, 30), replay: false, default);
        Assert.Contains("- Technical (written 13:01) NIFTY: bearish, grade C", afternoon);
        Assert.Contains("- Options (written 10:31) NIFTY: range-bound. Walls at 25000 and 25300.", afternoon);
        // An invalid report never reaches the brief.
        Assert.DoesNotContain("made up", afternoon);

        Assert.Equal("No analyst report was written for 6 Oct by 10:15 IST, the replay's clock.",
            await DeskAnalysts.BriefSectionAsync(db, At(Tue, 10, 15), replay: true, default));
        Assert.Equal("None written yet today.", await DeskAnalysts.BriefSectionAsync(db, At(Wed, 8, 30), replay: false, default));
    }

    [Fact]
    public async Task The_market_brief_carries_a_desk_analysts_section()
    {
        var db = NewDb();
        SeedSlotReports(db);
        var sessions = new MarketSessionService(new Calendar());
        var book = new MarketReplayBook();
        book.Begin(Wed);
        var builder = new MarketBriefBuilder(db, Live(db), Chains(db), null!, null!, sessions, NullLogger<MarketBriefBuilder>.Instance, book);

        var brief = await builder.BuildAsync(At(Wed, 10, 15), replay: true, default);

        Assert.Contains("DESK ANALYSTS (today's latest checked report of each, written by this moment; advisory)\n- Macro (written 08:46)", brief.Text);
        Assert.DoesNotContain("- Options (", brief.Text);
    }

    [Fact]
    public async Task The_daily_digest_has_one_short_section_for_each_analyst_that_ran_that_day()
    {
        var ai = Build();
        SeedSlotReports(ai.Db);
        ai.Db.AiReports.Add(new AiReport
        {
            AgentKey = AiCatalog.FundamentalAnalyst, SubjectType = AiReportSubject.Filing, SubjectId = "9", SessionDate = Wed, Status = AiReportStatus.Ok,
            Title = "TCS results: positive", DataJson = """{"results":true,"symbol":"TCS","surprise":"positive","summary":"Profit up 8.5% & revenue up."}""",
            CreatedUtc = At(Wed, 16, 0), UpdatedUtc = At(Wed, 16, 0),
        });
        await ai.Db.SaveChangesAsync();
        var telegram = new Recorder();
        var digest = new AiDailyDigest(ai.Db, ai.Store, new AiReportWriter(ai.Db, ai.Options), new MarketSessionService(new Calendar()), telegram, ai.Options);

        Assert.True(await digest.RunOnceAsync(At(Wed, 16, 5), default));

        string message = Assert.Single(telegram.Sent);
        Assert.Contains("Technical Analyst: 3 reports (2 ok, 1 invalid). Latest, 13:00: NIFTY bearish C.", message);
        Assert.Contains("Options/OI Analyst: 1 report (1 ok). Latest, 10:30: NIFTY range-bound.", message);
        Assert.Contains("Macro Analyst (1 ok): lean bullish, size ×0.80.", message);
        Assert.Contains("Fundamental Analyst: 1 results filing read since the last digest (1 ok): 1 positive, 0 negative, 0 inline or unclear.\n" +
                        "• TCS: positive. Profit up 8.5% &amp; revenue up.", message);

        // A day none of them ran has no analyst section; a results filing read after a digest goes in the next one.
        Assert.Empty(await DeskAnalysts.DigestSectionsAsync(ai.Db, Thu, At(Wed, 16, 5), default));
        ai.Db.AiReports.Add(new AiReport
        {
            AgentKey = AiCatalog.FundamentalAnalyst, SubjectType = AiReportSubject.Filing, SubjectId = "10", SessionDate = Wed, Status = AiReportStatus.Ok,
            Title = "INFY results: negative", DataJson = """{"results":true,"symbol":"INFY","surprise":"negative","summary":"Profit down."}""",
            CreatedUtc = At(Wed, 18, 0), UpdatedUtc = At(Wed, 18, 0),
        });
        await ai.Db.SaveChangesAsync();
        var thursday = Assert.Single(await DeskAnalysts.DigestSectionsAsync(ai.Db, Thu, At(Wed, 16, 5), default));
        Assert.StartsWith("Fundamental Analyst: 1 results filing read since the last digest (1 ok): 0 positive, 1 negative", thursday);
    }

    [Fact]
    public async Task The_reports_tab_lists_an_analyst_s_reports_and_counts_them()
    {
        var ai = Build();
        SeedSlotReports(ai.Db, today: true);
        var controller = ai.Controller();

        var page = (AiReportPage)((OkObjectResult)await controller.Reports(agent: AiCatalog.TechnicalAnalyst)).Value!;
        var stats = (AiReportStats)((OkObjectResult)await controller.ReportStats(7, default)).Value!;

        Assert.Equal(3, page.Reports.Count);
        Assert.All(page.Reports, r => Assert.Equal("Technical Analyst", r.AgentName));
        var technical = stats.Agents.Single(a => a.AgentKey == AiCatalog.TechnicalAnalyst);
        Assert.Equal((3, 2, 1, 0), (technical.Total, technical.Ok, technical.Invalid, technical.Failed));
    }

    [Fact]
    public async Task In_its_chat_an_analyst_reads_its_own_reports_and_the_facts_one_was_checked_against()
    {
        var db = NewDb();
        SeedSlotReports(db);
        var caller = new AiToolCaller(AiCatalog.TechnicalAnalyst, "console", "upendra", 1, "c");

        var list = await new AnalystReportsTool(db, new FixedClock(At(Wed, 16, 0))).RunAsync(AiToolArgs.Parse("{}").For(caller), default);
        var one = await new AnalystReportTool(db).RunAsync(AiToolArgs.Parse($$"""{"reportId":{{SeededTechnicalId}}}"""), default);

        var read = JsonDocument.Parse(JsonSerializer.Serialize(list.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;
        Assert.Equal("Technical Analyst", read.GetProperty("analyst").GetString());
        Assert.Equal(3, read.GetProperty("reports").GetArrayLength());
        Assert.Equal((2, 1), (read.GetProperty("totals").GetProperty("ok").GetInt32(), read.GetProperty("totals").GetProperty("invalid").GetInt32()));
        var full = JsonDocument.Parse(JsonSerializer.Serialize(one.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;
        Assert.Contains("\"price\":25120", full.GetProperty("factsHandedOver").GetString());
        await Assert.ThrowsAsync<AiToolArgumentException>(() => new AnalystReportsTool(db).RunAsync(AiToolArgs.Parse("""{"analyst":"astrology"}"""), default));
    }

    // ---------- the numbers check ----------

    [Theory]
    [InlineData("PCR 0.92", true)]
    [InlineData("PCR 0.9", true)]               // one place fewer
    [InlineData("PCR 1", false)]                // two fewer: not the number handed over
    [InlineData("PCR 0.95", false)]
    [InlineData("the wall at 25,300", true)]
    [InlineData("spot 25,130", true)]           // 25130.35 to the whole point
    [InlineData("spot 25,131", false)]
    [InlineData("fell 0.13%", true)]            // the sign is in words
    [InlineData("at 10:30 on 7 Oct, EMA 20 and ATR(14) on 5-minute bars, NIFTY 50, S&P 500, ATM+1", true)]
    [InlineData("EMA20 above EMA50, Q2 FY27", true)]
    [InlineData("a target of 25,500", false)]
    [InlineData("GIFT at 25,310 or 25,311", true)] // 25310.5 cut or rounded
    [InlineData("GIFT at 25,312", false)]
    [InlineData("NIFTY 26000 by the close", false)] // only NIFTY 50 is a name
    [InlineData("support at 25000 may break", false)] // a day is one or two digits
    [InlineData("3 declines", false)]                 // "decline" is not December
    public void A_text_states_only_numbers_handed_over(string text, bool allowed)
    {
        var numbers = new FactNumbers();
        numbers.Add(0.92m, 2);
        numbers.Add(25300m, 0);
        numbers.Add(25130.35m, 2);
        numbers.Add(-0.13m, 2);
        numbers.Add(25310.5m, 1);

        Assert.Equal(allowed, numbers.Unsupported(text).Count == 0);
    }

    // ---------- helpers ----------

    private const long SeededTechnicalId = 2;

    /// <summary>
    /// Wednesday's reports: macro at 08:45 (written 08:46), technical at 10:00 (10:01) and 13:00 (13:01), options at 10:30
    /// (10:31), and an invalid technical one. With <paramref name="today"/>, the same on today's date.
    /// </summary>
    private static void SeedSlotReports(TradingDbContext db, bool today = false)
    {
        var day = today ? IstTime.DateOf(DateTime.UtcNow) : Wed;
        DateTime When(int h, int m) => today ? DateTime.UtcNow.AddMinutes(-5) : At(Wed, h, m);
        AiReport Report(long id, string agent, string slot, DateTime written, string status, string title, string data) => new()
        {
            Id = id, AgentKey = agent, SubjectType = AiReportSubject.Check, SubjectId = AnalystSchedule.SubjectId(day, TimeOnly.Parse(slot, CultureInfo.InvariantCulture)),
            SessionDate = day, Status = status, Attempts = 1, Title = title, DataJson = data, CreatedUtc = written, UpdatedUtc = written,
        };

        db.AiReports.AddRange(
            Report(1, AiCatalog.MacroAnalyst, "08:45", When(8, 46), AiReportStatus.Ok, "Macro 08:45: lean bullish, size ×0.80",
                """{"slot":"08:45","bias":"lean bullish","sizeModifier":0.8,"reason":"GIFT Nifty is up."}"""),
            Report(SeededTechnicalId, AiCatalog.TechnicalAnalyst, "10:00", When(10, 1), AiReportStatus.Ok, "Technical 10:00: NIFTY bullish B",
                """{"slot":"10:00","indices":[{"underlying":"NIFTY","bias":"bullish","grade":"B","keyLevels":[{"id":"L1","price":25120,"what":"today's high"},{"id":"L4","price":24980.5,"what":"EMA 50"}],"reason":"Above both EMAs."}],"facts":{"indices":[{"underlying":"NIFTY","price":25100.25,"levels":[{"id":"L1","price":25120}]}]}}"""),
            Report(3, AiCatalog.OptionsAnalyst, "10:30", When(10, 31), AiReportStatus.Ok, "Options 10:30: NIFTY range-bound",
                """{"slot":"10:30","indices":[{"underlying":"NIFTY","stance":"range-bound","lines":["Walls at 25000 and 25300."]}]}"""),
            Report(4, AiCatalog.TechnicalAnalyst, "13:00", When(13, 1), AiReportStatus.Ok, "Technical 13:00: NIFTY bearish C",
                """{"slot":"13:00","indices":[{"underlying":"NIFTY","bias":"bearish","grade":"C","keyLevels":[],"reason":"Below the EMAs."}]}"""),
            Report(5, AiCatalog.TechnicalAnalyst, "13:30", When(13, 31), AiReportStatus.Invalid, "Technical 13:30: NIFTY bullish A",
                """{"slot":"13:30","indices":[{"underlying":"NIFTY","bias":"bullish","grade":"A","keyLevels":[],"reason":"A level made up."}]}"""));
        db.SaveChanges();
    }

    /// <summary>A session of 1-minute bars from 09:15 to 15:29: a rise with a wave in it, so it has swings.</summary>
    private static List<LiveBarResponse> Session(string symbol, DateOnly day, decimal start)
    {
        var bars = new List<LiveBarResponse>();
        for (int i = 0; i < 375; i++)
        {
            decimal mid = Math.Round(start + i * 0.2m + (decimal)(30 * Math.Sin(i / 15.0)), 2);
            bars.Add(new LiveBarResponse
            {
                Symbol = symbol, Resolution = "1m", BarStartUtc = At(day, 9, 15).AddMinutes(i),
                Open = mid - 0.5m, High = mid + 2m, Low = mid - 2m, Close = mid + 0.5m,
            });
        }

        return bars;
    }

    private static TechnicalFacts TechnicalFactsOf(DateTime asOf) => new()
    {
        AsOfUtc = asOf,
        Indices =
        [
            TechnicalAnalystAgent.Build("NIFTY", Session(NiftySpot, Tue, 25000m).Concat(Session(NiftySpot, Wed, 25100m)).ToList(), asOf),
            TechnicalAnalystAgent.Build("BANKNIFTY", [], asOf),
            TechnicalAnalystAgent.Build("SENSEX", [], asOf),
        ],
    };

    private static async Task SeedBarsAsync(TradingDbContext db, string symbol, IEnumerable<LiveBarResponse> bars)
    {
        db.LiveBars.AddRange(bars.Select(b => new LiveBar
        {
            Symbol = symbol, Resolution = "1m", BarStartUtc = b.BarStartUtc, Open = b.Open, High = b.High, Low = b.Low, Close = b.Close,
        }));
        await db.SaveChangesAsync();
    }

    /// <summary>A NIFTY chain captured at <paramref name="captured"/>: the call wall at 25300, the put wall at 25000.</summary>
    private static void SeedChain(TradingDbContext db, DateTime captured, decimal spot, decimal oiScale, decimal iv)
    {
        var expiry = new DateOnly(2026, 10, 13);
        foreach (decimal strike in new[] { 25000m, 25050m, 25100m, 25150m, 25200m, 25250m, 25300m })
        {
            foreach (string type in new[] { "CE", "PE" })
            {
                long oi = type == "CE"
                    ? strike switch { 25300m => 900_000, 25250m => 700_000, _ => 200_000 }
                    : strike switch { 25000m => 800_000, 25050m => 600_000, _ => 150_000 };
                db.OptionChainSnapshots.Add(new OptionChainSnapshot
                {
                    Underlying = "NIFTY", ExpiryDate = expiry, StrikePrice = strike, OptionType = type, Symbol = $"NSE:NIFTY26O13{strike:0}{type}",
                    CapturedUtc = captured, SpotPrice = spot, LastTradedPrice = 50m, BidPrice = 49.5m, AskPrice = 50.5m,
                    OpenInterest = (long)(oi * oiScale), OpenInterestAtOpen = oi, ImpliedVolatility = iv, SourceKey = "dhan",
                });
            }
        }

        db.SaveChanges();
    }

    /// <summary>NIFTY and India VIX closes, GIFT Nifty and the S&amp;P futures this morning, an S&amp;P close, cash flows and an event.</summary>
    private static void SeedMacro(TradingDbContext db)
    {
        void Close(string symbol, DateOnly day, decimal close) =>
            db.LiveBars.Add(new LiveBar { Symbol = symbol, Resolution = "1m", BarStartUtc = At(day, 15, 29), Open = close, High = close, Low = close, Close = close });
        Close(NiftySpot, Tue, 25000m);
        Close(NiftySpot, Wed, 25200m);
        Close(MarketBriefBuilder.VixSymbol, Tue, 12m);
        Close(MarketBriefBuilder.VixSymbol, Wed, 12.6m);
        db.MarketQuoteSnapshots.AddRange(
            new MarketQuoteSnapshot { Key = "GIFTNIFTY", Price = 25310.5m, ChangePct = 0.42m, FetchedUtc = At(Thu, 8, 30), Source = "nseix" },
            new MarketQuoteSnapshot { Key = "GIFTNIFTY", Price = 25999m, ChangePct = 3m, FetchedUtc = At(Thu, 9, 0), Source = "nseix" },
            new MarketQuoteSnapshot { Key = "ES", Price = 6550m, ChangePct = 0.21m, FetchedUtc = At(Thu, 8, 30), Source = "yahoo:ES=F" });
        db.MarketGlobalDaily.AddRange(
            new MarketGlobalDaily { Symbol = "SPX", Date = Tue, Close = 6500m, Source = "yahoo:^GSPC" },
            new MarketGlobalDaily { Symbol = "SPX", Date = Wed, Close = 6532.5m, Source = "yahoo:^GSPC" },
            new MarketGlobalDaily { Symbol = "SPX", Date = Thu, Close = 1m, Source = "yahoo:^GSPC" });   // today's: not closed by 08:45
        db.MarketCashFlows.AddRange(
            new MarketCashFlow { Date = Wed, Category = "FII/FPI", NetValueCrore = -1234.56m },
            new MarketCashFlow { Date = Wed, Category = "DII", NetValueCrore = 2345.67m },
            new MarketCashFlow { Date = Tue, Category = "FII/FPI", NetValueCrore = -500m });
        db.MarketEvents.Add(new MarketEvent { Date = Thu, TimeIst = new TimeOnly(10, 0), Category = "Policy", Title = "RBI policy decision", Importance = 3 });
        db.SaveChanges();
    }

    private static long Filing(TradingDbContext db, string symbol, string subject, string details, DateTime announcedUtc)
    {
        var a = new CorporateAnnouncement
        {
            Symbol = symbol, Company = symbol, Subject = subject, Details = details, AnnouncedUtc = announcedUtc, FirstSeenUtc = announcedUtc,
            UniqueKey = Guid.NewGuid().ToString("N"),
        };
        db.CorporateAnnouncements.Add(a);
        db.SaveChanges();
        return a.Id;
    }

    private static Services Build() => AiTestKit.Build(Settings());

    private static LiveDataService Live(TradingDbContext db) => new(db, new NoProviders(), new MarketSessionService(new Calendar()));

    private static OptionChainService Chains(TradingDbContext db) =>
        new(db, new MarketSessionService(new Calendar()), new MarketBriefBuilderTests.Lots(65));

    private static TechnicalAnalystAgent Technical(Services ai, IMarketSessionService? sessions = null) => new(
        ai.Db, ai.Gateway, new AiReportWriter(ai.Db, ai.Options), Live(ai.Db), sessions ?? new MarketSessionService(new Calendar()),
        NullLogger<TechnicalAnalystAgent>.Instance);

    private static OptionsAnalystAgent Options(Services ai, IMarketSessionService? sessions = null) => new(
        Chains(ai.Db), ai.Gateway, new AiReportWriter(ai.Db, ai.Options), sessions ?? new MarketSessionService(new Calendar()),
        NullLogger<OptionsAnalystAgent>.Instance);

    private static MacroAnalystAgent Macro(Services ai, IMarketSessionService? sessions = null, TimeProvider? reportClock = null) => new(
        ai.Db, Live(ai.Db), new MarketFactorsQueries(ai.Db, new MarketFactorsStatus()), ai.Gateway, new AiReportWriter(ai.Db, ai.Options, reportClock),
        sessions ?? new MarketSessionService(new Calendar()), NullLogger<MacroAnalystAgent>.Instance);

    private static FundamentalAnalystAgent Fundamental(Services ai, AiSchedulerState? state = null) => new(
        ai.Db, ai.Gateway, new AiReportWriter(ai.Db, ai.Options), state ?? new AiSchedulerState(), ai.Options, NullLogger<FundamentalAnalystAgent>.Instance);

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    /// <summary>Weekends (the session service's own rule) and these dates closed; every year loaded.</summary>
    private sealed class Calendar(params DateOnly[] holidays) : IMarketCalendar
    {
        public bool IsLoaded => true;

        public MarketHoliday? HolidayOn(string exchange, DateOnly date) =>
            holidays.Contains(date) ? new MarketHoliday { Exchange = exchange, Date = date, Name = "Holiday", Closure = MarketClosure.FullDay } : null;

        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date) => null;

        public bool HasYear(string exchange, int year) => true;

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoProviders : IProviderCatalog
    {
        public IReadOnlyList<ProviderDescriptor> Descriptors => [];

        public ProviderDescriptor? Find(string providerKey) => null;
    }

    /// <summary>What would have gone to Telegram: the messages.</summary>
    private sealed class Recorder : ISystemNotifier
    {
        public List<string> Sent { get; } = [];

        public Task NotifyAsync(NotificationCategory category, NotificationSeverity severity, string title, string message,
            string? underlying = null, string? symbol = null, long? simulationRunId = null, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
