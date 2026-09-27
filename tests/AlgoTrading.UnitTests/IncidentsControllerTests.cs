using System.Security.Claims;
using System.Text.RegularExpressions;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The console's side of Sentinel's incidents: listing what Sentinel wrote,
/// the things a person may do with it, and the record of what was learnt.
/// </summary>
/// <remarks>
/// The rows here are written the way Sentinel's <c>store.py</c> writes them,
/// straight into the table, because that is the only way they arrive in
/// production. What is worth pinning down: the default list is what still
/// needs a person, a resolved incident cannot be acknowledged back to life,
/// a click never overwrites a resolve Sentinel made a moment earlier, a
/// malformed evidence column is shown rather than dropped, nothing
/// secret-shaped reaches the browser, and a stopped Sentinel cannot pass for a
/// quiet desk. And for the knowledge record: notes are masked before they are
/// stored, a field left out is never wiped, every incident knows how often its
/// problem happened before and how it ended last time, and the history's
/// numbers agree with the episodes listed under them.
/// </remarks>
public class IncidentsControllerTests
{
    private static readonly DateTime T0 = new(2026, 9, 24, 5, 57, 35, DateTimeKind.Utc);

    [Fact]
    public async Task The_default_list_is_what_is_live_newest_sighting_first()
    {
        string name = NewName();
        await using var db = NewDb(name);
        db.Incidents.AddRange(
            Row("feed-silent:dhan", status: IncidentStatus.Open, lastSeen: T0.AddMinutes(1)),
            Row("run-missing:admin:Ghost", status: IncidentStatus.Acknowledged, lastSeen: T0.AddMinutes(5)),
            Row("runner-crash:429", status: IncidentStatus.Resolved, lastSeen: T0.AddMinutes(9)));
        await db.SaveChangesAsync();

        var items = await List(Controller(db));

        Assert.Equal(new[] { "run-missing:admin:Ghost", "feed-silent:dhan" }, items.Select(i => i.Fingerprint));
    }

    [Theory]
    [InlineData("open", new[] { "a" })]
    [InlineData("acknowledged", new[] { "b" })]
    [InlineData("resolved", new[] { "c" })]
    [InlineData("live", new[] { "b", "a" })]
    [InlineData("any", new[] { "c", "b", "a" })]
    [InlineData("ANY", new[] { "c", "b", "a" })]
    public async Task Each_status_filter_returns_its_rows(string status, string[] expected)
    {
        await using var db = NewDb(NewName());
        db.Incidents.AddRange(
            Row("a", status: IncidentStatus.Open, lastSeen: T0),
            Row("b", status: IncidentStatus.Acknowledged, lastSeen: T0.AddMinutes(1)),
            Row("c", status: IncidentStatus.Resolved, lastSeen: T0.AddMinutes(2)));
        await db.SaveChangesAsync();

        var items = await List(Controller(db), status: status);

        Assert.Equal(expected, items.Select(i => i.Fingerprint));
    }

    [Fact]
    public async Task Severity_and_agent_filter_and_take_caps_the_list()
    {
        await using var db = NewDb(NewName());
        db.Incidents.AddRange(
            Row("feeds-1", agent: "health", severity: IncidentSeverity.Critical, lastSeen: T0),
            Row("feeds-2", agent: "health", severity: IncidentSeverity.Low, lastSeen: T0.AddMinutes(1)),
            Row("runs-1", agent: "trading", severity: IncidentSeverity.Critical, lastSeen: T0.AddMinutes(2)),
            Row("runs-2", agent: "trading", severity: IncidentSeverity.High, lastSeen: T0.AddMinutes(3)));
        await db.SaveChangesAsync();
        var controller = Controller(db);

        var critical = await List(controller, severity: "critical");
        Assert.Equal(new[] { "runs-1", "feeds-1" }, critical.Select(i => i.Fingerprint));

        var loud = await List(controller, severity: "high, critical");
        Assert.Equal(new[] { "runs-2", "runs-1", "feeds-1" }, loud.Select(i => i.Fingerprint));

        var health = await List(controller, agent: "health");
        Assert.Equal(new[] { "feeds-2", "feeds-1" }, health.Select(i => i.Fingerprint));

        var one = await List(controller, take: 1);
        Assert.Equal(new[] { "runs-2" }, one.Select(i => i.Fingerprint));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(10000, 500)]
    public async Task Take_is_clamped_between_one_and_five_hundred(int take, int expected)
    {
        await using var db = NewDb(NewName());
        db.Incidents.AddRange(Enumerable.Range(0, 502).Select(i => Row($"f-{i}", lastSeen: T0.AddSeconds(i))));
        await db.SaveChangesAsync();

        var items = await List(Controller(db), take: take);

        Assert.Equal(expected, items.Count);
        Assert.Equal("f-501", items[0].Fingerprint);
    }

    [Theory]
    [InlineData("closed", null)]
    [InlineData(null, "urgent")]
    public async Task An_unknown_filter_value_is_refused_rather_than_ignored(string? status, string? severity)
    {
        // Ignoring it would answer an empty or unfiltered list, and an empty
        // list on this page reads as "nothing is wrong".
        await using var db = NewDb(NewName());

        var result = await Controller(db).List(status, severity, agent: null);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Summary_counts_live_incidents_by_severity_and_names_the_newest()
    {
        await using var db = NewDb(NewName());
        db.Incidents.AddRange(
            Row("a", severity: IncidentSeverity.High, firstSeen: T0, lastSeen: T0.AddMinutes(30)),
            Row("b", severity: IncidentSeverity.High, status: IncidentStatus.Acknowledged, firstSeen: T0.AddMinutes(10), title: "Dhan feed keeps reconnecting with no ticks"),
            Row("c", severity: IncidentSeverity.Low, firstSeen: T0.AddMinutes(2), lastSeen: T0.AddMinutes(40)),
            // Resolved rows are history, not what is wrong now.
            Row("d", severity: IncidentSeverity.Critical, status: IncidentStatus.Resolved, firstSeen: T0.AddMinutes(20)));
        await db.SaveChangesAsync();

        var result = await Controller(db).Summary(CancellationToken.None);

        var summary = Assert.IsType<IncidentSummary>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(3, summary.Live);
        Assert.Equal(0, summary.Counts[IncidentSeverity.Critical]);
        Assert.Equal(2, summary.Counts[IncidentSeverity.High]);
        Assert.Equal(0, summary.Counts[IncidentSeverity.Medium]);
        Assert.Equal(1, summary.Counts[IncidentSeverity.Low]);
        Assert.Equal(IncidentSeverity.High, summary.WorstSeverity);

        // Newest by when it started, not by when it was last re-seen.
        Assert.Equal("Dhan feed keeps reconnecting with no ticks", summary.NewestTitle);
        Assert.Equal(T0.AddMinutes(10), summary.NewestUtc);
    }

    [Fact]
    public async Task Summary_of_a_quiet_desk_has_real_zeros_and_no_newest()
    {
        await using var db = NewDb(NewName());
        db.Incidents.Add(Row("old", status: IncidentStatus.Resolved));
        await db.SaveChangesAsync();

        var result = await Controller(db).Summary(CancellationToken.None);

        var summary = Assert.IsType<IncidentSummary>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(0, summary.Live);
        Assert.All(IncidentSeverity.All, s => Assert.Equal(0, summary.Counts[s]));
        Assert.Null(summary.WorstSeverity);
        Assert.Null(summary.NewestId);
        Assert.Null(summary.NewestTitle);
        Assert.Null(summary.NewestUtc);
    }

    [Fact]
    public async Task Summary_says_so_when_Sentinel_has_never_reported_a_check()
    {
        // Zero live incidents and no heartbeat: the console must not read that
        // as "all clear". Null is "not known", which the page says out loud.
        await using var db = NewDb(NewName());

        var summary = await SummaryOf(Controller(db));

        Assert.Equal(0, summary.Live);
        Assert.Null(summary.LastCheckUtc);
    }

    [Fact]
    public async Task Summary_returns_Sentinel_s_last_check_as_stored_however_old()
    {
        // Hours old is exactly the case the console must be able to see: the
        // API reports it, the page decides it is stale.
        await using var db = NewDb(NewName());
        var lastCheck = T0.AddDays(-2);
        db.SentinelHeartbeats.Add(new SentinelHeartbeat { LastCheckUtc = lastCheck });
        await db.SaveChangesAsync();

        var summary = await SummaryOf(Controller(db));

        Assert.Equal(lastCheck, summary.LastCheckUtc);
        Assert.Equal(DateTimeKind.Utc, summary.LastCheckUtc!.Value.Kind);
    }

    [Fact]
    public async Task Get_returns_one_incident_or_404()
    {
        await using var db = NewDb(NewName());
        var row = Row("feed-silent:dhan", evidence: "[\"no tick since 11:27:35\",\"8 reconnects\"]");
        db.Incidents.Add(row);
        await db.SaveChangesAsync();
        var controller = Controller(db);

        var view = Assert.IsType<IncidentView>(Assert.IsType<OkObjectResult>(await controller.Get(row.Id, CancellationToken.None)).Value);
        Assert.Equal("feed-silent:dhan", view.Fingerprint);
        Assert.Equal(new[] { "no tick since 11:27:35", "8 reconnects" }, view.Evidence);

        Assert.IsType<NotFoundObjectResult>(await controller.Get(row.Id + 1000, CancellationToken.None));
    }

    [Fact]
    public async Task Acknowledging_an_open_incident_records_who_and_when()
    {
        await using var db = NewDb(NewName());
        var row = Row("feed-silent:dhan");
        db.Incidents.Add(row);
        await db.SaveChangesAsync();

        var before = DateTime.UtcNow;
        var result = await Controller(db, userName: "upendra").Acknowledge(row.Id, CancellationToken.None);

        var view = Assert.IsType<IncidentView>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(IncidentStatus.Acknowledged, view.Status);
        Assert.Equal("upendra", view.AcknowledgedBy);
        Assert.NotNull(view.AcknowledgedUtc);
        Assert.True(view.AcknowledgedUtc >= before);

        var stored = await db.Incidents.AsNoTracking().SingleAsync();
        Assert.Equal(IncidentStatus.Acknowledged, stored.Status);
        Assert.Equal("upendra", stored.AcknowledgedBy);
        Assert.Null(stored.ResolvedUtc);
    }

    [Theory]
    [InlineData(IncidentStatus.Acknowledged)]
    [InlineData(IncidentStatus.Resolved)]
    public async Task Only_an_open_incident_can_be_acknowledged(string status)
    {
        await using var db = NewDb(NewName());
        var row = Row("x", status: status);
        db.Incidents.Add(row);
        await db.SaveChangesAsync();

        var result = await Controller(db).Acknowledge(row.Id, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(status, (await db.Incidents.AsNoTracking().SingleAsync()).Status);
    }

    [Theory]
    [InlineData(IncidentStatus.Open)]
    [InlineData(IncidentStatus.Acknowledged)]
    public async Task Resolving_works_from_open_or_acknowledged(string status)
    {
        await using var db = NewDb(NewName());
        var row = Row("x", status: status);
        db.Incidents.Add(row);
        await db.SaveChangesAsync();

        var before = DateTime.UtcNow;
        var result = await Controller(db, userName: "upendra").Resolve(row.Id, null, CancellationToken.None);

        var view = Assert.IsType<IncidentView>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(IncidentStatus.Resolved, view.Status);
        Assert.NotNull(view.ResolvedUtc);
        Assert.True(view.ResolvedUtc >= before);
        Assert.Equal("upendra", view.ResolvedBy);

        var stored = await db.Incidents.AsNoTracking().SingleAsync();
        Assert.Equal(IncidentStatus.Resolved, stored.Status);
        Assert.Equal("upendra", stored.ResolvedBy);
    }

    [Fact]
    public async Task A_resolve_Sentinel_made_shows_no_person()
    {
        // store.py's resolve sets Status and ResolvedUtc only. Null ResolvedBy
        // is how the history says "it cleared" rather than "someone closed it".
        await using var db = NewDb(NewName());
        var row = Row("x", status: IncidentStatus.Resolved);
        row.ResolvedUtc = T0.AddMinutes(5);
        db.Incidents.Add(row);
        await db.SaveChangesAsync();

        var view = await GetView(Controller(db), row.Id);

        Assert.Equal(T0.AddMinutes(5), view.ResolvedUtc);
        Assert.Null(view.ResolvedBy);
    }

    [Fact]
    public async Task Resolving_a_resolved_incident_is_a_conflict_and_keeps_its_time()
    {
        await using var db = NewDb(NewName());
        var resolvedAt = T0.AddHours(1);
        var row = Row("x", status: IncidentStatus.Resolved);
        row.ResolvedUtc = resolvedAt;
        db.Incidents.Add(row);
        await db.SaveChangesAsync();

        var result = await Controller(db).Resolve(row.Id, null, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(resolvedAt, (await db.Incidents.AsNoTracking().SingleAsync()).ResolvedUtc);
    }

    [Fact]
    public async Task Acknowledge_and_resolve_of_a_missing_incident_are_404()
    {
        await using var db = NewDb(NewName());
        var controller = Controller(db);

        Assert.IsType<NotFoundObjectResult>(await controller.Acknowledge(42, CancellationToken.None));
        Assert.IsType<NotFoundObjectResult>(await controller.Resolve(42, null, CancellationToken.None));
    }

    [Fact]
    public async Task An_acknowledge_that_lands_after_Sentinel_resolved_the_row_is_a_conflict_not_a_revival()
    {
        // The console read the row while it was open; Sentinel's clean checks
        // resolved it before the click was saved. Saving the click would put a
        // finished incident back to acknowledged, live for good.
        string name = NewName();
        await using var console = NewDb(name);
        var row = Row("feed-silent:dhan");
        console.Incidents.Add(row);
        await console.SaveChangesAsync();

        await using (var sentinel = NewDb(name))
        {
            var same = await sentinel.Incidents.SingleAsync();
            same.Status = IncidentStatus.Resolved;
            same.ResolvedUtc = T0.AddMinutes(3);
            await sentinel.SaveChangesAsync();
        }

        // The console's context still holds the row as it read it: open.
        var result = await Controller(console).Acknowledge(row.Id, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        await using var check = NewDb(name);
        var stored = await check.Incidents.SingleAsync();
        Assert.Equal(IncidentStatus.Resolved, stored.Status);
        Assert.Null(stored.AcknowledgedBy);
    }

    [Fact]
    public async Task Sentinel_re_seeing_the_row_does_not_block_an_acknowledge_or_get_overwritten_by_it()
    {
        // Sentinel updates every live row on every check, about every thirty
        // seconds. Only Status is a concurrency token, so an acknowledge made
        // between two sightings lands, and the sighting it did not read is kept.
        // A token on the whole row would make this a 409 during every active
        // incident, which is when people press the button.
        string name = NewName();
        await using var console = NewDb(name);
        var row = Row("feed-silent:dhan", severity: IncidentSeverity.Medium);
        console.Incidents.Add(row);
        await console.SaveChangesAsync();

        var seenAgain = T0.AddMinutes(1);
        await using (var sentinel = NewDb(name))
        {
            var same = await sentinel.Incidents.SingleAsync();
            same.Occurrences += 1;
            same.LastSeenUtc = seenAgain;
            same.Severity = IncidentSeverity.High;
            same.EvidenceJson = "[\"no tick for 95s\"]";
            await sentinel.SaveChangesAsync();
        }

        // The console's context still holds the row as it first read it.
        var result = await Controller(console, userName: "upendra").Acknowledge(row.Id, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        await using var check = NewDb(name);
        var stored = await check.Incidents.SingleAsync();
        Assert.Equal(IncidentStatus.Acknowledged, stored.Status);
        Assert.Equal("upendra", stored.AcknowledgedBy);
        Assert.Equal(2, stored.Occurrences);
        Assert.Equal(seenAgain, stored.LastSeenUtc);
        Assert.Equal(IncidentSeverity.High, stored.Severity);
        Assert.Equal("[\"no tick for 95s\"]", stored.EvidenceJson);
    }

    [Fact]
    public async Task Secrets_in_a_row_are_masked_in_get_list_and_summary()
    {
        // The agent-crashed incident quotes an exception and a traceback tail;
        // whatever slipped into them must not reach the browser.
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U"; // pragma: allowlist secret
        await using var db = NewDb(NewName());
        var row = Row("trading:agent-crashed:HTTPError", evidence:
            "[\"Authorization: Bearer " + jwt + "\", \"password=hunter2xyz\", \"runner exited with code 1\"]");
        row.Title = "Login failed with token=abcd1234efgh";
        row.Summary = "HTTPError: 401 for url: https://api.example/login?access_token=" + jwt;
        row.Location = "postgres://algo:s3cretPw@localhost:5432/algotrading";
        row.Suggestion = "Check that bot 1234567890:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsawx is still valid."; // pragma: allowlist secret
        db.Incidents.Add(row);
        await db.SaveChangesAsync();
        var controller = Controller(db);

        var fromGet = await GetView(controller, row.Id);
        var fromList = Assert.Single(await List(controller));

        foreach (var view in new[] { fromGet, fromList })
        {
            string all = string.Join("\n", new[] { view.Title, view.Summary, view.Location, view.Suggestion }.Concat(view.Evidence));
            Assert.DoesNotContain(jwt, all);
            Assert.DoesNotContain("eyJ", all);
            Assert.DoesNotContain("hunter2xyz", all);
            Assert.DoesNotContain("abcd1234efgh", all);
            Assert.DoesNotContain("s3cretPw", all);
            Assert.DoesNotContain("AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsawx", all);

            // What is not secret stays, so the incident still reads.
            Assert.Equal(new[] { "Authorization: Bearer …", "password=…", "runner exited with code 1" }, view.Evidence);
            Assert.Equal("Login failed with token=…", view.Title);
            Assert.Equal("postgres://algo:…@localhost:5432/algotrading", view.Location);
        }

        var summary = await SummaryOf(controller);
        Assert.Equal("Login failed with token=…", summary.NewestTitle);
    }

    // The shared redaction spec as cases. The same table is in Sentinel's
    // tests/test_sentinel_notify.py and web/src/lib/incidents.test.ts: a case
    // added here is added there.
    [Theory]
    [InlineData("Generate a new Dhan token before tomorrow's 08:45 start")]
    [InlineData("SSH password guessing from 1.2.3.4 was banned")]
    [InlineData("Fyers rejected the desk's credential (token expired or invalid)")]
    [InlineData("FYERS token expired at 08:45; strategies are running deaf.")]
    [InlineData("Password reset for user coderforchange")]
    [InlineData("Sentinel's secret scan found a key in config/x.json")]
    [InlineData("Generate a new Dhan token\n\nEvidence:\n• feed silent")]
    [InlineData("Skipping: 3 runners already stopped")]
    [InlineData("input_tokens: 512")]
    [InlineData("spinning=3")]
    [InlineData("if token == expected:")]
    [InlineData("Dhan feed: 8 reconnect(s) carried no ticks — waiting 80s")]
    [InlineData("429 Client Error: Too Many Requests for url: http://localhost:5025/api/UserAuth/login")]
    [InlineData("https://example.com:8443/path")]
    [InlineData("NSE feed silent for 120 s; newest tick 11:27:35 IST on NSE:NIFTY50-INDEX")]
    [InlineData("SUBSCRIBED: 321 symbol(s)")]
    [InlineData("A setting added to .env did nothing until appsettings.Local.json was regenerated")]
    public void Ordinary_incident_text_is_left_alone(string text)
    {
        // A mask that eats the words a trader needs is its own failure: this
        // desk's incidents talk about tokens, logins and feeds all the time.
        Assert.Equal(text, IncidentRedaction.Mask(text));
    }

    [Theory]
    [InlineData("POSTGRES_PASSWORD=hunter2hunter2", "POSTGRES_PASSWORD=…")]
    [InlineData("TELEGRAM_BOT_TOKEN=abcdefghij", "TELEGRAM_BOT_TOKEN=…")]
    [InlineData("DHAN_PIN=1234", "DHAN_PIN=…")]
    [InlineData("FYERS_SECRET_KEY=ABCD1234XYZ", "FYERS_SECRET_KEY=…")]
    [InlineData("JWT_SECRET_KEY=supersecretjwtkey", "JWT_SECRET_KEY=…")]
    [InlineData("DHAN_API_SECRET=abcdef123", "DHAN_API_SECRET=…")]
    [InlineData("ANGEL_API_KEY=abcdef12", "ANGEL_API_KEY=…")]
    [InlineData("{\"trading_pin\": \"4821\"}", "{\"trading_pin\": \"…\"}")]
    [InlineData("\"access_token\": \"abcDEF123456\"", "\"access_token\": \"…\"")]
    [InlineData("refreshToken=Zm9vYmFyYmF6", "refreshToken=…")]
    [InlineData("X-Api-Key: abcd1234", "X-Api-Key: …")]
    [InlineData("Host=db;Password=pa55word;Database=algotrading", "Host=db;Password=…;Database=algotrading")]
    [InlineData("GET /login?client_secret=XYZ987654&state=1", "GET /login?client_secret=…&state=1")]
    [InlineData("TOTP=123456", "TOTP=…")]
    [InlineData("password=\"correct horse battery\"", "password=\"…\"")]
    [InlineData("postgresql://postgres:S3cretPassw0rd@localhost:5432/algotrading", "postgresql://postgres:…@localhost:5432/algotrading")]
    [InlineData("redis://:S3cretPassw0rd@localhost:6379/0", "redis://:…@localhost:6379/0")]
    [InlineData("Authorization: Basic dXNlcjpTM2NyZXRQYXNzdzByZA==", "Authorization: Basic …")]
    [InlineData("curl -H 'Authorization: Bearer abcdefghijklmnop'", "curl -H 'Authorization: Bearer …'")]
    [InlineData("headers={'Authorization': 'Bearer abc.def.ghi-jkl'}", "headers={'Authorization': 'Bearer …'}")]
    [InlineData("Bearer abcdefghijklmnopqrstuvwxyz123", "Bearer …")]
    [InlineData("url: /bot8123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw0/sendMessage", "url: /bot…/sendMessage")] // pragma: allowlist secret
    [InlineData("chat 123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw0 said", "chat … said")] // pragma: allowlist secret
    [InlineData("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhZG1pbiIsImV4cCI6MX0.c2lnbmF0dXJlLXZhbHVlLWhlcmU expired", "token … expired")] // pragma: allowlist secret
    public void Credential_shapes_are_masked(string text, string expected)
    {
        Assert.Equal(expected, IncidentRedaction.Mask(text));
        Assert.Equal(expected, IncidentRedaction.Mask(expected));   // masking twice is masking once
    }

    [Fact]
    public void A_long_line_is_masked_in_time_not_hidden()
    {
        // Bounded lengths: a long identifier cannot make a pattern backtrack past its 250 ms.
        string text = string.Concat(Enumerable.Repeat("token_", 20_000)) + " password=hunter22";
        Assert.EndsWith(" password=…", IncidentRedaction.Mask(text));
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("[]", new string[0])]
    [InlineData("null", new string[0])]
    [InlineData("[\"a\",\"b\"]", new[] { "a", "b" })]
    [InlineData("[\"a\",null,3]", new[] { "a", "3" })]
    [InlineData("\"just one\"", new[] { "just one" })]
    [InlineData("{\"k\":1}", new[] { "{\"k\":1}" })]
    [InlineData("[\"cut off", new[] { "[\"cut off" })]
    [InlineData("runner exited with code 1", new[] { "runner exited with code 1" })]
    public async Task Evidence_is_read_as_a_list_and_bad_json_is_shown_verbatim(string? json, string[] expected)
    {
        await using var db = NewDb(NewName());
        var row = Row("x", evidence: json);
        db.Incidents.Add(row);
        await db.SaveChangesAsync();

        var result = await Controller(db).Get(row.Id, CancellationToken.None);

        var view = Assert.IsType<IncidentView>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(expected, view.Evidence);
    }

    [Fact]
    public async Task A_row_with_unreadable_evidence_still_lists()
    {
        await using var db = NewDb(NewName());
        db.Incidents.Add(Row("x", evidence: "{not json"));
        await db.SaveChangesAsync();

        var items = await List(Controller(db));

        var item = Assert.Single(items);
        Assert.Equal(new[] { "{not json" }, item.Evidence);
    }

    // ---------- root cause, resolution, fix reference ----------

    [Fact]
    public async Task Resolving_with_notes_stores_them_trimmed_masked_and_capped()
    {
        // A pasted log line is the likeliest way a token reaches a note, and
        // what is stored is what the history keeps: masked before it lands.
        await using var db = NewDb(NewName());
        var row = Row("feed-silent:dhan");
        db.Incidents.Add(row);
        await db.SaveChangesAsync();

        var notes = new IncidentNotesRequest(
            RootCause: "  Dhan token expired overnight; feed said access_token=abcDEF123456 in the log  ",
            Resolution: new string('r', IncidentsController.MaxNoteChars + 500),
            FixRef: "  4c8eaba  ");
        var result = await Controller(db, userName: "upendra").Resolve(row.Id, notes, CancellationToken.None);

        var view = Assert.IsType<IncidentView>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(IncidentStatus.Resolved, view.Status);
        Assert.Equal("Dhan token expired overnight; feed said access_token=… in the log", view.RootCause);
        Assert.Equal("4c8eaba", view.FixRef);

        var stored = await db.Incidents.AsNoTracking().SingleAsync();
        Assert.Equal("Dhan token expired overnight; feed said access_token=… in the log", stored.RootCause);
        Assert.DoesNotContain("abcDEF123456", stored.RootCause);
        Assert.Equal(IncidentsController.MaxNoteChars, stored.Resolution!.Length);
        Assert.Equal("4c8eaba", stored.FixRef);
        Assert.Equal("upendra", stored.ResolvedBy);
    }

    [Fact]
    public async Task Resolving_without_notes_keeps_the_ones_already_written()
    {
        // The console's Resolve form is optional, and notes written while the
        // incident was open must not be wiped by a resolve that sends none.
        await using var db = NewDb(NewName());
        var row = Row("feed-silent:dhan");
        row.RootCause = "Token expired";
        db.Incidents.Add(row);
        await db.SaveChangesAsync();

        await Controller(db).Resolve(row.Id, null, CancellationToken.None);
        Assert.Equal("Token expired", (await db.Incidents.AsNoTracking().SingleAsync()).RootCause);

        await Controller(db).Notes(row.Id, new IncidentNotesRequest(null, "Regenerated the token", null), CancellationToken.None);
        var stored = await db.Incidents.AsNoTracking().SingleAsync();
        Assert.Equal("Token expired", stored.RootCause);
        Assert.Equal("Regenerated the token", stored.Resolution);
    }

    [Fact]
    public async Task A_resolve_that_is_refused_stores_no_notes()
    {
        await using var db = NewDb(NewName());
        var row = Row("x", status: IncidentStatus.Resolved);
        db.Incidents.Add(row);
        await db.SaveChangesAsync();

        var result = await Controller(db).Resolve(row.Id, new IncidentNotesRequest("cause", "fix", "abc"), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        var stored = await db.Incidents.AsNoTracking().SingleAsync();
        Assert.Null(stored.RootCause);
        Assert.Null(stored.Resolution);
        Assert.Null(stored.FixRef);
    }

    [Fact]
    public async Task Notes_can_be_written_on_an_open_incident_and_leave_its_status_alone()
    {
        await using var db = NewDb(NewName());
        var row = Row("feed-silent:dhan");
        db.Incidents.Add(row);
        await db.SaveChangesAsync();

        var result = await Controller(db, userName: "upendra").Notes(
            row.Id,
            new IncidentNotesRequest("The Dhan websocket was opened a sixth time", "Stopped the extra feed", "docs/modules/sentinel.md"),
            CancellationToken.None);

        var view = Assert.IsType<IncidentView>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(IncidentStatus.Open, view.Status);
        Assert.Equal("The Dhan websocket was opened a sixth time", view.RootCause);
        Assert.Equal("Stopped the extra feed", view.Resolution);
        Assert.Equal("docs/modules/sentinel.md", view.FixRef);

        var stored = await db.Incidents.AsNoTracking().SingleAsync();
        Assert.Equal(IncidentStatus.Open, stored.Status);
        Assert.Null(stored.ResolvedUtc);
        Assert.Null(stored.ResolvedBy);
        Assert.Equal("Stopped the extra feed", stored.Resolution);
    }

    [Fact]
    public async Task Notes_on_a_resolved_incident_can_be_corrected_and_an_empty_field_clears_it()
    {
        // The cause is often understood after the incident closed itself.
        await using var db = NewDb(NewName());
        var row = Row("feed-silent:dhan", status: IncidentStatus.Resolved);
        row.ResolvedUtc = T0.AddMinutes(9);
        row.RootCause = "Unknown";
        row.Resolution = "Waited";
        row.FixRef = "abc1234";
        db.Incidents.Add(row);
        await db.SaveChangesAsync();

        var result = await Controller(db).Notes(
            row.Id,
            new IncidentNotesRequest("Dhan dropped the socket at the 11:27 rollover", "   ", null),
            CancellationToken.None);

        var view = Assert.IsType<IncidentView>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(IncidentStatus.Resolved, view.Status);
        var stored = await db.Incidents.AsNoTracking().SingleAsync();
        Assert.Equal("Dhan dropped the socket at the 11:27 rollover", stored.RootCause);
        Assert.Null(stored.Resolution);          // blank is "clear it"
        Assert.Equal("abc1234", stored.FixRef);  // absent is "leave it"
        Assert.Equal(T0.AddMinutes(9), stored.ResolvedUtc);
        Assert.Equal(IncidentStatus.Resolved, stored.Status);
    }

    [Fact]
    public async Task Notes_masks_and_caps_like_resolve()
    {
        await using var db = NewDb(NewName());
        var row = Row("x");
        db.Incidents.Add(row);
        await db.SaveChangesAsync();

        string longRef = "https://github.com/x/y/commit/" + new string('f', IncidentsController.MaxFixRefChars);
        await Controller(db).Notes(
            row.Id,
            new IncidentNotesRequest("POSTGRES_PASSWORD=hunter2hunter2 was wrong", null, longRef),
            CancellationToken.None);

        var stored = await db.Incidents.AsNoTracking().SingleAsync();
        Assert.Equal("POSTGRES_PASSWORD=… was wrong", stored.RootCause);
        Assert.Equal(IncidentsController.MaxFixRefChars, stored.FixRef!.Length);
    }

    [Fact]
    public async Task Notes_of_nothing_is_a_bad_request_and_a_missing_incident_is_404()
    {
        await using var db = NewDb(NewName());
        var row = Row("x");
        db.Incidents.Add(row);
        await db.SaveChangesAsync();
        var controller = Controller(db);

        Assert.IsType<BadRequestObjectResult>(await controller.Notes(row.Id, null, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.Notes(row.Id, new IncidentNotesRequest(null, null, null), CancellationToken.None));
        Assert.IsType<NotFoundObjectResult>(await controller.Notes(row.Id + 1000, new IncidentNotesRequest("a", null, null), CancellationToken.None));
    }

    [Fact]
    public async Task A_notes_edit_that_lands_after_Sentinel_resolved_the_row_is_a_conflict_and_stores_nothing()
    {
        // The same rule as acknowledge: the update carries the status it read.
        string name = NewName();
        await using var console = NewDb(name);
        var row = Row("feed-silent:dhan");
        console.Incidents.Add(row);
        await console.SaveChangesAsync();

        await using (var sentinel = NewDb(name))
        {
            var same = await sentinel.Incidents.SingleAsync();
            same.Status = IncidentStatus.Resolved;
            same.ResolvedUtc = T0.AddMinutes(3);
            await sentinel.SaveChangesAsync();
        }

        var result = await Controller(console).Notes(row.Id, new IncidentNotesRequest("cause", null, null), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        await using var check = NewDb(name);
        var stored = await check.Incidents.SingleAsync();
        Assert.Equal(IncidentStatus.Resolved, stored.Status);
        Assert.Null(stored.RootCause);
    }

    [Theory]
    [InlineData("  \t ", null)]
    [InlineData("a\0b", "a�b")]
    public void A_note_is_trimmed_to_null_and_a_NUL_is_replaced(string text, string? expected)
    {
        Assert.Equal(expected, IncidentsController.CleanNote(text, IncidentsController.MaxNoteChars));
    }

    [Fact]
    public void The_cut_never_splits_a_surrogate_pair()
    {
        // Half an emoji is not UTF-8, and Postgres would refuse the whole update.
        string text = new string('a', 9) + "\U0001F600";
        string? cut = IncidentsController.CleanNote(text, 10);
        Assert.Equal(new string('a', 9), cut);
    }

    [Fact]
    public void A_secret_across_the_cut_is_masked_whole_before_the_cut()
    {
        // Cut first and a JWT loses its tail, no longer matches, and its head is stored.
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U"; // pragma: allowlist secret
        string text = new string('a', 20) + " " + jwt;
        string? stored = IncidentsController.CleanNote(text, 40);
        Assert.Equal(new string('a', 20) + " …", stored);
    }

    // ---------- seen before ----------

    [Fact]
    public async Task Every_incident_says_how_often_its_problem_happened_before_and_how_it_ended_last_time()
    {
        // Two problems, several episodes each, interleaved in time. Each row
        // counts only the episodes of its own fingerprint that started before it.
        await using var db = NewDb(NewName());
        var feed1 = Resolved("feed-silent:dhan", T0, T0.AddMinutes(4), by: null, rootCause: null, resolution: null, fixRef: null);
        var token1 = Resolved("broker-token:fyers", T0.AddHours(1), T0.AddHours(2), by: "upendra",
            rootCause: "Token expired at 08:45", resolution: "Regenerated it", fixRef: "abc1234");
        var feed2 = Resolved("feed-silent:dhan", T0.AddDays(1), T0.AddDays(1).AddMinutes(30), by: "upendra",
            rootCause: "Sixth websocket kicked the first", resolution: "Closed the extra connection", fixRef: "9f8e7d6");
        var feed3 = Row("feed-silent:dhan", firstSeen: T0.AddDays(2), lastSeen: T0.AddDays(2).AddMinutes(2));
        var token2 = Row("broker-token:fyers", status: IncidentStatus.Acknowledged, firstSeen: T0.AddDays(2).AddHours(1));
        db.Incidents.AddRange(feed1, token1, feed2, feed3, token2);
        await db.SaveChangesAsync();

        var views = (await List(Controller(db), status: "any")).ToDictionary(v => v.Id);

        Assert.Equal(0, views[feed1.Id].PreviousEpisodes);
        Assert.Equal(T0, views[feed1.Id].FirstEverUtc);
        Assert.Null(views[feed1.Id].LastResolution);

        Assert.Equal(1, views[feed2.Id].PreviousEpisodes);
        Assert.Equal(T0, views[feed2.Id].FirstEverUtc);
        Assert.Equal(feed1.Id, views[feed2.Id].LastResolution!.Id);
        Assert.Null(views[feed2.Id].LastResolution!.ResolvedBy);   // Sentinel's checks came back clean
        Assert.Null(views[feed2.Id].LastResolution!.Resolution);

        // The live one: two before it, and the last one is the most recent, not the first.
        var last = views[feed3.Id];
        Assert.Equal(2, last.PreviousEpisodes);
        Assert.Equal(T0, last.FirstEverUtc);
        Assert.Equal(feed2.Id, last.LastResolution!.Id);
        Assert.Equal(T0.AddDays(1).AddMinutes(30), last.LastResolution.ResolvedUtc);
        Assert.Equal("upendra", last.LastResolution.ResolvedBy);
        Assert.Equal("Sixth websocket kicked the first", last.LastResolution.RootCause);
        Assert.Equal("Closed the extra connection", last.LastResolution.Resolution);
        Assert.Equal("9f8e7d6", last.LastResolution.FixRef);

        // The other fingerprint does not leak into this one's count.
        Assert.Equal(0, views[token1.Id].PreviousEpisodes);
        Assert.Equal(1, views[token2.Id].PreviousEpisodes);
        Assert.Equal(T0.AddHours(1), views[token2.Id].FirstEverUtc);
        Assert.Equal("Regenerated it", views[token2.Id].LastResolution!.Resolution);

        // The default list (live only) still counts the resolved episodes it does not show.
        var live = (await List(Controller(db))).ToDictionary(v => v.Id);
        Assert.Equal(2, live[feed3.Id].PreviousEpisodes);
        Assert.Equal(1, live[token2.Id].PreviousEpisodes);

        // And so does one incident on its own.
        var one = await GetView(Controller(db), feed3.Id);
        Assert.Equal(2, one.PreviousEpisodes);
        Assert.Equal("Closed the extra connection", one.LastResolution!.Resolution);
    }

    [Fact]
    public async Task Two_episodes_that_started_at_the_same_instant_are_ordered_by_number()
    {
        await using var db = NewDb(NewName());
        var a = Resolved("x", T0, T0.AddMinutes(1));
        var b = Row("x", firstSeen: T0);
        db.Incidents.AddRange(a, b);
        await db.SaveChangesAsync();

        var views = (await List(Controller(db), status: "any")).ToDictionary(v => v.Id);

        Assert.Equal(0, views[a.Id].PreviousEpisodes);
        Assert.Equal(1, views[b.Id].PreviousEpisodes);
    }

    [Fact]
    public async Task Notes_shown_as_last_time_are_masked_even_when_stored_by_hand()
    {
        await using var db = NewDb(NewName());
        db.Incidents.Add(Resolved("x", T0, T0.AddMinutes(1), resolution: "Set DHAN_PIN=1234 again"));
        var now = Row("x", firstSeen: T0.AddHours(1));
        db.Incidents.Add(now);
        await db.SaveChangesAsync();

        var view = await GetView(Controller(db), now.Id);

        Assert.Equal("Set DHAN_PIN=… again", view.LastResolution!.Resolution);
    }

    [Fact]
    public async Task Resolving_answers_with_the_seen_before_facts_too()
    {
        await using var db = NewDb(NewName());
        db.Incidents.Add(Resolved("x", T0, T0.AddMinutes(1), resolution: "Restarted the feed"));
        var now = Row("x", firstSeen: T0.AddHours(1));
        db.Incidents.Add(now);
        await db.SaveChangesAsync();

        var result = await Controller(db).Resolve(now.Id, new IncidentNotesRequest(null, "Restarted it again", null), CancellationToken.None);

        var view = Assert.IsType<IncidentView>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(1, view.PreviousEpisodes);
        Assert.Equal("Restarted the feed", view.LastResolution!.Resolution);
        Assert.Equal("Restarted it again", view.Resolution);
    }

    // ---------- history ----------

    [Fact]
    public async Task History_is_one_row_per_problem_with_its_episodes_time_to_resolve_and_latest_notes()
    {
        var now = DateTime.UtcNow;
        await using var db = NewDb(NewName());
        var feed1 = Resolved("feed-silent:dhan", now.AddDays(-10), now.AddDays(-10).AddMinutes(10), severity: IncidentSeverity.Medium,
            occurrences: 20, resolution: "Restarted the feed", rootCause: "Socket dropped");
        var feed2 = Resolved("feed-silent:dhan", now.AddDays(-5), now.AddDays(-5).AddMinutes(30), by: "upendra",
            severity: IncidentSeverity.Critical, occurrences: 60);
        var feed3 = Row("feed-silent:dhan", severity: IncidentSeverity.High, firstSeen: now.AddMinutes(-3), lastSeen: now.AddMinutes(-1),
            title: "Dhan feed silent for 120 s");
        feed3.Occurrences = 4;
        var token = Resolved("broker-token:fyers", now.AddDays(-2), now.AddDays(-2).AddHours(1), by: "upendra", agent: "trading",
            fixRef: "abc1234");
        db.Incidents.AddRange(feed1, feed2, feed3, token);
        await db.SaveChangesAsync();

        var history = await HistoryOf(Controller(db), days: 90);

        Assert.Equal(90, history.Days);
        Assert.Equal(new[] { "feed-silent:dhan", "broker-token:fyers" }, history.Items.Select(i => i.Fingerprint));

        var feed = history.Items[0];
        Assert.Equal("health", feed.Agent);
        Assert.Equal("Dhan feed silent for 120 s", feed.Title);            // the latest episode's
        Assert.Equal(IncidentSeverity.Critical, feed.Severity);            // the loudest any reached
        Assert.Equal(3, feed.Episodes);
        Assert.Equal(2, feed.ResolvedEpisodes);
        Assert.Equal(84, feed.Occurrences);
        Assert.Equal(feed1.FirstSeenUtc, feed.FirstSeenUtc);
        Assert.Equal(feed3.LastSeenUtc, feed.LastSeenUtc);
        Assert.Equal(TimeSpan.FromMinutes(20).TotalSeconds, feed.MeanTimeToResolveSeconds!.Value, 3);   // (10 + 30) / 2
        Assert.True(feed.OpenNow);
        // The newest episode anyone wrote about, not merely the newest one.
        Assert.Equal(feed1.Id, feed.LatestResolution!.Id);
        Assert.Equal("Socket dropped", feed.LatestResolution.RootCause);
        Assert.Equal("Restarted the feed", feed.LatestResolution.Resolution);

        Assert.Equal(new[] { feed3.Id, feed2.Id, feed1.Id }, feed.EpisodeList.Select(e => e.Id));
        Assert.Null(feed.EpisodeList[2].ResolvedBy);                     // Sentinel resolved the first
        Assert.Equal("upendra", feed.EpisodeList[1].ResolvedBy);
        Assert.Equal("Restarted the feed", feed.EpisodeList[2].Resolution);

        var fyers = history.Items[1];
        Assert.Equal(1, fyers.Episodes);
        Assert.False(fyers.OpenNow);
        Assert.Equal(TimeSpan.FromHours(1).TotalSeconds, fyers.MeanTimeToResolveSeconds!.Value, 3);
        Assert.Equal("abc1234", fyers.LatestResolution!.FixRef);
    }

    [Fact]
    public async Task History_orders_by_episodes_then_by_the_latest_sighting()
    {
        var now = DateTime.UtcNow;
        await using var db = NewDb(NewName());
        db.Incidents.AddRange(
            Resolved("once-old", now.AddDays(-20), now.AddDays(-20).AddMinutes(1)),
            Resolved("once-new", now.AddDays(-1), now.AddDays(-1).AddMinutes(1)),
            Resolved("twice", now.AddDays(-30), now.AddDays(-30).AddMinutes(1)),
            Resolved("twice", now.AddDays(-25), now.AddDays(-25).AddMinutes(1)));
        await db.SaveChangesAsync();

        var history = await HistoryOf(Controller(db), days: 90);

        Assert.Equal(new[] { "twice", "once-new", "once-old" }, history.Items.Select(i => i.Fingerprint));
    }

    [Fact]
    public async Task History_keeps_to_its_window_but_never_leaves_out_what_is_live()
    {
        var now = DateTime.UtcNow;
        await using var db = NewDb(NewName());
        db.Incidents.AddRange(
            Resolved("x", now.AddDays(-100), now.AddDays(-100).AddMinutes(5)),   // before the window
            Resolved("x", now.AddDays(-3), now.AddDays(-3).AddMinutes(5)),
            // Started long ago and never re-seen since: a live row is part of now, whatever its age.
            Row("stuck", firstSeen: now.AddDays(-200), lastSeen: now.AddDays(-200)));
        await db.SaveChangesAsync();

        var history = await HistoryOf(Controller(db), days: 30);

        var x = Assert.Single(history.Items, i => i.Fingerprint == "x");
        Assert.Equal(1, x.Episodes);
        var stuck = Assert.Single(history.Items, i => i.Fingerprint == "stuck");
        Assert.True(stuck.OpenNow);
        Assert.Null(stuck.MeanTimeToResolveSeconds);   // nothing has ended: no mean, not a zero
        Assert.Equal(0, stuck.ResolvedEpisodes);
        Assert.Null(stuck.LatestResolution);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(100_000, 3650)]
    public async Task History_days_are_clamped_and_the_answer_says_which(int days, int expected)
    {
        await using var db = NewDb(NewName());

        var history = await HistoryOf(Controller(db), days: days);

        Assert.Equal(expected, history.Days);
        Assert.Empty(history.Items);
    }

    [Fact]
    public async Task History_lists_the_latest_episodes_of_a_flapping_problem_and_counts_them_all()
    {
        var now = DateTime.UtcNow;
        await using var db = NewDb(NewName());
        int total = IncidentsController.MaxEpisodesListed + 7;
        db.Incidents.AddRange(Enumerable.Range(0, total)
            .Select(i => Resolved("flap", now.AddHours(-total + i), now.AddHours(-total + i).AddMinutes(1))));
        await db.SaveChangesAsync();

        var flap = Assert.Single((await HistoryOf(Controller(db), days: 90)).Items);

        Assert.Equal(total, flap.Episodes);
        Assert.Equal(IncidentsController.MaxEpisodesListed, flap.EpisodeList.Count);
        Assert.True(flap.EpisodeList[0].FirstSeenUtc > flap.EpisodeList[^1].FirstSeenUtc);
    }

    // ---------- helpers ----------

    private static string NewName() => $"incidents-{Guid.NewGuid():N}";

    private static TradingDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase(name).Options);

    private static IncidentsController Controller(TradingDbContext db, string userName = "admin")
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Name, userName) },
            authenticationType: "Test");

        return new IncidentsController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
            },
        };
    }

    private static async Task<IncidentSummary> SummaryOf(IncidentsController controller)
    {
        var result = await controller.Summary(CancellationToken.None);
        return Assert.IsType<IncidentSummary>(Assert.IsType<OkObjectResult>(result).Value);
    }

    private static async Task<IncidentHistory> HistoryOf(IncidentsController controller, int days)
    {
        var result = await controller.History(days, CancellationToken.None);
        return Assert.IsType<IncidentHistory>(Assert.IsType<OkObjectResult>(result).Value);
    }

    /// <summary>
    /// A finished episode: Sentinel's resolve when <paramref name="by"/> is
    /// null, a person's otherwise, with whatever notes were written on it.
    /// </summary>
    private static Incident Resolved(
        string fingerprint,
        DateTime firstSeen,
        DateTime resolvedAt,
        string? by = null,
        string severity = IncidentSeverity.Medium,
        string agent = "health",
        int occurrences = 1,
        string? rootCause = null,
        string? resolution = null,
        string? fixRef = null)
    {
        var row = Row(fingerprint, status: IncidentStatus.Resolved, severity: severity, agent: agent,
            firstSeen: firstSeen, lastSeen: resolvedAt);
        row.ResolvedUtc = resolvedAt;
        row.ResolvedBy = by;
        row.Occurrences = occurrences;
        row.RootCause = rootCause;
        row.Resolution = resolution;
        row.FixRef = fixRef;
        return row;
    }

    private static async Task<IncidentView> GetView(IncidentsController controller, long id)
    {
        var result = await controller.Get(id, CancellationToken.None);
        return Assert.IsType<IncidentView>(Assert.IsType<OkObjectResult>(result).Value);
    }

    private static async Task<List<IncidentView>> List(
        IncidentsController controller,
        string? status = null,
        string? severity = null,
        string? agent = null,
        int take = 200)
    {
        var result = await controller.List(status, severity, agent, take);
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsAssignableFrom<IEnumerable<IncidentView>>(ok.Value).ToList();
    }

    /// <summary>A row as Sentinel's store inserts it.</summary>
    private static Incident Row(
        string fingerprint,
        string status = IncidentStatus.Open,
        string severity = IncidentSeverity.Medium,
        string agent = "health",
        DateTime? firstSeen = null,
        DateTime? lastSeen = null,
        string? title = null,
        string? evidence = "[\"observed\"]") => new()
    {
        Fingerprint = fingerprint,
        Agent = agent,
        Rule = "test-rule",
        Severity = severity,
        Status = status,
        Title = title ?? $"Problem {fingerprint}",
        Summary = "What happened.",
        Location = "somewhere",
        EvidenceJson = evidence,
        Suggestion = "Look at it.",
        Occurrences = 1,
        FirstSeenUtc = firstSeen ?? T0,
        LastSeenUtc = lastSeen ?? firstSeen ?? T0,
    };
}

/// <summary>
/// The incidents table as Sentinel's own SQL sees it.
/// </summary>
/// <remarks>
/// Sentinel writes the rows with hand-written SQL in <c>sentinel/store.py</c>,
/// not through EF, and the in-memory provider the controller tests use ignores
/// table names, column names, indexes and constraints. A rename on this side
/// would pass every other test and show up first as Sentinel's inserts failing
/// in production, with incidents going only to its fallback file. So the real
/// Npgsql model is built (no connection is opened) and checked against the
/// Python source.
/// </remarks>
public class IncidentsTableContractTests
{
    private static readonly string[] SentinelTables = { "incidents", "sentinel_heartbeat" };

    [Fact]
    public void Every_table_Sentinel_s_SQL_names_exists()
    {
        var model = Model();
        string store = ReadSentinel("store.py");

        // "DO UPDATE SET" is the upsert's conflict clause, not a table: the
        // heartbeat is written with INSERT ... ON CONFLICT ("Id") DO UPDATE SET.
        var named = Regex.Matches(store, @"\b(?:FROM|INTO|(?<!DO\s)UPDATE)\s+(?!SET\b)([A-Za-z_]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.Contains("incidents", named);
        Assert.Contains("sentinel_heartbeat", named);
        Assert.All(named, table => Assert.Contains(table, SentinelTables));
        Assert.All(SentinelTables, table => Assert.NotNull(EntityFor(model, table)));
    }

    [Fact]
    public void Every_column_Sentinel_s_SQL_names_exists_with_that_spelling()
    {
        var model = Model();
        var columns = SentinelTables
            .SelectMany(table =>
            {
                var entity = EntityFor(model, table)!;
                var store = StoreObjectIdentifier.Table(table);
                return entity.GetProperties().Select(p => p.GetColumnName(store));
            })
            .ToHashSet();

        var quoted = Regex.Matches(ReadSentinel("store.py"), "\"([A-Z][A-Za-z]+)\"")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        // Guard against the test passing because store.py moved or changed shape.
        Assert.Contains("Fingerprint", quoted);
        Assert.Contains("EvidenceJson", quoted);
        // Sentinel reads what a person wrote was done last time ("Last time: …"
        // in a recurring incident's message), so that column is contract too.
        Assert.Contains("Resolution", quoted);
        var missing = quoted.Where(c => !columns.Contains(c)).ToList();
        Assert.True(missing.Count == 0, "store.py names columns the table does not have: " + string.Join(", ", missing));
    }

    [Fact]
    public void One_live_row_per_fingerprint_is_enforced_by_the_database()
    {
        var entity = EntityFor(Model(), "incidents")!;

        var index = Assert.Single(entity.GetIndexes(), i => i.GetDatabaseName() == "UX_incidents_Fingerprint_live");
        Assert.True(index.IsUnique);
        Assert.Equal(new[] { nameof(Incident.Fingerprint) }, index.Properties.Select(p => p.Name));
        string filter = index.GetFilter() ?? string.Empty;
        Assert.Contains("'open'", filter);
        Assert.Contains("'acknowledged'", filter);
        Assert.DoesNotContain("'resolved'", filter);
    }

    [Fact]
    public void Only_Status_is_a_concurrency_token()
    {
        // Sentinel rewrites Occurrences, LastSeenUtc, Severity and the evidence
        // of every live row on every check. A token on any of those, or on the
        // whole row (xmin, a row version), turns every acknowledge during an
        // active incident into a 409.
        var entity = EntityFor(Model(), "incidents")!;

        Assert.Equal(
            new[] { nameof(Incident.Status) },
            entity.GetProperties().Where(p => p.IsConcurrencyToken).Select(p => p.Name));
    }

    [Fact]
    public void The_table_refuses_a_status_or_severity_the_API_would_not_count()
    {
        // A row with "Open" or "High" would match neither the live list nor the
        // severity counts: stored, and invisible. Refused at insert, Sentinel
        // falls back to its file and to Telegram instead.
        var entity = EntityFor(Model(), "incidents")!;
        var checks = entity.GetCheckConstraints().ToDictionary(c => c.Name!, c => c.Sql);

        Assert.Equal("\"Status\" IN ('open', 'acknowledged', 'resolved')", checks["CK_incidents_Status"]);
        Assert.Equal("\"Severity\" IN ('low', 'medium', 'high', 'critical')", checks["CK_incidents_Severity"]);
    }

    [Fact]
    public void The_status_and_severity_spellings_match_Sentinel_s_enums()
    {
        string model = ReadSentinel("model.py");

        Assert.Equal(IncidentSeverity.All, EnumValues(model, "Severity"));
        Assert.Equal(IncidentStatus.All, EnumValues(model, "Status"));
    }

    [Fact]
    public void Text_Sentinel_does_not_cut_is_not_given_a_length_it_could_overflow()
    {
        // store.py cuts Title and Location to 300 and Finding refuses a
        // fingerprint over 200; nothing cuts the rule, the summary, the
        // evidence or the suggestion. An overflow is an insert that fails for
        // every sighting of that problem.
        var entity = EntityFor(Model(), "incidents")!;
        int? MaxLength(string property) => entity.FindProperty(property)!.GetMaxLength();

        Assert.Equal(300, MaxLength(nameof(Incident.Title)));
        Assert.Equal(300, MaxLength(nameof(Incident.Location)));
        Assert.Equal(200, MaxLength(nameof(Incident.Fingerprint)));
        Assert.Null(MaxLength(nameof(Incident.Rule)));
        Assert.Null(MaxLength(nameof(Incident.Summary)));
        Assert.Null(MaxLength(nameof(Incident.EvidenceJson)));
        Assert.Null(MaxLength(nameof(Incident.Suggestion)));
    }

    [Fact]
    public void The_notes_columns_are_nullable_so_Sentinel_s_insert_needs_no_change()
    {
        // Sentinel's INSERT names its own columns only. A NOT NULL column added
        // without a default would fail every insert of every Sentinel still
        // running the store.py of before, and every incident would go to its
        // fallback file instead of the console.
        var entity = EntityFor(Model(), "incidents")!;

        foreach (string property in new[] { nameof(Incident.RootCause), nameof(Incident.Resolution), nameof(Incident.FixRef) })
        {
            Assert.True(entity.FindProperty(property)!.IsNullable, $"{property} must be nullable");
        }

        Assert.Null(entity.FindProperty(nameof(Incident.RootCause))!.GetMaxLength());
        Assert.Null(entity.FindProperty(nameof(Incident.Resolution))!.GetMaxLength());
        Assert.Equal(IncidentsController.MaxFixRefChars, entity.FindProperty(nameof(Incident.FixRef))!.GetMaxLength());
    }

    // ---------- helpers ----------

    /// <summary>The design-time model, which keeps check constraints and index filters.</summary>
    internal static IModel Model()
    {
        using var db = new TradingDbContext(
            new DbContextOptionsBuilder<TradingDbContext>().UseNpgsql("Host=unused").Options);
        return db.GetService<IDesignTimeModel>().Model;
    }

    internal static IEntityType? EntityFor(IModel model, string table) =>
        model.GetEntityTypes().SingleOrDefault(e => e.GetTableName() == table);

    /// <summary>The <c>NAME = "value"</c> members of one of model.py's enums, in order.</summary>
    internal static List<string> EnumValues(string source, string enumName)
    {
        var start = Regex.Match(source, $@"^class {enumName}\(str, Enum\):", RegexOptions.Multiline);
        Assert.True(start.Success, $"class {enumName} not found in model.py");
        string rest = source[(start.Index + start.Length)..];
        var end = Regex.Match(rest, @"^\S", RegexOptions.Multiline);
        string body = end.Success ? rest[..end.Index] : rest;
        return Regex.Matches(body, @"^\s+[A-Z_]+\s*=\s*""([a-z_]+)""", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();
    }

    internal static string ReadSentinel(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string path = Path.Combine(dir!.FullName, "src", "AlgoTrading.PythonEngine", "sentinel", file);
        Assert.True(File.Exists(path), $"{path} not found");
        return File.ReadAllText(path);
    }
}
