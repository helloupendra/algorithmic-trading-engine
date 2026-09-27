using System.Text.Json;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Sentinel's desk checkups: the latest report with what to do about each
/// item, the history of reports, and "run a checkup now".
/// </summary>
/// <remarks>
/// <para>
/// Sentinel runs a checklist over the desk at fixed times (before the open,
/// after the close, at end of day, weekly) and writes each report straight
/// into the <c>desk_checkups</c> table, as it does its incidents. This
/// controller never writes a report. Its one write is a request: "run a
/// checkup now" inserts a <c>requested</c> row, which Sentinel picks up,
/// runs and completes in place.
/// </para>
/// <para>
/// So a request is only as good as Sentinel being alive, and the answers say
/// so rather than hide it. A request nobody picks up within
/// <see cref="PickUpWindow"/> stops counting as pending, and the next request
/// fails it with the command to check. And "when was the desk last checked"
/// counts only checkups that were done, never the API's own "not picked up"
/// failures, which would make a stopped Sentinel look busy.
/// </para>
/// <para>
/// Admin-only, like incidents: the items name accounts, runs, token expiries
/// and the host. Every piece of text is passed through
/// <see cref="IncidentRedaction"/> on the way out. Sentinel redacts before it
/// stores; this is the second layer, for the same reason as on incidents.
/// </para>
/// </remarks>
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/Checkups")]
public class CheckupsController : ControllerBase
{
    private const int DefaultTake = 30;
    private const int MaxTake = 200;

    /// <summary>
    /// How long a requested or running checkup counts as pending, from when it
    /// was asked for or started. A new request inside it waits for that one
    /// instead of adding another; a request older than it was never picked up.
    /// </summary>
    public static readonly TimeSpan PickUpWindow = TimeSpan.FromMinutes(10);

    /// <summary>What a request Sentinel never picked up says, with the first thing to check.</summary>
    public const string NotPickedUpError =
        "Not picked up within 10 minutes: is Sentinel running? systemctl status algotrading-sentinel";

    private readonly TradingDbContext _dbContext;
    private readonly TimeProvider _time;

    public CheckupsController(TradingDbContext dbContext, TimeProvider? time = null)
    {
        _dbContext = dbContext;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The newest checkups first, each with its items counted by state.</summary>
    /// <param name="take">At most this many, 1 to 200 (clamped); 30 by default.</param>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int take = DefaultTake, CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.DeskCheckups.AsNoTracking()
            .OrderByDescending(x => x.Id)
            .Take(Math.Clamp(take, 1, MaxTake))
            .ToListAsync(cancellationToken);

        return Ok(rows.Select(ToSummary).ToList());
    }

    /// <summary>
    /// What the console opens on: the newest finished checkup in full, the one
    /// in progress if there is one, and when the desk was last checked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Latest" is the newest done or failed row by number, so a request
    /// Sentinel never picked up shows there, as the failure it is. "Pending"
    /// follows the same <see cref="PickUpWindow"/> rule as <see cref="Run"/>,
    /// so the console never disables its button for a request that
    /// <see cref="Run"/> would treat as abandoned.
    /// </para>
    /// <para>
    /// <c>LastCompletedUtc</c> is the newest completion of a <em>done</em>
    /// checkup: a failed one did not check the desk, and the failures this
    /// controller writes itself are stamped with the moment of the next
    /// request. It is null when no checkup has ever finished, and returned
    /// however old it is: judging it stale is the console's job.
    /// </para>
    /// </remarks>
    [HttpGet("latest")]
    public async Task<IActionResult> Latest(CancellationToken cancellationToken)
    {
        string[] finished = DeskCheckupStatus.Finished.ToArray();
        var latest = await _dbContext.DeskCheckups.AsNoTracking()
            .Where(x => finished.Contains(x.Status))
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var pending = await NewestPendingAsync(_time.GetUtcNow().UtcDateTime, cancellationToken);

        DateTime? lastCompleted = await _dbContext.DeskCheckups.AsNoTracking()
            .Where(x => x.Status == DeskCheckupStatus.Done)
            .Select(x => x.CompletedUtc)
            .MaxAsync(cancellationToken);

        return Ok(new CheckupLatest(
            latest is null ? null : ToDetail(latest),
            pending is null ? null : ToSummary(pending),
            Utc(lastCompleted)));
    }

    /// <summary>One checkup with its items.</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken)
    {
        var row = await _dbContext.DeskCheckups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        return row is null
            ? NotFound(new { message = $"Checkup {id} not found." })
            : Ok(ToDetail(row));
    }

    /// <summary>
    /// Ask Sentinel for a checkup now. 202 with the new request, or 200 with
    /// the checkup already pending, which answers the same question.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pending means requested or running and asked for or started within
    /// <see cref="PickUpWindow"/>: a second click, or a click while the
    /// morning checkup runs, waits for that one rather than queueing another.
    /// </para>
    /// <para>
    /// Otherwise every request still waiting is older than the window, so
    /// Sentinel never took it. Those are failed first, with the command that
    /// says why, so the history reads "not picked up" instead of "pending"
    /// forever, and the new request goes in behind them.
    /// </para>
    /// <para>
    /// Two admins clicking in the same instant can each add a request; Sentinel
    /// runs both, which costs one extra checkup and is not worth a lock.
    /// </para>
    /// </remarks>
    [HttpPost("run")]
    public async Task<IActionResult> Run(CancellationToken cancellationToken)
    {
        DateTime now = _time.GetUtcNow().UtcDateTime;

        var pending = await NewestPendingAsync(now, cancellationToken);
        if (pending is not null)
        {
            return AlreadyPending(pending);
        }

        var abandoned = await _dbContext.DeskCheckups
            .Where(x => x.Status == DeskCheckupStatus.Requested)
            .ToListAsync(cancellationToken);

        foreach (var row in abandoned)
        {
            row.Status = DeskCheckupStatus.Failed;
            row.Error = NotPickedUpError;
            row.CompletedUtc = now;
        }

        try
        {
            // Saved before the insert, on its own: if Sentinel starts one of
            // them in between, nothing of this request has been written.
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Status is a concurrency token, so the update matched nothing:
            // Sentinel took one of the old requests after all. That running
            // checkup is the answer to this request.
            _dbContext.ChangeTracker.Clear();
            var started = await NewestPendingAsync(now, cancellationToken);
            return started is not null
                ? AlreadyPending(started)
                : Conflict(new { message = "A waiting checkup changed while this request was reading it. Try again." });
        }

        var request = new DeskCheckup
        {
            Slot = DeskCheckupSlot.OnRequest,
            Status = DeskCheckupStatus.Requested,
            RequestedUtc = now,
            RequestedBy = Actor(),
        };
        _dbContext.DeskCheckups.Add(request);
        await _dbContext.SaveChangesAsync(cancellationToken);

        HttpContext.Describe($"Asked Sentinel for a desk checkup (#{request.Id})", "checkup", request.Id.ToString());
        return Accepted(new CheckupRunAnswer(request.Id, request.Status, AlreadyPending: false));
    }

    private OkObjectResult AlreadyPending(DeskCheckup pending)
    {
        HttpContext.Describe(
            $"Asked for a desk checkup; #{pending.Id} was already {pending.Status}", "checkup", pending.Id.ToString());
        return Ok(new CheckupRunAnswer(pending.Id, pending.Status, AlreadyPending: true));
    }

    /// <summary>The newest requested or running checkup asked for or started within <see cref="PickUpWindow"/>.</summary>
    private Task<DeskCheckup?> NewestPendingAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        DateTime since = nowUtc - PickUpWindow;
        string[] pending = DeskCheckupStatus.Pending.ToArray();

        return _dbContext.DeskCheckups.AsNoTracking()
            .Where(x => pending.Contains(x.Status) && (x.RequestedUtc >= since || x.StartedUtc >= since))
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>The signed-in user's name, cut to the column's 100 characters.</summary>
    private string Actor()
    {
        string by = User.GetUserName() ?? User.Identity?.Name ?? "admin";
        return by.Length > 100 ? by[..100] : by;
    }

    private static DateTime? Utc(DateTime? value) =>
        value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

    /// <summary>A row as the history lists it: no items, just how many of each state.</summary>
    internal static CheckupSummary ToSummary(DeskCheckup x) => new(
        x.Id,
        x.Slot,
        x.Status,
        x.Verdict,
        IncidentRedaction.Mask(x.Headline),
        Utc(x.RequestedUtc),
        IncidentRedaction.Mask(x.RequestedBy),
        Utc(x.StartedUtc),
        Utc(x.CompletedUtc),
        x.Host,
        CountStates(ParseItems(x.ItemsJson).Items));

    /// <summary>
    /// A row in full. The prose is masked; the key, area, state and link are
    /// not, since Sentinel's code builds them rather than quoting anything, and
    /// masking them could only break the page's grouping and links.
    /// </summary>
    internal static CheckupDetail ToDetail(DeskCheckup x)
    {
        var (items, unreadable) = ParseItems(x.ItemsJson);
        return new CheckupDetail(
            x.Id,
            x.Slot,
            x.Status,
            x.Verdict,
            IncidentRedaction.Mask(x.Headline),
            Utc(x.RequestedUtc),
            IncidentRedaction.Mask(x.RequestedBy),
            Utc(x.StartedUtc),
            Utc(x.CompletedUtc),
            x.Host,
            CountStates(items),
            items.Select(i => i with
            {
                Title = IncidentRedaction.Mask(i.Title),
                Detail = IncidentRedaction.Mask(i.Detail),
                Action = IncidentRedaction.Mask(i.Action),
            }).ToList(),
            unreadable,
            IncidentRedaction.Mask(x.Error),
            Utc(x.NotifiedUtc));
    }

    /// <summary>
    /// The items column as a list, never an exception.
    /// </summary>
    /// <remarks>
    /// Sentinel writes a JSON array of objects, but the writer is a script and
    /// the column is plain text. Anything that is not one reads as no items,
    /// so a bad row still lists; <c>Unreadable</c> says it happened, so the
    /// console can tell "nothing to report" from "could not read the report".
    /// A field that is missing reads as empty, and one that is not a string as
    /// its JSON text.
    /// </remarks>
    internal static (IReadOnlyList<CheckupItem> Items, bool Unreadable) ParseItems(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ([], false);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return ([], true);
            }

            var items = new List<CheckupItem>();
            bool skipped = false;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    skipped = true;
                    continue;
                }

                string link = Text(element, "link");
                items.Add(new CheckupItem(
                    Text(element, "key"),
                    Text(element, "area"),
                    Text(element, "title"),
                    Text(element, "state"),
                    Text(element, "detail"),
                    Text(element, "action"),
                    string.IsNullOrWhiteSpace(link) ? null : link));
            }

            return (items, skipped);
        }
        catch (JsonException)
        {
            return ([], true);
        }
    }

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString()!,
                JsonValueKind.Null => string.Empty,
                _ => value.GetRawText(),
            }
            : string.Empty;

    /// <summary>How many items are in each known state; an unknown state counts nowhere.</summary>
    internal static CheckupCounts CountStates(IEnumerable<CheckupItem> items)
    {
        int ok = 0, warn = 0, fail = 0, info = 0, skip = 0;
        foreach (var item in items)
        {
            switch (item.State)
            {
                case DeskCheckupItemState.Ok: ok++; break;
                case DeskCheckupItemState.Warn: warn++; break;
                case DeskCheckupItemState.Fail: fail++; break;
                case DeskCheckupItemState.Info: info++; break;
                case DeskCheckupItemState.Skip: skip++; break;
            }
        }

        return new CheckupCounts(ok, warn, fail, info, skip);
    }
}

/// <summary>How many of a checkup's items are in each state.</summary>
public sealed record CheckupCounts(int Ok, int Warn, int Fail, int Info, int Skip);

/// <summary>One item of a checkup, as Sentinel wrote it.</summary>
/// <param name="Key">What was checked, for example <c>dhan-token</c>.</param>
/// <param name="Area">The group it is shown under, for example "Brokers &amp; data".</param>
/// <param name="State">ok, warn, fail, info or skip; anything else is passed through as written.</param>
/// <param name="Action">What to do about it; empty when nothing is to be done.</param>
/// <param name="Link">A console path that shows more, or null.</param>
public sealed record CheckupItem(
    string Key,
    string Area,
    string Title,
    string State,
    string Detail,
    string Action,
    string? Link);

/// <summary>A checkup as the history lists it.</summary>
/// <param name="Verdict">ok, attention or action once done; empty before.</param>
/// <param name="RequestedBy">Who asked for it; empty for a scheduled checkup.</param>
public sealed record CheckupSummary(
    long Id,
    string Slot,
    string Status,
    string Verdict,
    string Headline,
    DateTime? RequestedUtc,
    string RequestedBy,
    DateTime? StartedUtc,
    DateTime? CompletedUtc,
    string Host,
    CheckupCounts Counts);

/// <summary>A checkup in full: the history's fields, its items and why it failed, if it did.</summary>
/// <param name="ItemsUnreadable">Some or all of the items column could not be read, so the list may be short.</param>
/// <param name="Error">Why it failed; empty otherwise.</param>
/// <param name="NotifiedUtc">When its Telegram message went out, if it did.</param>
public sealed record CheckupDetail(
    long Id,
    string Slot,
    string Status,
    string Verdict,
    string Headline,
    DateTime? RequestedUtc,
    string RequestedBy,
    DateTime? StartedUtc,
    DateTime? CompletedUtc,
    string Host,
    CheckupCounts Counts,
    IReadOnlyList<CheckupItem> Items,
    bool ItemsUnreadable,
    string Error,
    DateTime? NotifiedUtc);

/// <summary>GET /api/Checkups/latest.</summary>
/// <param name="Latest">The newest done or failed checkup; null when none has finished.</param>
/// <param name="Pending">The newest checkup requested or running within the pick-up window; null when none is.</param>
/// <param name="LastCompletedUtc">When the newest done checkup finished; null when none ever has.</param>
public sealed record CheckupLatest(CheckupDetail? Latest, CheckupSummary? Pending, DateTime? LastCompletedUtc);

/// <summary>POST /api/Checkups/run: the checkup to wait for.</summary>
/// <param name="AlreadyPending">True when it was already requested or running, and nothing new was added.</param>
public sealed record CheckupRunAnswer(long Id, string Status, bool AlreadyPending);
