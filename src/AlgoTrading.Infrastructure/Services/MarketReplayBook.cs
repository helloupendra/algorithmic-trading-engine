using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// The in-memory prices of a market replay (<see cref="IMarketReplayBook"/>):
/// each symbol's latest replayed quote, and the 1m bar it is in the middle of.
/// One per API, a singleton; one replay at a time.
/// </summary>
public sealed class MarketReplayBook(TimeProvider? time = null) : IMarketReplayBook
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, LiveQuoteResponse> _quotes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Minute> _minutes = new(StringComparer.Ordinal);
    private DateOnly? _day;
    private DateTime? _clockUtc;

    /// <summary>A 1m bar being built from the ticks played so far, and the cumulative volume its next delta is taken from.</summary>
    private sealed class Minute
    {
        public DateTime StartUtc;
        public decimal Open, High, Low, Close;
        public long VolumeDelta;
        public int TickCount;
        public long? LastVolume;
        public DateTime UpdatedUtc;
    }

    public DateOnly? Day
    {
        get { lock (_gate) return _day; }
    }

    public DateTime? ClockUtc
    {
        get { lock (_gate) return _clockUtc; }
    }

    public void Begin(DateOnly day)
    {
        lock (_gate)
        {
            _quotes.Clear();
            _minutes.Clear();
            _day = day;
            _clockUtc = null;
        }
    }

    public void End()
    {
        lock (_gate)
        {
            _quotes.Clear();
            _minutes.Clear();
            _day = null;
            _clockUtc = null;
        }
    }

    public int Apply(IReadOnlyList<UpsertLiveTickRequest> ticks)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        int taken = 0;
        lock (_gate)
        {
            if (_day is not DateOnly day) return 0;
            foreach (var tick in ticks)
            {
                if (string.IsNullOrWhiteSpace(tick.Symbol) || tick.LastTradedPrice is not > 0m || tick.ExchangeTimestampUtc is not DateTime raw) continue;
                var stamp = Utc(raw);
                // Nothing from after the replayed day. An earlier stamp is a contract that has not traded yet
                // that day (Dhan stamps a quote with its last trade): its book is still that day's, so it
                // prices a fill, but it moves neither the replay's clock nor a minute's bar.
                var stampDay = IstTime.DateOf(stamp);
                if (stampDay > day) continue;
                bool onDay = stampDay == day;

                decimal ltp = tick.LastTradedPrice.Value;
                _quotes.TryGetValue(tick.Symbol, out var previous);
                long? volume = tick.Volume ?? previous?.Volume;
                _quotes[tick.Symbol] = new LiveQuoteResponse
                {
                    Symbol = tick.Symbol,
                    DataType = string.IsNullOrWhiteSpace(tick.DataType) ? "symbolUpdate" : tick.DataType,
                    LastTradedPrice = ltp,
                    BidPrice = tick.BidPrice ?? previous?.BidPrice,
                    AskPrice = tick.AskPrice ?? previous?.AskPrice,
                    BidSize = tick.BidSize ?? previous?.BidSize,
                    AskSize = tick.AskSize ?? previous?.AskSize,
                    Open = tick.Open ?? previous?.Open,
                    High = tick.High ?? previous?.High,
                    Low = tick.Low ?? previous?.Low,
                    Close = tick.PrevClose ?? previous?.Close,
                    Volume = volume,
                    OpenInterest = tick.OpenInterest ?? previous?.OpenInterest,
                    // Fresh by the wall clock: a recap run judges a quote's age by
                    // when it arrived, as a live run does.
                    UpdatedUtc = now,
                    ExchangeTimestampUtc = stamp,
                };

                if (onDay)
                {
                    ApplyMinute(tick.Symbol, stamp, ltp, tick.Volume, now);
                    if (_clockUtc is null || stamp > _clockUtc) _clockUtc = stamp;
                }

                taken++;
            }
        }

        return taken;
    }

    private void ApplyMinute(string symbol, DateTime stamp, decimal ltp, long? volume, DateTime now)
    {
        var start = new DateTime(stamp.Ticks - stamp.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        if (!_minutes.TryGetValue(symbol, out var minute) || minute.StartUtc != start)
        {
            // A late tick for a minute already left behind changes nothing: that
            // minute is in the recorded bars.
            if (minute is not null && start < minute.StartUtc) return;
            minute = new Minute { StartUtc = start, Open = ltp, High = ltp, Low = ltp, LastVolume = minute?.LastVolume };
            _minutes[symbol] = minute;
        }

        minute.High = Math.Max(minute.High, ltp);
        minute.Low = Math.Min(minute.Low, ltp);
        minute.Close = ltp;
        minute.TickCount++;
        if (volume is long v)
        {
            if (minute.LastVolume is long last && v > last) minute.VolumeDelta += v - last;
            minute.LastVolume = v;
        }

        minute.UpdatedUtc = now;
    }

    public LiveQuoteResponse? Quote(string symbol)
    {
        lock (_gate) return _quotes.TryGetValue(symbol, out var quote) ? quote : null;
    }

    public IReadOnlyList<LiveQuoteResponse> AllQuotes()
    {
        lock (_gate) return _quotes.Values.ToList();
    }

    public LiveBarResponse? CurrentMinute(string symbol)
    {
        lock (_gate)
        {
            if (!_minutes.TryGetValue(symbol, out var m) || m.TickCount == 0) return null;
            return new LiveBarResponse
            {
                Symbol = symbol,
                Resolution = "1m",
                BarStartUtc = m.StartUtc,
                Open = m.Open,
                High = m.High,
                Low = m.Low,
                Close = m.Close,
                VolumeDelta = m.VolumeDelta,
                TickCount = m.TickCount,
                UpdatedUtc = m.UpdatedUtc,
            };
        }
    }

    public bool Prices(string? parametersJson)
    {
        var day = Day;
        return day is not null && RecapClock.IsRecap(parametersJson) && RecapClock.RecapDate(parametersJson) == day;
    }

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
