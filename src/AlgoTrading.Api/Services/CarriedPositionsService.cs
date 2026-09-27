// src/AlgoTrading.Api/Services/CarriedPositionsService.cs
using AlgoTrading.Application.UseCases.LiveData;
using AlgoTrading.Contracts.LiveData;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Looks after the manual book's positions between sessions: keeps a carried
/// contract on the feed, and settles it once its expiry day has closed.
/// </summary>
/// <remarks>
/// The manual book is the one place positions are held overnight on purpose —
/// the nightly market-close leaves it alone, the 15:30 sweep stops registered
/// runners only, and the start-up reconciler skips it. Holding a position
/// across days needs two things nothing else did: a quote for it the next
/// morning, and a settlement when the contract expires (see
/// <see cref="ExpirySettler"/>).
/// <para>
/// One pass a couple of minutes after the API starts — the catch-up for
/// anything that expired while it was down — then every five minutes, so an
/// NSE or BSE expiry settles between 15:35 and 15:40 IST and an MCX expiry
/// shortly after its evening close.
/// </para>
/// </remarks>
public sealed class CarriedPositionsService : BackgroundService
{
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CarriedPositionsService> _logger;

    public CarriedPositionsService(IServiceScopeFactory scopeFactory, ILogger<CarriedPositionsService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // After migrations, the holiday calendar and the reconcilers: an expiry
        // time read from an unloaded calendar would still be right on a normal
        // day, but there is no reason to race it.
        try { await Task.Delay(StartDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await PassAsync(stoppingToken);

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PassAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var settler = scope.ServiceProvider.GetRequiredService<ExpirySettler>();

            var result = await settler.SettleDueAsync(DateTime.UtcNow, ct);
            if (result.Settled > 0)
                _logger.LogInformation("Expiry settlement: {Settled} manual position(s) settled.", result.Settled);

            var missing = await settler.CarriedSymbolsOffTheFeedAsync(DateTime.UtcNow, ct);
            if (missing.Count > 0)
            {
                var upsert = scope.ServiceProvider.GetRequiredService<UpsertWatchlistItemUseCase>();
                foreach (var symbol in missing)
                {
                    await upsert.ExecuteAsync(new UpsertWatchlistItemRequest
                    {
                        Symbol = symbol,
                        DataType = "symbolUpdate",
                        IsActive = true,
                        Priority = 10
                    }, ct);
                }
                _logger.LogWarning(
                    "Put {Count} carried manual position(s) back on the recording list: {Symbols}.",
                    missing.Count, string.Join(", ", missing));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Carried-positions pass failed; the next one retries.");
        }
    }
}
