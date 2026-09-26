using System.Collections.Concurrent;
using System.Globalization;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.OptionChain;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Providers.Dhan;

/// <summary>An underlying the chain can be asked for, with the exchange whose hours gate it.</summary>
/// <param name="PriceSymbol">
/// Where the underlying's price is read from when the chain's own figure cannot
/// be trusted: for MCX, the future the options are written on.
/// </param>
public sealed record DhanChainUnderlying(string Name, string Exchange, DhanInstrument Instrument, string? PriceSymbol = null)
{
    /// <summary>The segment the market-session rules know this exchange's derivatives by.</summary>
    public string SessionSegment => Exchange == "MCX" ? "COM" : "FO";
}

/// <summary>What one underlying's last attempt did.</summary>
public sealed record DhanChainOutcome(
    string Underlying,
    DateTime AttemptUtc,
    string State,
    string Detail,
    DateOnly? Expiry = null,
    int Rows = 0,
    int Unmatched = 0,
    decimal? Spot = null);

/// <summary>
/// Turns Dhan's chain into the platform's snapshot rows. Pure, so the mapping is
/// pinned by tests rather than discovered in the database.
/// </summary>
public static class DhanChainRows
{
    /// <param name="contracts">The platform's symbol for each (strike, "CE"/"PE") of this expiry.</param>
    public static (List<OptionChainSnapshotRow> Rows, int Unmatched) Build(
        DhanOptionChain chain,
        IReadOnlyDictionary<(decimal Strike, string Type), string> contracts)
    {
        var rows = new List<OptionChainSnapshotRow>(chain.Rows.Count * 2);
        int unmatched = 0;

        foreach (var strike in chain.Rows)
        {
            Add(strike.Strike, "CE", strike.Call);
            Add(strike.Strike, "PE", strike.Put);
        }

        return (rows, unmatched);

        void Add(decimal strike, string type, DhanChainSide? side)
        {
            if (side is null) return;

            // A strike listed after the morning's instrument download has no
            // platform symbol yet. Skipped and counted, never invented: a symbol
            // built by string formatting is how two vendors end up disagreeing.
            if (!contracts.TryGetValue((StrikeKey(strike), type), out var symbol))
            {
                unmatched++;
                return;
            }

            rows.Add(new OptionChainSnapshotRow
            {
                Underlying = chain.Underlying,
                ExpiryDate = chain.Expiry,
                StrikePrice = strike,
                OptionType = type,
                Symbol = symbol,
                SpotPrice = chain.UnderlyingPrice ?? 0m,
                LastTradedPrice = side.LastPrice,
                PriceChange = side.LastPrice is { } last && side.PreviousClose is { } close ? last - close : null,
                BidPrice = side.Bid,
                AskPrice = side.Ask,
                Volume = side.Volume,
                OpenInterest = side.OpenInterest,
                PreviousDayOpenInterest = side.PreviousOpenInterest,
                ImpliedVolatility = side.ImpliedVolatility,
                Delta = side.Greeks?.Delta,
                Gamma = side.Greeks?.Gamma,
                Theta = side.Greeks?.Theta,
                Vega = side.Greeks?.Vega,
                SourceKey = DhanProvider.Key,
            });
        }
    }

    /// <summary>
    /// A strike as a lookup key. Dhan writes "9600.000000" and the instrument
    /// table 9600.00; equal as numbers, but rounded to one scale so the key never
    /// depends on how either side formatted it.
    /// </summary>
    public static decimal StrikeKey(decimal strike) => decimal.Round(strike, 2);

    /// <summary>
    /// The expiry to record: the nearest one not behind today. On an expiry day
    /// that is today's, which is the contract most traded until the bell.
    /// </summary>
    public static DateOnly? NearestExpiry(IEnumerable<DateOnly> expiries, DateOnly today) =>
        expiries.Where(d => d >= today).Order().Select(d => (DateOnly?)d).FirstOrDefault();
}

/// <summary>
/// A start or stop of the recorder (<c>POST /api/Dhan/chain-poller/start</c> or
/// <c>/stop</c>; the morning job calls start) and the IST day it was made on.
/// Stored as "true on 2026-09-24" under <see cref="SystemSettingKeys.DhanChainPollerEnabled"/>.
/// </summary>
public sealed record DhanChainSwitch(bool On, DateOnly Day)
{
    public string Stored => $"{(On ? "true" : "false")} on {Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    /// <summary>The stored value read back, or null when it is absent or not in that shape.</summary>
    public static DhanChainSwitch? FromStored(string? stored)
    {
        var parts = (stored ?? string.Empty).Split(" on ", 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
               && bool.TryParse(parts[0], out bool on)
               && DateOnly.TryParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? new DhanChainSwitch(on, day)
            : null;
    }
}

/// <summary>
/// Whether the poller is recording, and what each underlying's last round did.
/// A singleton, so the console reads the same state the background loop writes.
/// </summary>
/// <remarks>
/// A start or stop holds until the end of the IST day it was made on, across
/// API restarts; the next day starts from configuration again.
/// It used to be a field seeded from configuration, which is "off" on the
/// server because the morning job switches the recorder on, so every restart
/// switched it off without a word. On 2026-09-24 a restart at 11:28 ended the
/// day's chain at 11:27:49: ChainFlowBuy on all three indices blocked on a stale
/// chain for 47 of 47 candles until the close, and the evening's CRUDEOIL and
/// NATURALGAS chains were never recorded. On 22 Sep nothing after 14:58:40.
/// </remarks>
public sealed class DhanChainPollerState
{
    private readonly ConcurrentDictionary<string, DhanChainOutcome> _outcomes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string Underlying, DateOnly Day), IReadOnlyList<DateOnly>> _expiries = new();
    private readonly bool _configured;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DhanChainPollerState> _logger;
    private readonly TimeProvider _time;

    // One switch at a time, so the value saved is always the one in force: a
    // start and a stop racing each other could otherwise be saved the other way round.
    private readonly SemaphoreSlim _switching = new(1, 1);
    private volatile DhanChainSwitch? _switch;

    public DhanChainPollerState(
        IOptions<DhanSettings> settings,
        IServiceScopeFactory scopes,
        ILogger<DhanChainPollerState> logger,
        TimeProvider? time = null)
    {
        _configured = settings.Value.ChainPoller.Enabled;
        _scopes = scopes;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Today's start or stop, or else configuration.</summary>
    public bool Enabled => TodaysSwitch?.On ?? _configured;

    /// <summary>The start or stop made today (IST); null while configuration decides.</summary>
    public DhanChainSwitch? TodaysSwitch => _switch is { } decision && decision.Day == Today ? decision : null;

    private DateOnly Today => IstTime.DateOf(_time.GetUtcNow().UtcDateTime);

    /// <summary>
    /// Switches the recorder on or off for the rest of the IST day and saves
    /// that, so an API restart today finds it the same way. A save that fails is
    /// logged and does not undo the switch: recording now matters more than
    /// what a restart would remember.
    /// </summary>
    public async Task SwitchAsync(bool on, string? by)
    {
        await _switching.WaitAsync();
        try
        {
            var decision = new DhanChainSwitch(on, Today);
            _switch = decision;
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IProcessSettingsStore>();
                // Not the request's token: the switch has happened, and a client
                // that hangs up must not leave the database saying otherwise.
                await store.SetAsync(SystemSettingKeys.DhanChainPollerEnabled, decision.Stored, by, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Dhan chain recorder switched {State}, but that could not be saved; an API restart today would start it as configured ({Configured}).",
                    on ? "on" : "off", _configured ? "on" : "off");
            }
        }
        finally
        {
            _switching.Release();
        }
    }

    /// <summary>
    /// At startup: today's start or stop from before the restart, when there is
    /// one. Another day's, none at all, or a read that failed leaves configuration
    /// in charge; it never stops the API from starting.
    /// </summary>
    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        DhanChainSwitch? stored;
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IProcessSettingsStore>();
            stored = DhanChainSwitch.FromStored(await store.GetAsync(SystemSettingKeys.DhanChainPollerEnabled, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not read whether the Dhan chain recorder was switched on today; starting it as configured ({Configured}).",
                _configured ? "on" : "off");
            return;
        }

        if (stored is null || stored.Day != Today) return;

        await _switching.WaitAsync(CancellationToken.None);
        try
        {
            // A start or stop that arrived while this was reading is newer than what it read.
            if (_switch is not null) return;
            _switch = stored;
        }
        finally
        {
            _switching.Release();
        }

        _logger.LogInformation("Dhan chain recorder kept {State} across the restart: it was switched so earlier today.",
            stored.On ? "on" : "off");
    }

    public DateTime? LastRoundStartedUtc { get; set; }
    public DateTime? LastRoundFinishedUtc { get; set; }

    public IReadOnlyList<DhanChainOutcome> Outcomes => _outcomes.Values.OrderBy(o => o.Underlying, StringComparer.Ordinal).ToList();

    public void Record(DhanChainOutcome outcome) => _outcomes[outcome.Underlying] = outcome;

    /// <summary>Expiries change once a day at most, so Dhan is asked once a day per underlying.</summary>
    public bool TryGetExpiries(string underlying, DateOnly day, out IReadOnlyList<DateOnly> expiries) =>
        _expiries.TryGetValue((underlying, day), out expiries!);

    public void SetExpiries(string underlying, DateOnly day, IReadOnlyList<DateOnly> expiries) =>
        _expiries[(underlying, day)] = expiries;
}

/// <summary>
/// One round of recording: for each underlying whose market is open, Dhan's chain
/// for the nearest expiry, written through the option chain module with source
/// "dhan". Everything the chain page, OI history and replay read comes out of that
/// one table, so nothing downstream learns Dhan exists.
/// </summary>
public sealed class DhanChainRecorder
{
    private readonly DhanOptionChainClient _chain;
    private readonly DhanApiClient _api;
    private readonly OptionChainService _store;
    private readonly TradingDbContext _db;
    private readonly IMarketSessionService _sessions;
    private readonly DhanChainPollerState _state;
    private readonly ILogger<DhanChainRecorder> _logger;

    public DhanChainRecorder(
        DhanOptionChainClient chain,
        DhanApiClient api,
        OptionChainService store,
        TradingDbContext db,
        IMarketSessionService sessions,
        DhanChainPollerState state,
        ILogger<DhanChainRecorder> logger)
    {
        _chain = chain;
        _api = api;
        _store = store;
        _db = db;
        _sessions = sessions;
        _state = state;
        _logger = logger;
    }

    /// <param name="onlyOpenMarkets">
    /// False only for a capture an admin asked for by hand: a closed market's chain
    /// is the last close, stamped now.
    /// </param>
    public async Task<IReadOnlyList<DhanChainOutcome>> RecordAsync(
        IEnumerable<string> underlyings,
        bool onlyOpenMarkets,
        CancellationToken cancellationToken)
    {
        var outcomes = new List<DhanChainOutcome>();
        foreach (var name in underlyings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await RecordOneAsync(name, onlyOpenMarkets, cancellationToken);
            _state.Record(outcome);
            outcomes.Add(outcome);

            // A rejected token is rejected for every underlying: stop spending
            // the three-second chain budget on answers that will all say so.
            if (outcome.State == "auth-failed") break;
        }

        return outcomes;
    }

    private async Task<DhanChainOutcome> RecordOneAsync(string name, bool onlyOpenMarkets, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, IstTime.Zone));

        DhanChainUnderlying? underlying;
        try
        {
            underlying = await ResolveAsync(name, today, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DhanChainOutcome(name, now, "failed", $"could not resolve the underlying: {ex.Message}");
        }

        if (underlying is null)
            return new DhanChainOutcome(name, now, "failed", "no Dhan id for this underlying; run the Dhan instrument import");

        if (onlyOpenMarkets && !_sessions.IsMarketOpen(now, underlying.Exchange, underlying.SessionSegment))
            return new DhanChainOutcome(underlying.Name, now, "idle", $"{underlying.Exchange} is closed");

        try
        {
            if (!_state.TryGetExpiries(underlying.Name, today, out var expiries))
            {
                expiries = await _chain.GetExpiriesAsync(underlying.Instrument, cancellationToken);
                if (expiries.Count > 0) _state.SetExpiries(underlying.Name, today, expiries);
            }

            var expiry = DhanChainRows.NearestExpiry(expiries, today);
            if (expiry is null)
                return new DhanChainOutcome(underlying.Name, now, "failed", "Dhan listed no current expiry");

            var chain = await _chain.GetChainAsync(underlying.Instrument, underlying.Name, expiry.Value, cancellationToken);
            if (underlying.PriceSymbol is not null)
            {
                // Dhan's MCX chain reports a price that is not the future's: on
                // 2026-09-14 CRUDEOIL's said 9,577 while the future traded 9,971
                // and put-call parity on the same chain gave about 9,985. The
                // future's own recent quote is the spot its options are priced on.
                var quoted = await _db.LiveQuotesLatest.AsNoTracking()
                    .Where(q => q.Symbol == underlying.PriceSymbol && q.LastTradedPrice > 0 && q.UpdatedUtc >= now.AddMinutes(-5))
                    .Select(q => q.LastTradedPrice)
                    .FirstOrDefaultAsync(cancellationToken);
                chain = chain with { UnderlyingPrice = quoted ?? await FutureLastPriceAsync(underlying, cancellationToken) ?? chain.UnderlyingPrice };
            }
            var contracts = await ContractsAsync(underlying, expiry.Value, cancellationToken);
            var (rows, unmatched) = DhanChainRows.Build(chain, contracts);

            if (rows.Count == 0)
            {
                return new DhanChainOutcome(underlying.Name, now, "failed",
                    contracts.Count == 0
                        ? "the platform has no contracts for this expiry; refresh the instrument master"
                        : $"none of Dhan's {chain.Rows.Count} strikes matched a platform contract",
                    expiry, 0, unmatched, chain.UnderlyingPrice);
            }

            int stored = await _store.StoreAsync(rows, cancellationToken);
            return new DhanChainOutcome(underlying.Name, DateTime.UtcNow, "recorded",
                $"{stored} rows", expiry, stored, unmatched, chain.UnderlyingPrice);
        }
        catch (DhanApiException ex)
        {
            _logger.LogWarning("Dhan chain {Underlying}: {Message}", underlying.Name, ex.Message);
            return new DhanChainOutcome(underlying.Name, now, ex.IsAuthFailure ? "auth-failed" : "failed", ex.Message);
        }
    }

    /// <summary>
    /// Indices from the fixed table; an MCX commodity by its nearest future,
    /// which is what Dhan writes that commodity's options on.
    /// </summary>
    private async Task<DhanChainUnderlying?> ResolveAsync(string name, DateOnly today, CancellationToken cancellationToken)
    {
        string key = name.Trim().ToUpperInvariant();
        if (DhanInstruments.IndexUnderlyings.TryGetValue(key, out var index))
        {
            string exchange = DhanInstruments.Indices.First(kv => kv.Value == index).Key.Split(':')[0];
            return new DhanChainUnderlying(key, exchange, index);
        }

        var future = await (
                from i in _db.Instruments.AsNoTracking()
                join v in _db.InstrumentVendorSymbols.AsNoTracking() on i.Symbol equals v.CanonicalSymbol
                where v.ProviderKey == DhanProvider.Key
                      && i.Exchange == "MCX" && i.Underlying == key && i.InstrumentType == "FUT"
                      && i.ExpiryDate >= today
                orderby i.ExpiryDate
                select new { i.Symbol, v.VendorSymbol })
            .FirstOrDefaultAsync(cancellationToken);

        var instrument = DhanInstrument.Parse(future?.VendorSymbol);
        return instrument is null ? null : new DhanChainUnderlying(key, "MCX", instrument, future!.Symbol);
    }

    /// <summary>The future's last price from Dhan, when no live quote for it is recorded.</summary>
    private async Task<decimal?> FutureLastPriceAsync(DhanChainUnderlying underlying, CancellationToken cancellationToken)
    {
        try
        {
            using var answer = await _api.PostAsync(
                "/marketfeed/ltp",
                new Dictionary<string, long[]> { [underlying.Instrument.Segment] = new[] { underlying.Instrument.SecurityId } },
                DhanRateClass.Quote,
                cancellationToken);
            return DhanUniverseBuilder.ReadLastPrices(answer.RootElement)
                .TryGetValue((underlying.Instrument.Segment, underlying.Instrument.SecurityId), out var price) && price > 0
                ? price
                : null;
        }
        catch (DhanApiException ex)
        {
            _logger.LogWarning("Dhan chain {Underlying}: the future's last price was not available ({Message}).", underlying.Name, ex.Message);
            return null;
        }
    }

    private async Task<Dictionary<(decimal Strike, string Type), string>> ContractsAsync(
        DhanChainUnderlying underlying, DateOnly expiry, CancellationToken cancellationToken)
    {
        var contracts = await _db.Instruments.AsNoTracking()
            .Where(i => i.Exchange == underlying.Exchange && i.Underlying == underlying.Name && i.ExpiryDate == expiry
                        && (i.OptionType == "CE" || i.OptionType == "PE") && i.StrikePrice != null)
            .Select(i => new { i.Symbol, Strike = i.StrikePrice!.Value, i.OptionType })
            .ToListAsync(cancellationToken);

        var map = new Dictionary<(decimal, string), string>();
        foreach (var c in contracts) map[(DhanChainRows.StrikeKey(c.Strike), c.OptionType)] = c.Symbol;
        return map;
    }
}

/// <summary>
/// Records Dhan's option chain every <see cref="DhanChainPollerSettings.IntervalSeconds"/>
/// while it is enabled, each underlying only while its exchange is open.
/// </summary>
/// <remarks>
/// In the API rather than a Python daemon like the FYERS poller: the chain client,
/// the pacing that keeps to Dhan's one-chain-per-three-seconds limit, and the
/// store are all here already, and a hosted service has no process to supervise.
/// </remarks>
public sealed class DhanChainPoller : BackgroundService
{
    /// <summary>How often, while the recorder is off, the loop asks whether it should be on.</summary>
    private static readonly TimeSpan OffCheckInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly DhanChainPollerState _state;
    private readonly IMarketSessionService _sessions;
    private readonly DhanSettings _settings;
    private readonly ILogger<DhanChainPoller> _logger;
    private readonly TimeProvider _time;
    private DateOnly? _warnedOffOn;

    public DhanChainPoller(
        IServiceScopeFactory scopes,
        DhanChainPollerState state,
        IMarketSessionService sessions,
        IOptions<DhanSettings> settings,
        ILogger<DhanChainPoller> logger,
        TimeProvider? time = null)
    {
        _scopes = scopes;
        _state = state;
        _sessions = sessions;
        _settings = settings.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Before the first round, so a restart in the middle of the session goes
        // on recording what the morning job switched on.
        await _state.RestoreAsync(stoppingToken);

        var interval = TimeSpan.FromSeconds(Math.Max(15, _settings.ChainPoller.IntervalSeconds));
        _logger.LogInformation(
            "Dhan chain poller {State}: {Underlyings} every {Seconds}s.",
            _state.Enabled ? "enabled" : "disabled", string.Join(", ", _settings.ChainPoller.UnderlyingList), interval.TotalSeconds);

        var offCheckedUtc = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            if (!_state.Enabled && started - offCheckedUtc >= OffCheckInterval)
            {
                offCheckedUtc = started;
                await WarnIfOffWhileNseTradesAsync(stoppingToken);
            }

            if (_state.Enabled)
            {
                try
                {
                    _state.LastRoundStartedUtc = started;
                    await using var scope = _scopes.CreateAsyncScope();
                    var recorder = scope.ServiceProvider.GetRequiredService<DhanChainRecorder>();
                    var outcomes = await recorder.RecordAsync(_settings.ChainPoller.UnderlyingList, onlyOpenMarkets: true, stoppingToken);
                    _state.LastRoundFinishedUtc = DateTime.UtcNow;

                    var recorded = outcomes.Where(o => o.State == "recorded").ToList();
                    if (recorded.Count > 0)
                    {
                        _logger.LogInformation("Dhan chain: recorded {Summary}.",
                            string.Join(", ", recorded.Select(o => $"{o.Underlying} {o.Rows}")));
                    }
                    foreach (var failure in outcomes.Where(o => o.State is "failed" or "auth-failed"))
                    {
                        _logger.LogWarning("Dhan chain {Underlying}: {Detail}", failure.Underlying, failure.Detail);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A failed round is a gap in a chart, never a reason to stop
                    // recording the rest of the session.
                    _logger.LogError(ex, "Dhan chain round failed.");
                }
            }

            // Checked every few seconds while disabled, so Start answers promptly.
            var wait = _state.Enabled ? interval - (DateTime.UtcNow - started) : TimeSpan.FromSeconds(5);
            try
            {
                await Task.Delay(wait > TimeSpan.FromSeconds(1) ? wait : TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Says so, once a day, when NSE is trading and Dhan is signed in but the
    /// recorder is off, and nobody stopped it today.
    /// </summary>
    /// <remarks>
    /// The backstop for a switch lost some other way: the morning job never got
    /// as far as starting it, Dhan was connected after the job gave up, or the
    /// saved switch could not be read at a restart. On 2026-09-24 the chain was
    /// off from 11:28 to the close, and the first sign of it was a strategy log
    /// saying "chain BLOCKED" on every candle.
    /// </remarks>
    /// <returns>True when it said so.</returns>
    public async Task<bool> WarnIfOffWhileNseTradesAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var today = IstTime.DateOf(now);
        if (_state.Enabled || _state.TodaysSwitch is not null || _warnedOffOn == today) return false;

        try
        {
            if (!_sessions.IsMarketOpen(now, "NSE", "FO")) return false;

            await using var scope = _scopes.CreateAsyncScope();
            var dhan = await scope.ServiceProvider.GetRequiredService<IBrokerSessionStore>()
                .GetForProviderAsync(DhanProvider.Key, cancellationToken);
            if (dhan is null || !dhan.IsAuthenticatedAt(now)) return false;

            _warnedOffOn = today;
            await scope.ServiceProvider.GetRequiredService<ISystemNotifier>().NotifyAsync(
                NotificationCategory.Process, NotificationSeverity.Warning,
                "Dhan chain recorder is off",
                "NSE is open and Dhan is signed in, but Dhan's option chain is not being recorded, and nobody stopped it today. " +
                "Strategies that read the chain block on a stale one until it is. Start it: POST /api/Dhan/chain-poller/start.",
                cancellationToken: cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            // A check that could not be made is a missing warning, never a reason
            // to stop the loop that records.
            _logger.LogWarning(ex, "Could not check whether the Dhan chain recorder should be on.");
            return false;
        }
    }
}
