using System.Text.Json;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>
/// Asks each model whose cooling has ended one tiny question, so a recovered
/// model is back at its place in its chains before a person's question has to
/// find out (<see cref="AiModelHealth"/>).
/// </summary>
/// <remarks>
/// A probe goes through <see cref="AiGateway"/> as a model test (source
/// <c>health</c>), so it is on the Calls tab like any other call, counts
/// against the rate limit, and teaches the health table what it found. It
/// only runs for models that cooled, so on a good day it asks nothing.
/// At start it re-reads the last half hour of calls, so a restart (a deploy)
/// does not forget which models were queueing.
/// </remarks>
public sealed class AiHealthProbe(
    IServiceScopeFactory scopes,
    AiModelHealth health,
    IOptionsMonitor<AiSettings> settings,
    ILogger<AiHealthProbe> logger,
    TimeProvider? time = null) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Remember = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _time, stoppingToken);
            await SeedAsync(stoppingToken);
            using var timer = new PeriodicTimer(Every, _time);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await ProbeAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // Never let the AI take the API down.
                    logger.LogError(ex, "The AI health probe failed; it tries again in {Minutes} minutes", Every.TotalMinutes);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The API is stopping.
        }
    }

    /// <summary>Replays the last half hour of attempts into the health table. Public for tests.</summary>
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var since = _time.GetUtcNow().UtcDateTime - Remember;
            var rows = await db.AiCalls.AsNoTracking()
                .Where(c => c.CreatedUtc >= since)
                .OrderBy(c => c.Id)
                .Select(c => new { c.CreatedUtc, c.AttemptsJson })
                .ToListAsync(cancellationToken);

            double slow = settings.CurrentValue.SlowFailureSeconds;
            foreach (var row in rows)
            {
                using var doc = JsonDocument.Parse(row.AttemptsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var a in doc.RootElement.EnumerateArray())
                {
                    string model = a.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                    string outcome = a.TryGetProperty("outcome", out var o) ? o.GetString() ?? "" : "";
                    double seconds = a.TryGetProperty("seconds", out var sec) && sec.ValueKind == JsonValueKind.Number ? sec.GetDouble() : 0;
                    if (model.Length == 0 || outcome == "cancelled") continue;
                    bool ok = outcome == "ok";
                    health.Record(model, ok, seconds, !ok && seconds >= slow, outcome, DateTime.SpecifyKind(row.CreatedUtc, DateTimeKind.Utc));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read recent AI calls to seed model health");
        }
    }

    /// <summary>One round: a tiny question to each model whose cooling has ended. Public for tests.</summary>
    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        var s = settings.CurrentValue;
        if (!s.HealthProbeEnabled || !s.KeyConfigured) return;

        foreach (string model in health.DueForProbe(_time.GetUtcNow().UtcDateTime))
        {
            health.Probed(model, _time.GetUtcNow().UtcDateTime);
            await using var scope = scopes.CreateAsyncScope();
            var gateway = scope.ServiceProvider.GetRequiredService<AiGateway>();
            var result = await gateway.AskAsync(new AiAskInput(
                AiCatalog.ModelTest, null, [new AiMessage("user", "Reply with the word OK and nothing else.")], string.Empty,
                256, 0, string.Empty, "health", "health-probe", null, [model]), NullAiStreamSink.Instance, cancellationToken);
            logger.LogInformation("AI health probe of {Model}: {Outcome} in {Seconds} s", model, result.Outcome, result.Seconds);
        }
    }
}
