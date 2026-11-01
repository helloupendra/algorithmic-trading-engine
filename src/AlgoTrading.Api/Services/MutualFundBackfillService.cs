using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Services.MarketIntelligence;
using AlgoTrading.Infrastructure.Services.MutualFunds;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Runs the AMFI NAV history backfill (<see cref="MutualFundHistory"/>) once an
/// admin has asked for it (<c>POST api/MutualFunds/backfill</c>), and carries
/// it on after restarts until it reaches April 2006 or is paused.
/// </summary>
/// <remarks>
/// It never starts by itself: the request is recorded in <c>system_settings</c>,
/// and only a recorded request runs. Like the market-intelligence backfills it
/// keeps out of 09:00–15:40 IST on trading days (15:55 from 7 Dec 2026), so
/// the live desk has the small server to itself; a window in progress at 09:00
/// finishes its current request and the rest waits for the close. After a
/// request fails three times it waits an hour and tries that window again.
/// Set <c>MutualFunds:BackfillEnabled</c> to false to switch it off.
/// </remarks>
public sealed class MutualFundBackfillService : BackgroundService
{
    private static readonly TimeSpan Look = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MutualFundBackfillService> _logger;
    private readonly IMarketSessionService _sessions;
    private readonly MutualFundStatus _status;
    private readonly bool _enabled;

    public MutualFundBackfillService(IServiceScopeFactory scopes, ILogger<MutualFundBackfillService> logger, IMarketSessionService sessions,
        MutualFundStatus status, IOptions<MutualFundsOptions> options)
    {
        _scopes = scopes;
        _logger = logger;
        _sessions = sessions;
        _status = status;
        _enabled = options.Value.BackfillEnabled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("MutualFundBackfillService is switched off (MutualFunds:BackfillEnabled=false).");
            return;
        }

        try
        {
            // After the daily sync's first look, so a new server has today's NAVs first.
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await LookAsync(stoppingToken);
                await _status.WaitAsync(Look, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task LookAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IProcessSettingsStore>();
            var state = await MutualFundSettings.BackfillAsync(settings, ct);
            if (_status.StopBackfill || !MutualFundHistory.ReadyToRun(state, DateTime.UtcNow)) return;

            if (Quiet(DateTime.UtcNow))
            {
                if (state.State != "waiting")
                {
                    state.State = "waiting";
                    state.UpdatedUtc = DateTime.UtcNow;
                    await MutualFundSettings.SaveBackfillAsync(settings, state, ct);
                }

                return;
            }

            var history = scope.ServiceProvider.GetRequiredService<MutualFundHistory>();
            var finished = await history.RunAsync(() => !ct.IsCancellationRequested && !_status.StopBackfill && !Quiet(DateTime.UtcNow), ct);
            _logger.LogInformation("Mutual fund history backfill {State}: {Windows} windows, {Rows:N0} NAVs written{Error}.",
                finished.State, finished.WindowsDone, finished.RowsWritten, finished.LastError is null ? string.Empty : $"; {finished.LastError}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mutual fund history backfill failed: {Error}", ex.Message);
        }
    }

    private bool Quiet(DateTime nowUtc) =>
        MarketIntelligenceSchedule.InQuietWindow(nowUtc, d => _sessions.GetSessionInfo(IstTime.MiddayUtc(d), "NSE", "CM").IsTradingDay);
}
