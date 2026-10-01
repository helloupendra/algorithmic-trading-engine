using Microsoft.EntityFrameworkCore;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Application.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Api.Services
{
    /// <summary>
    /// Auto-shutdown at the close. Every strategy run is stopped — its open paper
    /// positions squared off — at the close of the market it trades on: NSE and
    /// BSE runs at 15:30 IST, MCX runs at the MCX close (<see cref="MarketCloseRules"/>).
    /// A leg ticked "carry forward" is moved to the run owner's manual book
    /// instead (<see cref="PositionCarryForward"/>), and the manual book's own
    /// unticked positions are squared off at their exchange's close
    /// (<see cref="ManualIntradaySquareOff"/>).
    /// At 15:30 on weekdays the live data feeds, the chain poller and the alerter
    /// are stopped too, except the feeds MCX still needs, which go at the MCX
    /// close — so nothing keeps consuming the host after its session ends.
    /// </summary>
    public class MarketHoursService : BackgroundService
    {
        public const string MarketClosedReason = "Market closed (15:30 IST)";

        private readonly ILogger<MarketHoursService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly FeedSupervisorRegistry _feeds;
        private readonly ChainPollerSupervisor _poller;
        private readonly AlertsSupervisor _alerts;
        private readonly IMarketSessionService _marketSession;
        private readonly StrategyProcessRegistry _runs;
        private readonly TimeZoneInfo _istZone;
        private bool _hasShutdownToday;
        private DateTime _lastShutdownDate;
        // Connector keys of the feeds left running for MCX at 15:30; each is
        // removed once it has been stopped at the MCX close.
        private readonly HashSet<string> _feedsKeptOpenForMcx = new(StringComparer.OrdinalIgnoreCase);
        public const string McxClosedReason = "MCX closed";

        public MarketHoursService(ILogger<MarketHoursService> logger, IServiceScopeFactory scopeFactory, FeedSupervisorRegistry feeds, ChainPollerSupervisor poller, AlertsSupervisor alerts, IMarketSessionService marketSession, StrategyProcessRegistry runs)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _feeds = feeds;
            _poller = poller;
            _alerts = alerts;
            _marketSession = marketSession;
            _runs = runs;
            try
            {
                // Windows uses "India Standard Time", Linux/macOS uses "Asia/Kolkata"
                _istZone = TimeZoneInfo.FindSystemTimeZoneById(
                    OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata");
            }
            catch (TimeZoneNotFoundException)
            {
                _logger.LogWarning("IST timezone not found. Using UTC as fallback. Ensure tzdata is installed.");
                _istZone = TimeZoneInfo.Utc;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("MarketHoursService is starting.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var nowUtc = DateTime.UtcNow;
                    var nowIst = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, _istZone);

                    // Strategy runs, each at the close of its own market: NSE and
                    // BSE at 15:30, MCX at the MCX close. Until 27 Sep every run
                    // went at 15:30, once a day behind a "done today" flag kept in
                    // memory — so an API process started after 15:30 swept at
                    // start-up and squared off a crude run trading the evening
                    // (run 104, 22:40:15 on 2026-09-10). Now it is asked every
                    // minute with no flag, and a run adopted after a restart is in
                    // the registry like any other, so the MCX close reaches it too.
                    // Before the feeds below, so the square-off marks at quotes
                    // that are still arriving.
                    await StopRunsPastTheirCloseAsync(nowUtc, stoppingToken);

                    // The manual book's intraday positions, each at its own
                    // exchange's close, by the same memory-less rule. After the
                    // runs, whose carried legs arrive in the book ticked and are
                    // left alone here.
                    await SquareOffManualIntradayAsync(nowUtc, stoppingToken);

                    // Reset shutdown flag if it's a new day
                    if (_hasShutdownToday && nowIst.Date > _lastShutdownDate)
                    {
                        _hasShutdownToday = false;
                    }

                    // Check if it's a weekday and time is exactly 15:30 IST (3:30 PM) or shortly after
                    if (!_hasShutdownToday &&
                        nowIst.DayOfWeek != DayOfWeek.Saturday &&
                        nowIst.DayOfWeek != DayOfWeek.Sunday)
                    {
                        if (nowIst.TimeOfDay >= new TimeSpan(15, 30, 0))
                        {
                            _logger.LogInformation("Market has closed (15:30 IST). Triggering auto-shutdown of heavy processes to save system load.");

                            // Stop every live feed (managed, or adopted after an API restart) —
                            // unless the commodity session is still on and the list carries
                            // MCX symbols: MCX trades until 23:30, and stopping the feed at the
                            // equity close left the Commodity page frozen at 15:30 on the first
                            // evening on the server. The feeds are then stopped at the MCX
                            // close instead (below), so the morning starts fresh ones.
                            //
                            // The question is asked once and the same answer applied to each
                            // feed: it is about the session and the recording list, which
                            // every feed reads, not about any one vendor. FYERS is the
                            // "ingestor" here, and its log lines read exactly as they did
                            // before the other vendors were added to this loop.
                            var mcxWantsTheFeeds = await McxStillWantsTheFeedAsync(nowUtc, stoppingToken);
                            foreach (var (key, _, feed) in _feeds.All)
                            {
                                if (mcxWantsTheFeeds)
                                {
                                    // Only a feed running now is kept for MCX, and so
                                    // only it is stopped at the MCX close. A feed started
                                    // later in the evening — TrueData's recap, which plays
                                    // until about 00:30 — was started on purpose after
                                    // this decision and must not be cut off at 23:30 by it.
                                    var running = await feed.GetStatusAsync(stoppingToken);
                                    if (running.IsRunning)
                                    {
                                        _feedsKeptOpenForMcx.Add(key);
                                        _logger.LogInformation("Market close: {Feed} kept running for the MCX session (MCX symbols on the recording list).", feed.Descriptor.Name);
                                    }
                                    else
                                    {
                                        _logger.LogInformation("Market close: {Feed} was not running.", feed.Descriptor.Name);
                                    }
                                }
                                else
                                {
                                    var feedStop = await feed.StopAsync(MarketClosedReason, stoppingToken);
                                    _logger.LogInformation("Market close: {Feed} {Outcome}.", feed.Descriptor.Name, feedStop.Message);
                                }
                            }

                            // The chain poller too. Nothing in a chain moves after the close,
                            // and left running it spent the evening of 2026-09-08 storing the
                            // same numbers every five seconds until FYERS answered 429
                            // ("request limit reached") — a quota the next morning needs.
                            var pollerStop = await _poller.StopAsync(MarketClosedReason, stoppingToken);
                            _logger.LogInformation("Market close: chain poller {Outcome}.", pollerStop.Message);

                            // The strategy runs are not stopped here: the sweep at the
                            // top of the loop has stopped the NSE and BSE ones already,
                            // and leaves the MCX ones trading until the MCX close.

                            // The alerter too: it watches the same closed market, and left
                            // running it sat "waiting for ticks" until the next reboot.
                            var alerterStop = await _alerts.StopAsync(MarketClosedReason, stoppingToken);
                            _logger.LogInformation("Market close: alerter {Outcome}.", alerterStop.WasRunning ? "stopped" : "was not running");

                            _hasShutdownToday = true;
                            _lastShutdownDate = nowIst.Date;

                            _logger.LogInformation("Auto-shutdown completed successfully.");
                        }
                    }

                    // The MCX close: the feeds that stayed up for commodities are stopped
                    // once that session ends, so no ingestor lives through the night on a
                    // token that expires at 06:00 — market-open would otherwise find it
                    // "already running" and leave it deaf all day.
                    if (_feedsKeptOpenForMcx.Count > 0 && !_marketSession.IsMarketOpen(nowUtc, "MCX", "COM"))
                    {
                        foreach (var (key, _, feed) in _feeds.All)
                        {
                            if (!_feedsKeptOpenForMcx.Contains(key)) continue;

                            var feedStop = await feed.StopAsync(McxClosedReason, stoppingToken);
                            _logger.LogInformation("MCX close: {Feed} {Outcome}.", feed.Descriptor.Name, feedStop.Message);
                            // One at a time, so a failure part-way retries only the feeds still up.
                            _feedsKeptOpenForMcx.Remove(key);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred in MarketHoursService check loop.");
                }

                // Check every 1 minute
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation("MarketHoursService is stopping.");
        }

        /// <summary>
        /// True when the commodity session is open and the recording list holds
        /// an active MCX symbol — the one case the equity close must not take
        /// the tick feed down with it.
        /// </summary>
        private async Task<bool> McxStillWantsTheFeedAsync(DateTime nowUtc, CancellationToken cancellationToken)
        {
            if (!_marketSession.IsMarketOpen(nowUtc, "MCX", "COM")) return false;
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            return await db.LiveWatchlistItems.AsNoTracking()
                .AnyAsync(x => x.IsActive && x.Symbol.StartsWith("MCX:"), cancellationToken);
        }

        /// <summary>
        /// Stops, squaring off, every run whose market has closed since it
        /// started — adopted runs included, since the registry holds them like
        /// any other. A run whose stop another caller has already claimed is left
        /// to that caller. A failure is logged and asked about again a minute
        /// later; it never keeps the other runs, or the feed shutdown after this,
        /// from going ahead.
        /// </summary>
        /// <summary>The runs of the market replay in progress, if one is.</summary>
        private async Task<IReadOnlySet<long>> ReplayRunIdsAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var replay = scope.ServiceProvider.GetService<Replay.MarketReplayService>();
            var session = replay is null ? null : await replay.LoadAsync(cancellationToken);
            return session is not null && Replay.MarketReplayService.IsActive(session.State)
                ? session.RunIds.ToHashSet()
                : new HashSet<long>();
        }

        private async Task StopRunsPastTheirCloseAsync(DateTime nowUtc, CancellationToken cancellationToken)
        {
            try
            {
                // A market replay's runs end with the replay, at its prices (MarketReplayService). A weekday
                // holiday still has a 15:30 close, and a replay played that afternoon must not be cut there.
                var replayRuns = await ReplayRunIdsAsync(cancellationToken);
                var due = MarketCloseRules.RunsToStop(
                    _marketSession,
                    nowUtc,
                    _runs.List()
                        .Where(r => !r.StopRequested && !replayRuns.Contains(r.RunId))
                        .Select(r => new MarketCloseRules.DeskRun(r.RunId, r.Underlying, r.SpotSymbol, r.StartedUtc)));
                if (due.Count == 0) return;

                using var scope = _scopeFactory.CreateScope();
                var control = scope.ServiceProvider.GetRequiredService<StrategyRunControl>();
                int stopped = 0;
                foreach (var run in due)
                {
                    try
                    {
                        // The close's own stop: ticked legs move to the owner's
                        // manual book, the rest are squared off.
                        var result = await control.StopAtMarketCloseAsync(run.RunId, run.Reason, run.ClosedAtUtc, cancellationToken);
                        if (result.WasRunning) stopped++;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Market close: could not stop run {RunId} ({Reason}); asking again in a minute.", run.RunId, run.Reason);
                    }
                }

                _logger.LogInformation("Market close: stopped {Count} strategy run(s) — {Reasons}.",
                    stopped, string.Join(", ", due.Select(r => r.Reason).Distinct()));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Market close: the strategy-run sweep failed; asking again in a minute.");
            }
        }

        /// <summary>
        /// Squares off the manual book's positions nobody ticked to carry, at
        /// the close of each one's exchange. A failure is logged and the rule is
        /// asked again a minute later; it never stops the feed shutdown.
        /// </summary>
        private async Task SquareOffManualIntradayAsync(DateTime nowUtc, CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var squareOff = scope.ServiceProvider.GetRequiredService<ManualIntradaySquareOff>();
                int closed = await squareOff.SquareOffDueAsync(nowUtc, cancellationToken);
                if (closed > 0)
                {
                    _logger.LogInformation("Market close: squared off {Count} intraday manual position(s).", closed);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Market close: the manual intraday square-off failed; asking again in a minute.");
            }
        }
    }
}
