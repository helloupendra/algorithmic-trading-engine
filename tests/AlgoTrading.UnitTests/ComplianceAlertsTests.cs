using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Api.Services.AiVoice;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// A circular that applies to the desk is told once, directly: the Desk System channel and Ira, never for "does not
/// apply" or "unclear", never twice for one circular. And the owner marks it handled. The circular is SEBI's of 15 Oct
/// 2026 that moves the index F&amp;O close to 3:45 p.m. from 7 Dec 2026.
/// </summary>
public class ComplianceAlertsTests
{
    private static readonly DateTime Now = IstTime.FromIst(new DateTime(2026, 10, 18, 21, 0, 0));

    // ---------- the alert ----------

    [Fact]
    public async Task A_circular_that_applies_is_sent_to_the_desk_system_channel_once_and_never_again()
    {
        var rig = new Rig();
        var report = rig.Report(41, ComplianceTriageAgent.Check(AiOfflineAgentsTests.Record23843(), AiOfflineAgentsTests.Sebi23843()));

        var row = await rig.Alerts.RaiseAsync(report, CancellationToken.None);

        Assert.NotNull(row);
        Assert.True(row!.AlertDelivered);
        var (chat, text, mode) = Assert.Single(rig.Telegram.Sent);
        Assert.Equal(("-100222", "HTML"), (chat, mode));   // the Desk System channel, not the trades one
        Assert.StartsWith("<b>Rule change: applies to the desk</b>\n<b>Review of certain aspects of the Closing Auction Session", text);
        Assert.Contains("• 7 Dec 2026: The modifications other than para 4.8 come into effect from December 07, 2026.", text);
        Assert.Contains("The cash segment&#39;s Closing Auction Session runs from 3:30 p.m. to 3:40 p.m.", text);

        // Triaged again (a re-read, a "Run now"), caught up on the next pass: nothing more is sent.
        var again = rig.Report(41, ComplianceTriageAgent.Check(AiOfflineAgentsTests.Record23843(), AiOfflineAgentsTests.Sebi23843()), id: 99);
        Assert.Null(await rig.Alerts.RaiseAsync(again, CancellationToken.None));
        Assert.Equal(0, await rig.Alerts.CatchUpAsync(CancellationToken.None));
        Assert.Single(rig.Telegram.Sent);
        Assert.Single(await rig.Db.ComplianceFollowUps.ToListAsync());
    }

    [Fact]
    public void The_alert_for_the_15_Oct_circular_reads_as_the_owner_gets_it()
    {
        var v = ComplianceTriageAgent.Check(AiOfflineAgentsTests.Record23843(), AiOfflineAgentsTests.Sebi23843());

        string text = ComplianceAlertText.Plain(AiOfflineAgentsTests.Circular23843Row(), ComplianceTriageAgent.ReadFacts(v.Data)!, 412);

        Assert.Equal("""
            Rule change: applies to the desk
            Review of certain aspects of the Closing Auction Session, market timings and settlement methodology for derivatives contracts
            SEBI circular HO/47/11/11(3)2025-MRD-POD2/I/23843/2026 of 15 Oct 2026: applies to the desk (market hours).
            Takes effect:
            • 7 Dec 2026: The modifications other than para 4.8 come into effect from December 07, 2026.
            • 8 Feb 2027: Para 4.8 comes into effect from February 08, 2027.
            On the desk:
            • Index derivatives trade until 3:45 p.m. on all trading days; expiring index derivatives contracts stop at 3:30 p.m. on expiry days.
            • Stock derivatives trade until 3:45 p.m. on all trading days, including on expiry days.
            • The cash segment's Closing Auction Session runs from 3:30 p.m. to 3:40 p.m.
            • The post close session in the cash segment runs between 3:50 p.m. to 4:00 p.m.
            • The expiry settlement price of index derivatives is the VWAP of the constituent securities during the last 30 minutes of the CTS.
            https://www.sebi.gov.in/legal/circulars/oct-2026/review_105210.html
            Triage report #412 on AI → Reports: mark it handled there once the desk is ready.
            """, text);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("unclear")]
    public async Task Nothing_is_sent_for_a_circular_that_does_not_apply_or_is_unclear(string verdict)
    {
        var rig = new Rig();
        var record = AiOfflineAgentsTests.Record23843();
        record["applies"] = verdict;
        var report = rig.Report(41, ComplianceTriageAgent.Check(record, AiOfflineAgentsTests.Sebi23843()));
        Assert.Equal(AiReportStatus.Ok, report.Status);

        Assert.Null(await rig.Alerts.RaiseAsync(report, CancellationToken.None));
        Assert.Equal(0, await rig.Alerts.CatchUpAsync(CancellationToken.None));
        Assert.Empty(rig.Telegram.Sent);
        Assert.Empty(await rig.Db.ComplianceFollowUps.ToListAsync());
    }

    [Fact]
    public async Task An_invalid_report_or_one_written_before_the_dates_raises_nothing()
    {
        var rig = new Rig();
        var invalid = rig.Report(41, ComplianceTriageAgent.Check(AiOfflineAgentsTests.Record23843(dates: """[{"date":"2026-12-01","what":"x"}]"""),
            AiOfflineAgentsTests.Sebi23843()));
        Assert.Equal(AiReportStatus.Invalid, invalid.Status);
        // 17 Oct 2026: the verdict that applied, with no dates in it. It stays in the digest and on Today.
        var old = rig.Report(41, new ComplianceTriageAgent.Verdict(AiReportStatus.Ok, "Applies: Review", "body",
            """{"circularId":41,"applies":"yes","area":"market hours","why":"x","change":"y","quote":"z"}""", string.Empty), id: 77);

        Assert.Null(await rig.Alerts.RaiseAsync(invalid, CancellationToken.None));
        Assert.Null(await rig.Alerts.RaiseAsync(old, CancellationToken.None));
        Assert.Empty(rig.Telegram.Sent);
    }

    [Fact]
    public async Task The_triage_alerts_as_it_writes_and_a_second_triage_of_the_same_circular_sends_nothing()
    {
        var ai = Build();
        await ai.Store.SetAgentEnabledAsync(AiCatalog.ComplianceTriage, true, "upendra", null);
        ai.Db.RegulatoryCirculars.Add(AiOfflineAgentsTests.Circular23843Row());
        await ai.Db.SaveChangesAsync();
        var telegram = new FakeTelegram();
        var alerts = new ComplianceAlerts(ai.Db, Sender(telegram), NullLogger<ComplianceAlerts>.Instance, new AiAgentsTests.FixedTime(Now));
        string answer = new JsonObject { ["circulars"] = new JsonArray(AiOfflineAgentsTests.Record23843()) }.ToJsonString();
        ai.Provider.On(Extract1, Answer(answer), Answer(answer));
        var agent = new ComplianceTriageAgent(ai.Db, ai.Gateway, new AiReportWriter(ai.Db, ai.Options, new AiAgentsTests.FixedTime(Now)),
            new MarketSessionService(new MarketIntelligenceTestKit.Calendar()), new AiSchedulerState(), ai.Options,
            NullLogger<ComplianceTriageAgent>.Instance, new AiAgentsTests.FixedTime(Now), alerts);

        Assert.Equal(AiReportStatus.Ok, (await agent.RunForAsync("41", CancellationToken.None))!.Status);
        Assert.Equal(AiReportStatus.Ok, (await agent.RunForAsync("41", CancellationToken.None))!.Status);

        Assert.Single(telegram.Sent);
        var row = Assert.Single(await ai.Db.ComplianceFollowUps.AsNoTracking().ToListAsync());
        Assert.Equal(41, row.CircularId);
        Assert.StartsWith("Rule change: applies to the desk\n", row.AlertText);
    }

    // ---------- Ira ----------

    [Fact]
    public async Task Ira_tells_the_alert_once_with_the_dates_it_takes_effect()
    {
        var rig = new Rig();
        var report = rig.Report(41, ComplianceTriageAgent.Check(AiOfflineAgentsTests.Record23843(), AiOfflineAgentsTests.Sebi23843()));
        await rig.Alerts.RaiseAsync(report, CancellationToken.None);
        var desk = new IraDesk(rig.Db, new AlgoTrading.Api.Services.RunPnl(rig.Db, new PositionGreeksTests.FixedLots(65),
            new AlgoTrading.Api.Services.RunCharges(rig.Db, new PositionGreeksTests.FixedLots(65))));
        var marks = new IraMarks();

        var first = await desk.EventsAsync(marks, Now.AddMinutes(1), CancellationToken.None);
        var second = await desk.EventsAsync(marks, Now.AddMinutes(2), CancellationToken.None);

        var told = Assert.Single(first);
        Assert.Equal(IraDesk.ComplianceKind, told.Kind);
        Assert.Equal(
            "A SEBI circular applies to the desk: Review of certain aspects of the Closing Auction Session, market timings and settlement " +
            "methodology for derivatives contracts. It takes effect on 7 December 2026 and 8 February 2027. What changes is on the Desk System channel.",
            told.Text);
        Assert.True(told.Text.Length <= IraAlertText.MaxChars);
        Assert.Empty(second);
    }

    // ---------- marking it handled ----------

    [Fact]
    public async Task Marking_a_circular_handled_keeps_who_when_and_the_note_and_the_list_says_so()
    {
        var ai = Build();
        ai.Db.RegulatoryCirculars.Add(AiOfflineAgentsTests.Circular23843Row());
        var v = ComplianceTriageAgent.Check(AiOfflineAgentsTests.Record23843(), AiOfflineAgentsTests.Sebi23843());
        ai.Db.AiReports.AddRange(
            new AiReport { Id = 412, AgentKey = AiCatalog.ComplianceTriage, SubjectType = AiReportSubject.Check, SubjectId = "circular:41",
                Status = AiReportStatus.Ok, Title = v.Title, Body = v.Body, DataJson = v.Data, CreatedUtc = Now, UpdatedUtc = Now },
            new AiReport { Id = 413, AgentKey = AiCatalog.TradeReviewer, SubjectType = AiReportSubject.Run, SubjectId = "7",
                Status = AiReportStatus.Ok, Title = "Run 7", CreatedUtc = Now, UpdatedUtc = Now });
        await ai.Db.SaveChangesAsync();
        var controller = ai.Controller();

        var before = Body<AiComplianceList>(await controller.Compliance(CancellationToken.None));
        var item = Assert.Single(before.Circulars);
        Assert.Equal((412L, 41L, "SEBI", "market hours"), (item.ReportId, item.CircularId, item.Source, item.Area));
        Assert.Equal(new[] { "2026-12-07", "2027-02-08" }, item.EffectiveDates.Select(d => d.Date));
        Assert.Equal(5, item.DeskChanges.Count);
        Assert.Null(item.FollowUp);

        Assert.IsType<BadRequestObjectResult>(await controller.MarkComplianceHandled(412, new AiComplianceHandledRequest("  "), CancellationToken.None));
        Assert.IsType<NotFoundObjectResult>(await controller.MarkComplianceHandled(999, new AiComplianceHandledRequest("Done."), CancellationToken.None));
        Assert.IsType<ConflictObjectResult>(await controller.MarkComplianceHandled(413, new AiComplianceHandledRequest("Done."), CancellationToken.None));

        var handled = Body<AiComplianceFollowUpDto>(await controller.MarkComplianceHandled(412,
            new AiComplianceHandledRequest("Session close moved to 15:45 for index options, date-gated from 7 Dec."), CancellationToken.None));

        Assert.Equal(("upendra", "Session close moved to 15:45 for index options, date-gated from 7 Dec."), (handled.HandledBy, handled.Note));
        Assert.NotNull(handled.HandledUtc);
        Assert.Null(handled.AlertedUtc);
        var after = Assert.Single(Body<AiComplianceList>(await controller.Compliance(CancellationToken.None)).Circulars);
        Assert.NotNull(after.FollowUp!.HandledUtc);
        Assert.Equal("upendra", Body<AiReportDetail>(await controller.Report(412, CancellationToken.None)).FollowUp!.HandledBy);

        // A circular handled before its alert went is never alerted.
        var telegram = new FakeTelegram();
        var alerts = new ComplianceAlerts(ai.Db, Sender(telegram), NullLogger<ComplianceAlerts>.Instance, new AiAgentsTests.FixedTime(Now));
        Assert.Null(await alerts.RaiseAsync(await ai.Db.AiReports.SingleAsync(r => r.Id == 412), CancellationToken.None));
        Assert.Empty(telegram.Sent);
    }

    // ---------- helpers ----------

    private static T Body<T>(IActionResult result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);

    /// <summary>A database with the 15 Oct circular, the alerts over a sender whose Telegram is a fake.</summary>
    private sealed class Rig
    {
        public TradingDbContext Db { get; } = NewDb();
        public FakeTelegram Telegram { get; } = new();
        public ComplianceAlerts Alerts { get; }

        public Rig()
        {
            Db.RegulatoryCirculars.Add(AiOfflineAgentsTests.Circular23843Row());
            Db.SaveChanges();
            Alerts = new ComplianceAlerts(Db, Sender(Telegram), NullLogger<ComplianceAlerts>.Instance, new AiAgentsTests.FixedTime(Now));
        }

        /// <summary>The triage's report on a circular, as the agent writes it.</summary>
        public AiReport Report(long circularId, ComplianceTriageAgent.Verdict v, long id = 0)
        {
            var report = new AiReport
            {
                Id = id, AgentKey = AiCatalog.ComplianceTriage, SubjectType = AiReportSubject.Check, SubjectId = ComplianceTriageAgent.Subject(circularId),
                Status = v.Status, Title = v.Title, Body = v.Body, DataJson = v.Data, Error = v.Error, Attempts = 1, CreatedUtc = Now, UpdatedUtc = Now,
            };
            Db.AiReports.Add(report);
            Db.SaveChanges();
            return report;
        }
    }

    private static TelegramSender Sender(FakeTelegram telegram) => new(new TelegramFactory(telegram),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = "123:abc",
            ["Telegram:ChatId"] = "-100111",
            ["Telegram:SystemChatId"] = "-100222",
        }).Build(), NullLogger<TelegramSender>.Instance, delay: (_, _) => Task.CompletedTask, callerWait: TimeSpan.FromSeconds(30));

    /// <summary>api.telegram.org, faked: each message's chat, text and parse mode.</summary>
    private sealed class FakeTelegram : HttpMessageHandler
    {
        private readonly ConcurrentQueue<(string Chat, string Text, string Mode)> _sent = new();

        public List<(string Chat, string Text, string Mode)> Sent => [.. _sent];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            _sent.Enqueue((root.GetProperty("chat_id").GetString() ?? string.Empty, root.GetProperty("text").GetString() ?? string.Empty,
                root.TryGetProperty("parse_mode", out var mode) ? mode.GetString() ?? string.Empty : string.Empty));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true}""") };
        }
    }

    private sealed class TelegramFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
