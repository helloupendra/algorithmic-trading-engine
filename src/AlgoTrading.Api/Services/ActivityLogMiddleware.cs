using System.Diagnostics;
using AlgoTrading.Api.Security;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Lets an endpoint add meaning to its own log entry.
/// </summary>
/// <remarks>
/// The middleware records the envelope of every change — who, what path, what
/// status. Where that is not enough to understand what happened, the endpoint
/// calls <see cref="Describe"/> and the sentence is stored alongside it.
/// </remarks>
public static class ActivityLogContext
{
    private const string Key = "activity-log-detail";
    private const string ActorKey = "activity-log-actor";

    private sealed record Detail(string? Summary, string? TargetType, string? TargetId);

    internal sealed record Actor(long UserId, string UserName, string Role);

    /// <summary>Attaches a human sentence and, optionally, what was acted on.</summary>
    public static void Describe(
        this HttpContext context,
        string summary,
        string? targetType = null,
        string? targetId = null)
    {
        context.Items[Key] = new Detail(summary, targetType, targetId);
    }

    /// <summary>
    /// Names the actor on a request that carries no token yet — a sign-in, or a
    /// refresh. Without it those rows read "anonymous", and an admin looking at
    /// one person's trail would not see when they got in.
    /// </summary>
    public static void AttributeTo(this HttpContext context, long userId, string userName, string role)
    {
        context.Items[ActorKey] = new Actor(userId, userName, role);
    }

    internal static (string? Summary, string? TargetType, string? TargetId) Read(HttpContext context)
        => context.Items.TryGetValue(Key, out var value) && value is Detail detail
            ? (detail.Summary, detail.TargetType, detail.TargetId)
            : (null, null, null);

    internal static Actor? ReadActor(HttpContext context)
        => context.Items.TryGetValue(ActorKey, out var value) ? value as Actor : null;
}

/// <summary>
/// Records every request that changes something, whoever made it.
/// </summary>
/// <remarks>
/// Automatic rather than hand-written per endpoint: an audit trail that depends
/// on someone remembering to add a line is an audit trail with holes in it, and
/// the holes are exactly where a mistake hides.
/// <para>
/// Request and response bodies are never stored. They carry passwords, broker
/// secrets and tokens, and an audit log that leaks credentials is worse than no
/// audit log. The envelope plus an endpoint's own summary is enough to answer
/// "who did this".
/// </para>
/// </remarks>
public class ActivityLogMiddleware
{
    /// <summary>
    /// Machine traffic that would drown the log. The ingestor posts a tick per
    /// symbol per second; recording those would bury every human action under
    /// millions of rows and teach everyone to ignore the log.
    /// </summary>
    private static readonly string[] IgnoredPathPrefixes =
    {
        "/api/livedata/ticks/upsert",
        "/api/livedata/latest/upsert",
        "/api/livedata/heartbeat",
        "/api/simulator/runs/marks",
        "/metrics",
        "/health",
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<ActivityLogMiddleware> _logger;

    public ActivityLogMiddleware(RequestDelegate next, ILogger<ActivityLogMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, TradingDbContext dbContext)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        string method = context.Request.Method;

        if (!ShouldRecord(method, path))
        {
            await _next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();

            try
            {
                await WriteAsync(context, dbContext, method, path, (int)stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                // Recording history must never break the thing being recorded.
                _logger.LogWarning(ex, "Could not write the activity log entry for {Method} {Path}.", method, path);
            }
        }
    }

    private static bool ShouldRecord(string method, string path)
    {
        // Reads are not recorded: they are the overwhelming majority of traffic and
        // they change nothing. Sign-in is the exception — a failed one matters.
        bool isChange =
            HttpMethods.IsPost(method) ||
            HttpMethods.IsPut(method) ||
            HttpMethods.IsPatch(method) ||
            HttpMethods.IsDelete(method);

        if (!isChange) return false;

        string lower = path.ToLowerInvariant();

        return !IgnoredPathPrefixes.Any(prefix => lower.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static async Task WriteAsync(
        HttpContext context,
        TradingDbContext dbContext,
        string method,
        string path,
        int durationMs)
    {
        var user = context.User;
        var (summary, targetType, targetId) = ActivityLogContext.Read(context);

        // The token is the normal source of "who". A sign-in has none, so the
        // endpoint names the actor itself once it knows.
        var actor = ActivityLogContext.ReadActor(context);

        int status = context.Response.StatusCode;

        var entry = new ActivityLogEntry
        {
            OccurredUtc = DateTime.UtcNow,
            UserId = user?.GetUserId() ?? actor?.UserId,
            UserName = user?.GetUserName() ?? actor?.UserName ?? "anonymous",
            Role = user?.FindFirst("role")?.Value ?? actor?.Role ?? string.Empty,
            Module = ModuleFor(path),
            Action = ActionFor(method, path),
            Method = method,
            Path = Truncate(path, 300) ?? string.Empty,
            StatusCode = status,
            DurationMs = durationMs,
            Succeeded = status is >= 200 and < 400,
            TargetType = targetType,
            TargetId = Truncate(targetId, 60),
            Summary = Truncate(summary, 500),
            IpAddress = context.Connection.RemoteIpAddress?.ToString(),
        };

        dbContext.ActivityLog.Add(entry);
        await dbContext.SaveChangesAsync(context.RequestAborted);
    }

    /// <summary>Which part of the platform a path belongs to.</summary>
    private static string ModuleFor(string path)
    {
        string lower = path.ToLowerInvariant();

        if (lower.StartsWith("/api/strategy", StringComparison.Ordinal) ||
            lower.StartsWith("/api/simulator", StringComparison.Ordinal)) return "strategies";
        if (lower.StartsWith("/api/backtest", StringComparison.Ordinal)) return "backtesting";
        if (lower.StartsWith("/api/livedata", StringComparison.Ordinal) ||
            lower.StartsWith("/api/marketdata", StringComparison.Ordinal) ||
            lower.StartsWith("/api/instruments", StringComparison.Ordinal) ||
            lower.StartsWith("/api/options", StringComparison.Ordinal) ||
            lower.StartsWith("/api/backfill", StringComparison.Ordinal) ||
            lower.StartsWith("/api/equities", StringComparison.Ordinal) ||
            lower.StartsWith("/api/watchlist", StringComparison.Ordinal) ||
            lower.StartsWith("/api/ingestor", StringComparison.Ordinal)) return "data";
        if (lower.StartsWith("/api/providers", StringComparison.Ordinal) ||
            lower.StartsWith("/api/auth", StringComparison.Ordinal)) return "connectors";
        if (lower.StartsWith("/api/risk", StringComparison.Ordinal)) return "risk";
        if (lower.StartsWith("/api/alerts", StringComparison.Ordinal)) return "alerts";
        if (lower.StartsWith("/api/users", StringComparison.Ordinal) ||
            lower.StartsWith("/api/strategypackages", StringComparison.Ordinal) ||
            lower.StartsWith("/api/invites", StringComparison.Ordinal)) return "users";
        if (lower.StartsWith("/api/userauth", StringComparison.Ordinal)) return "auth";

        return "other";
    }

    /// <summary>
    /// A short verb for the row, derived from the route so it stays right even
    /// when nobody remembers to name it.
    /// </summary>
    private static string ActionFor(string method, string path)
    {
        var segments = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !string.Equals(s, "api", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Ids make a poor verb; the trailing word usually is one.
        var words = segments
            .Where(s => !long.TryParse(s, out _))
            .TakeLast(2)
            .ToList();

        string tail = words.Count > 0 ? string.Join("-", words) : "request";

        string verb = method switch
        {
            "POST" => "create",
            "PUT" => "replace",
            "PATCH" => "update",
            "DELETE" => "delete",
            _ => method.ToLowerInvariant(),
        };

        return Truncate($"{verb}:{tail}".ToLowerInvariant(), 60)!;
    }

    private static string? Truncate(string? value, int max)
        => value is null ? null : value.Length <= max ? value : value[..max];
}
