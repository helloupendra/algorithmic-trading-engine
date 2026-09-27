using System.Security.Claims;
using System.Text.RegularExpressions;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The console's side of Sentinel's desk checkups: reading the reports
/// Sentinel wrote, and asking for one now.
/// </summary>
/// <remarks>
/// The rows are written the way Sentinel's <c>checkup/store.py</c> writes
/// them, straight into the table. What is worth pinning down: the items are
/// counted without ever failing on a malformed column, "latest" and "pending"
/// are told apart, a second click waits for the checkup already pending, a
/// request nobody picked up is failed with the command to check rather than
/// left pending forever (and never makes a stopped Sentinel look busy), a
/// request Sentinel claims in the same instant is followed rather than
/// overwritten, and nothing secret-shaped reaches the browser.
/// </remarks>
public class CheckupsControllerTests
{
    // 08:40 IST on a Monday: the morning checkup's time.
    private static readonly DateTime T0 = new(2026, 9, 28, 3, 10, 0, DateTimeKind.Utc);

    private const string MixedItems = """
        [
          {"key":"dhan-token","area":"Brokers & data","title":"Dhan token","state":"fail","detail":"Expired at 08:45","action":"Generate a new Dhan token","link":"/admin/broker/dhan"},
          {"key":"fyers-token","area":"Brokers & data","title":"FYERS token","state":"warn","detail":"Expires in 20 min","action":"Sign in again before 09:15","link":""},
          {"key":"disk","area":"Server","title":"Disk","state":"ok","detail":"41% used","action":""},
          {"key":"api","area":"Server","title":"API","state":"ok","detail":"Answering in 40 ms","action":""},
          {"key":"carry","area":"Trading","title":"Carried overnight","state":"info","detail":"2 positions","action":""},
          {"key":"mcx","area":"Trading","title":"MCX session","state":"skip","detail":"Holiday","action":""}
        ]
        """;

    // ---------- reading ----------

    [Fact]
    public async Task The_list_is_newest_first_with_each_checkup_s_items_counted_by_state()
    {
        string name = NewName();
        await Seed(name,
            Done(DeskCheckupSlot.Morning, T0.AddDays(-1), MixedItems, DeskCheckupVerdict.Action),
            Done(DeskCheckupSlot.Close, T0.AddHours(-12), "[]", DeskCheckupVerdict.Ok),
            Requested(T0.AddMinutes(-1)));
        await using var db = NewDb(name);

        var rows = await List(Controller(db));

        Assert.Equal(new[] { DeskCheckupSlot.OnRequest, DeskCheckupSlot.Close, DeskCheckupSlot.Morning }, rows.Select(r => r.Slot));
        Assert.True(rows[0].Id > rows[1].Id && rows[1].Id > rows[2].Id);
        Assert.Equal(new CheckupCounts(Ok: 2, Warn: 1, Fail: 1, Info: 1, Skip: 1), rows[2].Counts);
        Assert.Equal(new CheckupCounts(0, 0, 0, 0, 0), rows[1].Counts);
        Assert.Equal(DeskCheckupVerdict.Action, rows[2].Verdict);
        Assert.Equal(string.Empty, rows[0].Verdict);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1000, 200)]
    public async Task Take_is_clamped_between_one_and_two_hundred(int take, int expected)
    {
        string name = NewName();
        await Seed(name, Enumerable.Range(0, 205).Select(i => Done(DeskCheckupSlot.Night, T0.AddDays(-i), "[]")).ToArray());
        await using var db = NewDb(name);

        var rows = await List(Controller(db), take);

        Assert.Equal(expected, rows.Count);
        Assert.Equal(await db.DeskCheckups.MaxAsync(x => x.Id), rows[0].Id);
    }

    [Theory]
    [InlineData("[]", 0, false)]
    [InlineData("", 0, false)]
    [InlineData("{not json", 0, true)]
    [InlineData("[{\"key\":\"x\",\"state\":\"ok\"", 0, true)]
    [InlineData("{\"key\":\"x\",\"state\":\"ok\"}", 0, true)]
    [InlineData("null", 0, true)]
    [InlineData("\"all good\"", 0, true)]
    [InlineData("[{\"key\":\"x\",\"state\":\"ok\"}, 3, null, \"y\"]", 1, true)]
    public async Task Malformed_items_never_fail_a_read_and_say_they_could_not_be_read(string json, int expectedItems, bool unreadable)
    {
        // The writer is a script and the column is text. A bad value must
        // still list, and the detail says the report could not be read rather
        // than showing a checkup with nothing in it as if nothing was found.
        string name = NewName();
        var row = Done(DeskCheckupSlot.Morning, T0, json, DeskCheckupVerdict.Attention);
        await Seed(name, row);
        await using var db = NewDb(name);
        var controller = Controller(db);

        var listed = Assert.Single(await List(controller));
        var detail = await Detail(controller, row.Id);

        Assert.Equal(expectedItems, listed.Counts.Ok);
        Assert.Equal(expectedItems, detail.Items.Count);
        Assert.Equal(unreadable, detail.ItemsUnreadable);
        Assert.Equal(DeskCheckupVerdict.Attention, detail.Verdict);
    }

    [Fact]
    public async Task An_unknown_state_is_passed_through_and_counted_nowhere()
    {
        string name = NewName();
        var row = Done(DeskCheckupSlot.Close, T0, """
            [{"key":"a","state":"critical"},{"key":"b","state":"OK"},{"key":"c","state":"ok"},{"key":"d"}]
            """);
        await Seed(name, row);
        await using var db = NewDb(name);

        var detail = await Detail(Controller(db), row.Id);

        Assert.Equal(new[] { "critical", "OK", "ok", "" }, detail.Items.Select(i => i.State));
        Assert.Equal(new CheckupCounts(Ok: 1, Warn: 0, Fail: 0, Info: 0, Skip: 0), detail.Counts);
        Assert.False(detail.ItemsUnreadable);
    }

    [Fact]
    public async Task Get_returns_one_checkup_with_its_items_or_404()
    {
        string name = NewName();
        var row = Done(DeskCheckupSlot.Morning, T0, MixedItems, DeskCheckupVerdict.Action, "1 thing to do before the open, 1 more worth a look");
        await Seed(name, row);
        await using var db = NewDb(name);
        var controller = Controller(db);

        var detail = await Detail(controller, row.Id);

        Assert.Equal("1 thing to do before the open, 1 more worth a look", detail.Headline);
        Assert.Equal(6, detail.Items.Count);
        var dhan = detail.Items[0];
        Assert.Equal(new CheckupItem("dhan-token", "Brokers & data", "Dhan token", "fail", "Expired at 08:45",
            "Generate a new Dhan token", "/admin/broker/dhan"), dhan);
        // Missing and empty links both mean "nothing to open".
        Assert.Null(detail.Items[1].Link);
        Assert.Null(detail.Items[2].Link);
        Assert.Equal(string.Empty, detail.Error);

        Assert.IsType<NotFoundObjectResult>(await controller.Get(row.Id + 1000, CancellationToken.None));
    }

    [Fact]
    public async Task A_missing_field_reads_as_empty_and_a_non_string_as_its_json()
    {
        string name = NewName();
        var row = Done(DeskCheckupSlot.Weekly, T0, """[{"key":"disk","state":"warn","title":"Disk","detail":87,"action":null}]""");
        await Seed(name, row);
        await using var db = NewDb(name);

        var item = Assert.Single((await Detail(Controller(db), row.Id)).Items);

        Assert.Equal(new CheckupItem("disk", "", "Disk", "warn", "87", "", null), item);
    }

    [Fact]
    public async Task Latest_is_the_newest_finished_checkup_and_pending_the_one_in_progress()
    {
        string name = NewName();
        var morning = Done(DeskCheckupSlot.Morning, T0.AddDays(-1), MixedItems, DeskCheckupVerdict.Action);
        var failed = Row(DeskCheckupSlot.OnRequest, DeskCheckupStatus.Failed, requested: T0.AddHours(-2), started: T0.AddHours(-2), completed: T0.AddHours(-2));
        failed.Error = "Timed out reading the broker";
        var close = Row(DeskCheckupSlot.Close, DeskCheckupStatus.Running, started: T0.AddMinutes(-1));
        await Seed(name, morning, failed, close);
        await using var db = NewDb(name);

        var answer = await Latest(Controller(db));

        // A failed checkup is the latest when it is the newest: it is news.
        Assert.Equal(failed.Id, answer.Latest!.Id);
        Assert.Equal("Timed out reading the broker", answer.Latest.Error);
        Assert.Equal(close.Id, answer.Pending!.Id);
        Assert.Equal(DeskCheckupStatus.Running, answer.Pending.Status);
        // But only a done checkup says when the desk was last checked.
        Assert.Equal(morning.CompletedUtc, answer.LastCompletedUtc);
    }

    [Fact]
    public async Task Latest_on_a_desk_that_has_never_had_a_checkup_is_all_null()
    {
        await using var db = NewDb(NewName());

        var answer = await Latest(Controller(db));

        Assert.Null(answer.Latest);
        Assert.Null(answer.Pending);
        Assert.Null(answer.LastCompletedUtc);
    }

    [Theory]
    [InlineData(9, true)]
    [InlineData(11, false)]
    public async Task A_request_counts_as_pending_for_ten_minutes(int minutesAgo, bool pending)
    {
        // An older one was never picked up. Showing it as pending would disable
        // the console's button for good while Sentinel is down.
        string name = NewName();
        await Seed(name, Requested(T0.AddMinutes(-minutesAgo)));
        await using var db = NewDb(name);

        var answer = await Latest(Controller(db));

        Assert.Equal(pending, answer.Pending is not null);
    }

    // ---------- run a checkup now ----------

    [Fact]
    public async Task Run_asks_Sentinel_with_a_requested_row_saying_who_and_when()
    {
        string name = NewName();
        await using var db = NewDb(name);

        var result = await Controller(db, userName: "upendra").Run(CancellationToken.None);

        var answer = Assert.IsType<CheckupRunAnswer>(Assert.IsType<AcceptedResult>(result).Value);
        Assert.False(answer.AlreadyPending);
        Assert.Equal(DeskCheckupStatus.Requested, answer.Status);

        await using var check = NewDb(name);
        var stored = await check.DeskCheckups.SingleAsync();
        Assert.Equal(answer.Id, stored.Id);
        Assert.Equal(DeskCheckupSlot.OnRequest, stored.Slot);
        Assert.Equal(DeskCheckupStatus.Requested, stored.Status);
        Assert.Equal(T0, stored.RequestedUtc);
        Assert.Equal("upendra", stored.RequestedBy);
        Assert.Null(stored.StartedUtc);
        Assert.Null(stored.CompletedUtc);
        Assert.Equal("[]", stored.ItemsJson);
    }

    [Fact]
    public async Task Asking_again_within_ten_minutes_answers_the_pending_checkup_instead_of_adding_one()
    {
        string name = NewName();
        long first;
        await using (var db = NewDb(name))
        {
            first = RunAnswer(await Controller(db).Run(CancellationToken.None)).Id;
        }

        await using (var db = NewDb(name))
        {
            var result = await Controller(db, now: T0.AddMinutes(9)).Run(CancellationToken.None);

            var answer = Assert.IsType<CheckupRunAnswer>(Assert.IsType<OkObjectResult>(result).Value);
            Assert.True(answer.AlreadyPending);
            Assert.Equal(first, answer.Id);
            Assert.Equal(DeskCheckupStatus.Requested, answer.Status);
        }

        await using var check = NewDb(name);
        Assert.Equal(1, await check.DeskCheckups.CountAsync());
    }

    [Fact]
    public async Task A_scheduled_checkup_running_now_answers_a_request_too()
    {
        // Sentinel inserts a scheduled checkup already running, with no
        // request time. It is started within the window, so it is the one to wait for.
        string name = NewName();
        var morning = Row(DeskCheckupSlot.Morning, DeskCheckupStatus.Running, started: T0.AddMinutes(-2));
        await Seed(name, morning);
        await using var db = NewDb(name);

        var result = await Controller(db).Run(CancellationToken.None);

        var answer = Assert.IsType<CheckupRunAnswer>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(answer.AlreadyPending);
        Assert.Equal(morning.Id, answer.Id);
        Assert.Equal(DeskCheckupStatus.Running, answer.Status);
        Assert.Equal(1, await db.DeskCheckups.CountAsync());
    }

    [Fact]
    public async Task Requests_nobody_picked_up_are_failed_before_a_new_one_goes_in()
    {
        string name = NewName();
        var lastHour = Requested(T0.AddMinutes(-30));
        var yesterday = Requested(T0.AddDays(-1));
        // A checkup Sentinel started and never finished is Sentinel's to close,
        // not the API's: only requests are failed here.
        var stuck = Row(DeskCheckupSlot.Close, DeskCheckupStatus.Running, started: T0.AddHours(-1));
        var done = Done(DeskCheckupSlot.Night, T0.AddDays(-2), "[]");
        await Seed(name, yesterday, lastHour, stuck, done);
        await using var db = NewDb(name);

        var answer = RunAnswer(await Controller(db).Run(CancellationToken.None));

        Assert.False(answer.AlreadyPending);
        await using var check = NewDb(name);
        var rows = await check.DeskCheckups.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(5, rows.Count);
        foreach (var old in rows.Where(r => r.Id == lastHour.Id || r.Id == yesterday.Id))
        {
            Assert.Equal(DeskCheckupStatus.Failed, old.Status);
            Assert.Equal(CheckupsController.NotPickedUpError, old.Error);
            Assert.Contains("systemctl status algotrading-sentinel", old.Error);
            Assert.Equal(T0, old.CompletedUtc);
        }

        Assert.Equal(DeskCheckupStatus.Running, rows.Single(r => r.Id == stuck.Id).Status);
        Assert.Equal(DeskCheckupStatus.Done, rows.Single(r => r.Id == done.Id).Status);
        Assert.Equal(DeskCheckupStatus.Requested, rows.Single(r => r.Id == answer.Id).Status);
    }

    [Fact]
    public async Task A_request_failed_as_not_picked_up_does_not_make_the_desk_look_checked()
    {
        // The failure is stamped with the moment of the next click. Counting it
        // as a completion would make a stopped Sentinel read as fresh.
        string name = NewName();
        var lastDone = Done(DeskCheckupSlot.Night, T0.AddDays(-3), "[]");
        var abandoned = Requested(T0.AddHours(-1));
        await Seed(name, lastDone, abandoned);
        await using var db = NewDb(name);
        var controller = Controller(db);

        var request = RunAnswer(await controller.Run(CancellationToken.None));
        var answer = await Latest(controller);

        Assert.Equal(abandoned.Id, answer.Latest!.Id);
        Assert.Equal(DeskCheckupStatus.Failed, answer.Latest.Status);
        Assert.Equal(CheckupsController.NotPickedUpError, answer.Latest.Error);
        Assert.Equal(request.Id, answer.Pending!.Id);
        Assert.Equal(lastDone.CompletedUtc, answer.LastCompletedUtc);
    }

    [Fact]
    public async Task A_request_Sentinel_claims_while_it_is_being_failed_is_followed_not_overwritten()
    {
        // Sentinel, restarted after a stop, claims the old request at the
        // moment an admin asks again. Overwriting its "running" with "failed"
        // would report a checkup that is happening as one that never did.
        string name = NewName();
        var old = Requested(T0.AddMinutes(-30));
        await Seed(name, old);
        var claim = new SentinelClaimsFirst(name, T0);
        await using var db = NewDb(name, claim);

        var result = await Controller(db).Run(CancellationToken.None);

        Assert.True(claim.Claimed);
        var answer = Assert.IsType<CheckupRunAnswer>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(answer.AlreadyPending);
        Assert.Equal(old.Id, answer.Id);
        Assert.Equal(DeskCheckupStatus.Running, answer.Status);

        await using var check = NewDb(name);
        var stored = await check.DeskCheckups.SingleAsync();
        Assert.Equal(DeskCheckupStatus.Running, stored.Status);
        Assert.Equal(string.Empty, stored.Error);
        Assert.Null(stored.CompletedUtc);
    }

    // ---------- what reaches the browser ----------

    [Fact]
    public async Task Secrets_are_masked_in_the_list_the_latest_and_the_detail()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U"; // pragma: allowlist secret
        const string botToken = "1234567890:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsawx"; // pragma: allowlist secret
        string name = NewName();
        var row = Row(DeskCheckupSlot.OnRequest, DeskCheckupStatus.Failed, requested: T0, started: T0, completed: T0.AddMinutes(1));
        row.Headline = "Login failed with token=abcd1234efgh";
        row.RequestedBy = "password=hunter2xyz";
        row.Error = "could not connect to postgres://algo:s3cretPw@localhost:5432/algotrading";
        row.ItemsJson = """
            [{"key":"fyers-token","area":"Brokers & data","title":"Authorization: Bearer JWT","state":"fail",
              "detail":"401 for https://api.example/login?access_token=JWT","action":"Check bot BOT_TOKEN is valid",
              "link":"/admin/broker/fyers"}]
            """.Replace("JWT", jwt).Replace("BOT_TOKEN", botToken);
        await Seed(name, row);
        await using var db = NewDb(name);
        var controller = Controller(db);

        var listed = Assert.Single(await List(controller));
        var latest = (await Latest(controller)).Latest!;
        var detail = await Detail(controller, row.Id);

        foreach (var view in new[] { latest, detail })
        {
            var item = Assert.Single(view.Items);
            string all = string.Join("\n", view.Headline, view.RequestedBy, view.Error, item.Title, item.Detail, item.Action);
            Assert.DoesNotContain(jwt, all);
            Assert.DoesNotContain("eyJ", all);
            Assert.DoesNotContain("abcd1234efgh", all);
            Assert.DoesNotContain("hunter2xyz", all);
            Assert.DoesNotContain("s3cretPw", all);
            Assert.DoesNotContain(botToken, all);

            // What is not secret stays, so the report still reads.
            Assert.Equal("Login failed with token=…", view.Headline);
            Assert.Equal("could not connect to postgres://algo:…@localhost:5432/algotrading", view.Error);
            Assert.Equal("Authorization: Bearer …", item.Title);
            Assert.Equal("fyers-token", item.Key);
            Assert.Equal("Brokers & data", item.Area);
            Assert.Equal("/admin/broker/fyers", item.Link);
        }

        Assert.Equal("Login failed with token=…", listed.Headline);
        Assert.Equal("password=…", listed.RequestedBy);
    }

    [Fact]
    public async Task Times_are_served_as_UTC()
    {
        // Npgsql reads timestamptz as UTC already; a value that lost its kind
        // on the way would be serialised without a zone and read in the
        // browser's, five and a half hours out in India.
        string name = NewName();
        var row = Row(DeskCheckupSlot.OnRequest, DeskCheckupStatus.Done,
            requested: Unspecified(T0), started: Unspecified(T0.AddSeconds(5)), completed: Unspecified(T0.AddSeconds(40)));
        row.NotifiedUtc = Unspecified(T0.AddSeconds(41));
        await Seed(name, row);
        await using var db = NewDb(name);
        var controller = Controller(db);

        var detail = await Detail(controller, row.Id);
        var listed = Assert.Single(await List(controller));
        var latest = await Latest(controller);

        foreach (DateTime? time in new[] { detail.RequestedUtc, detail.StartedUtc, detail.CompletedUtc, detail.NotifiedUtc,
                     listed.RequestedUtc, listed.StartedUtc, listed.CompletedUtc, latest.LastCompletedUtc })
        {
            Assert.NotNull(time);
            Assert.Equal(DateTimeKind.Utc, time!.Value.Kind);
        }

        Assert.Equal(T0.AddSeconds(40), latest.LastCompletedUtc);
    }

    // ---------- helpers ----------

    private static string NewName() => $"checkups-{Guid.NewGuid():N}";

    private static TradingDbContext NewDb(string name, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase(name);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new TradingDbContext(options.Options);
    }

    private static async Task Seed(string name, params DeskCheckup[] rows)
    {
        await using var db = NewDb(name);
        db.DeskCheckups.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static CheckupsController Controller(TradingDbContext db, string userName = "admin", DateTime? now = null)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Name, userName) },
            authenticationType: "Test");

        return new CheckupsController(db, new FixedTime(now ?? T0))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
            },
        };
    }

    private static async Task<List<CheckupSummary>> List(CheckupsController controller, int take = 30)
    {
        var result = await controller.List(take);
        return Assert.IsAssignableFrom<IEnumerable<CheckupSummary>>(Assert.IsType<OkObjectResult>(result).Value).ToList();
    }

    private static async Task<CheckupDetail> Detail(CheckupsController controller, long id)
    {
        var result = await controller.Get(id, CancellationToken.None);
        return Assert.IsType<CheckupDetail>(Assert.IsType<OkObjectResult>(result).Value);
    }

    private static async Task<CheckupLatest> Latest(CheckupsController controller)
    {
        var result = await controller.Latest(CancellationToken.None);
        return Assert.IsType<CheckupLatest>(Assert.IsType<OkObjectResult>(result).Value);
    }

    /// <summary>The answer of a run that added a request (202).</summary>
    private static CheckupRunAnswer RunAnswer(IActionResult result) =>
        Assert.IsType<CheckupRunAnswer>(Assert.IsType<AcceptedResult>(result).Value);

    private static DateTime Unspecified(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

    /// <summary>A row as Sentinel's store leaves it.</summary>
    private static DeskCheckup Row(
        string slot,
        string status,
        DateTime? requested = null,
        DateTime? started = null,
        DateTime? completed = null) => new()
    {
        Slot = slot,
        Status = status,
        RequestedUtc = requested,
        RequestedBy = requested is null ? string.Empty : "admin",
        StartedUtc = started,
        CompletedUtc = completed,
        Host = started is null ? string.Empty : "ip-172-31-5-10",
    };

    /// <summary>What the console inserts when someone asks, before Sentinel claims it.</summary>
    private static DeskCheckup Requested(DateTime at) =>
        Row(DeskCheckupSlot.OnRequest, DeskCheckupStatus.Requested, requested: at);

    /// <summary>A scheduled checkup Sentinel finished: started 40 s before it completed.</summary>
    private static DeskCheckup Done(
        string slot,
        DateTime completed,
        string itemsJson,
        string verdict = DeskCheckupVerdict.Ok,
        string headline = "All clear") => new()
    {
        Slot = slot,
        Status = DeskCheckupStatus.Done,
        StartedUtc = completed.AddSeconds(-40),
        CompletedUtc = completed,
        Verdict = verdict,
        Headline = headline,
        ItemsJson = itemsJson,
        Host = "ip-172-31-5-10",
    };

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    /// <summary>
    /// Sentinel's <c>claim_request</c>, run between the API reading the old
    /// request and saving it as failed: the race the Status token is for.
    /// </summary>
    private sealed class SentinelClaimsFirst(string dbName, DateTime startedUtc) : SaveChangesInterceptor
    {
        public bool Claimed { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Claimed)
            {
                Claimed = true;
                await using var sentinel = NewDb(dbName);
                var row = await sentinel.DeskCheckups.SingleAsync(x => x.Status == DeskCheckupStatus.Requested, cancellationToken);
                row.Status = DeskCheckupStatus.Running;
                row.StartedUtc = startedUtc;
                row.Host = "ip-172-31-5-10";
                await sentinel.SaveChangesAsync(cancellationToken);
            }

            return result;
        }
    }
}

/// <summary>
/// The desk_checkups table as Sentinel's own SQL sees it.
/// </summary>
/// <remarks>
/// Sentinel inserts and updates checkups with hand-written SQL in
/// <c>sentinel/checkup/store.py</c>, and the in-memory provider the controller
/// tests use ignores names, types, defaults and constraints. So, as for the
/// incidents table, the real Npgsql model is built (no connection is opened)
/// and checked against the Python source: a rename on this side would
/// otherwise show up first as every checkup failing to save on the server.
/// </remarks>
public class DeskCheckupsTableContractTests
{
    private const string Table = "desk_checkups";
    private const string StoreFile = "checkup/store.py";

    [Fact]
    public void The_only_table_the_checkup_SQL_names_is_desk_checkups()
    {
        string store = IncidentsTableContractTests.ReadSentinel(StoreFile);

        // Neither "DO UPDATE SET" (an upsert's conflict clause) nor "FOR UPDATE"
        // (a row lock: claiming a request uses FOR UPDATE SKIP LOCKED) names a table.
        var named = Regex.Matches(store, @"\b(?:FROM|INTO|(?<!(?:DO|FOR)\s)UPDATE)\s+(?!SET\b)([A-Za-z_]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.Equal(new[] { Table }, named);
        Assert.NotNull(IncidentsTableContractTests.EntityFor(IncidentsTableContractTests.Model(), Table));
    }

    [Fact]
    public void Every_column_the_checkup_SQL_names_exists_with_that_spelling()
    {
        var entity = IncidentsTableContractTests.EntityFor(IncidentsTableContractTests.Model(), Table)!;
        var table = StoreObjectIdentifier.Table(Table);
        var columns = entity.GetProperties().Select(p => p.GetColumnName(table)).ToHashSet();

        var quoted = Regex.Matches(IncidentsTableContractTests.ReadSentinel(StoreFile), "\"([A-Z][A-Za-z]+)\"")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        // Guard against the test passing because store.py moved or changed shape.
        Assert.Contains("Status", quoted);
        Assert.Contains("ItemsJson", quoted);
        var missing = quoted.Where(c => !columns.Contains(c)).ToList();
        Assert.True(missing.Count == 0, "checkup/store.py names columns desk_checkups does not have: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_columns_have_the_contract_s_types()
    {
        var entity = IncidentsTableContractTests.EntityFor(IncidentsTableContractTests.Model(), Table)!;
        IProperty Column(string property) => entity.FindProperty(property)!;

        Assert.Equal("bigint", Column(nameof(DeskCheckup.Id)).GetColumnType());
        Assert.Equal(32, Column(nameof(DeskCheckup.Slot)).GetMaxLength());
        Assert.Equal(16, Column(nameof(DeskCheckup.Status)).GetMaxLength());
        Assert.Equal(16, Column(nameof(DeskCheckup.Verdict)).GetMaxLength());
        Assert.Equal(100, Column(nameof(DeskCheckup.RequestedBy)).GetMaxLength());
        Assert.Equal(100, Column(nameof(DeskCheckup.Host)).GetMaxLength());

        // Sentinel's headline and items are not cut, so no length they could overflow.
        foreach (string property in new[] { nameof(DeskCheckup.Headline), nameof(DeskCheckup.ItemsJson), nameof(DeskCheckup.Error) })
        {
            Assert.Equal("text", Column(property).GetColumnType());
        }

        foreach (string property in new[]
                 {
                     nameof(DeskCheckup.RequestedUtc), nameof(DeskCheckup.StartedUtc),
                     nameof(DeskCheckup.CompletedUtc), nameof(DeskCheckup.NotifiedUtc),
                 })
        {
            Assert.True(Column(property).IsNullable, $"{property} must be nullable");
            Assert.Equal("timestamp with time zone", Column(property).GetColumnType());
        }

        Assert.All(entity.GetProperties().Where(p => p.ClrType == typeof(string)), p => Assert.False(p.IsNullable, $"{p.Name} must be NOT NULL"));
    }

    [Fact]
    public void Every_text_column_but_slot_and_status_has_a_default_so_an_insert_may_leave_it_out()
    {
        // A NOT NULL column without a default fails every insert that does not
        // name it: every checkup of a Sentinel whose INSERT predates the column.
        var entity = IncidentsTableContractTests.EntityFor(IncidentsTableContractTests.Model(), Table)!;
        object? Default(string property) => entity.FindProperty(property)!.GetDefaultValue();

        Assert.Equal("", Default(nameof(DeskCheckup.RequestedBy)));
        Assert.Equal("", Default(nameof(DeskCheckup.Verdict)));
        Assert.Equal("", Default(nameof(DeskCheckup.Headline)));
        Assert.Equal("[]", Default(nameof(DeskCheckup.ItemsJson)));
        Assert.Equal("", Default(nameof(DeskCheckup.Error)));
        Assert.Equal("", Default(nameof(DeskCheckup.Host)));

        // These two are what a row is, so an insert must say them.
        Assert.Null(Default(nameof(DeskCheckup.Slot)));
        Assert.Null(Default(nameof(DeskCheckup.Status)));
    }

    [Fact]
    public void The_table_refuses_a_status_the_API_would_not_find()
    {
        var entity = IncidentsTableContractTests.EntityFor(IncidentsTableContractTests.Model(), Table)!;
        var check = Assert.Single(entity.GetCheckConstraints());

        Assert.Equal("CK_desk_checkups_Status", check.Name);
        Assert.Equal("\"Status\" IN ('requested', 'running', 'done', 'failed')", check.Sql);
    }

    [Fact]
    public void Only_Status_is_a_concurrency_token()
    {
        // Sentinel's UPDATEs are hand-written, so the token only guards the
        // API's own write: failing a request Sentinel is claiming in the same instant.
        var entity = IncidentsTableContractTests.EntityFor(IncidentsTableContractTests.Model(), Table)!;

        Assert.Equal(
            new[] { nameof(DeskCheckup.Status) },
            entity.GetProperties().Where(p => p.IsConcurrencyToken).Select(p => p.Name));
    }

    [Fact]
    public void The_spellings_match_Sentinel_s()
    {
        string store = IncidentsTableContractTests.ReadSentinel(StoreFile);
        Assert.All(DeskCheckupStatus.All, status => Assert.Contains($"\"{status}\"", store));

        // The console counts and colours items by these, and reads the verdict.
        string model = IncidentsTableContractTests.ReadSentinel("checkup/model.py");
        Assert.Equal(
            new[] { DeskCheckupItemState.Ok, DeskCheckupItemState.Warn, DeskCheckupItemState.Fail, DeskCheckupItemState.Info, DeskCheckupItemState.Skip },
            IncidentsTableContractTests.EnumValues(model, "State"));
        Assert.Equal(
            new[] { DeskCheckupVerdict.Ok, DeskCheckupVerdict.Attention, DeskCheckupVerdict.Action },
            IncidentsTableContractTests.EnumValues(model, "Verdict"));
    }
}
