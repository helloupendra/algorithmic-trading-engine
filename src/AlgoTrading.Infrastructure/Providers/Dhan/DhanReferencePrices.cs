using System.Text.Json;
using System.Collections.Concurrent;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.Infrastructure.Providers.Dhan;

/// <summary>Reference closes for the overview, separate from executable live prices.</summary>
public sealed class DhanReferencePrices
{
    private sealed record Snapshot(DateTime FetchedUtc, IReadOnlyDictionary<string, decimal> Closes);
    private Snapshot _snapshot = new(DateTime.MinValue, new Dictionary<string, decimal>());
    private readonly ConcurrentDictionary<string, (DateOnly Day, decimal Close)> _daily = new();

    public decimal? DailyClose(string symbol, DateOnly day)
        => _daily.TryGetValue(symbol, out var row) && row.Day == day ? row.Close : null;
    public void RememberDailyClose(string symbol, DateOnly day, decimal close) => _daily[symbol] = (day, close);

    public void Replace(DateTime fetchedUtc, IReadOnlyDictionary<string, decimal> closes)
        => Volatile.Write(ref _snapshot, new(fetchedUtc, new Dictionary<string, decimal>(closes)));

    public decimal? PreviousClose(string symbol, DateTime quoteUtc, DateTime nowUtc)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        // Never carry yesterday's reference into a new session or apply today's
        // REST reference to a stale quote from a different trading date.
        if (nowUtc < snapshot.FetchedUtc || nowUtc - snapshot.FetchedUtc > TimeSpan.FromMinutes(5)
            || IstTime.DateOf(snapshot.FetchedUtc) != IstTime.DateOf(nowUtc)
            || IstTime.DateOf(quoteUtc) != IstTime.DateOf(snapshot.FetchedUtc)) return null;
        return snapshot.Closes.TryGetValue(symbol, out var close) ? close : null;
    }

    /// <summary>Dhan defines net_change as LTP minus previous close. OHLC close is today's close.</summary>
    public static decimal? ReadPreviousClose(JsonElement quote)
    {
        if (quote.ValueKind != JsonValueKind.Object
            || !quote.TryGetProperty("last_price", out var last) || last.ValueKind != JsonValueKind.Number || !last.TryGetDecimal(out var price)
            || !quote.TryGetProperty("net_change", out var change) || change.ValueKind != JsonValueKind.Number || !change.TryGetDecimal(out var net)
            || price <= 0) return null;
        var close = price - net;
        return close > 0 ? close : null;
    }
}

public sealed class DhanReferencePriceReader(DhanApiClient api, ISymbolMapper mapper, DhanReferencePrices cache, IMarketSessionService sessions, TimeProvider? time = null)
{
    public async Task RefreshAsync(IReadOnlyCollection<string> symbols, CancellationToken cancellationToken)
    {
        var mapped = await mapper.ToVendorManyAsync(symbols, DhanProvider.Key, cancellationToken);
        var instruments = new Dictionary<string, DhanInstrument>();
        foreach (var symbol in symbols)
        {
            if (DhanInstruments.Indices.TryGetValue(symbol, out var index)) instruments[symbol] = index;
            else if (mapped.TryGetValue(symbol, out var value) && DhanInstrument.Parse(value) is { } instrument) instruments[symbol] = instrument;
        }
        if (instruments.Count == 0) return;
        var body = instruments.Values.GroupBy(x => x.Segment)
            .ToDictionary(g => g.Key, g => g.Select(x => x.SecurityId).Distinct().ToArray());
        using var response = await api.PostAsync("/marketfeed/quote", body, DhanRateClass.Quote, cancellationToken);
        if (!response.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
        var closes = new Dictionary<string, decimal>();
        foreach (var (symbol, instrument) in instruments)
        {
            var exchange = symbol.Split(':')[0];
            var marketSegment = exchange == "MCX" ? "COM" : "CM";
            var now = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;
            if (sessions.IsMarketOpen(now, exchange, marketSegment)
                && data.TryGetProperty(instrument.Segment, out var segment) && segment.ValueKind == JsonValueKind.Object
                && segment.TryGetProperty(instrument.SecurityId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var quote)
                && DhanReferencePrices.ReadPreviousClose(quote) is { } close)
            {
                closes[symbol] = close;
                continue;
            }
            // Dhan resets net_change to zero after closing some segments. Use
            // the completed daily bar of the preceding exchange session then,
            // rather than presenting that reset as a genuinely flat day.
            var today = IstTime.DateOf(now);
            DateOnly? previous = null;
            for (var day = today.AddDays(-1); day >= today.AddDays(-14); day = day.AddDays(-1))
                if (sessions.GetSessionInfo(IstTime.MiddayUtc(day), exchange, marketSegment).IsTradingDay) { previous = day; break; }
            if (previous is not { } referenceDay) continue;
            var stored = cache.DailyClose(symbol, referenceDay);
            if (stored is null)
            {
                try
                {
                    var from = IstTime.StartOfDayUtc(referenceDay);
                    using var history = await api.PostAsync("/charts/historical", DhanHistory.DailyRequest(instrument, from, from), DhanRateClass.Data, cancellationToken);
                    stored = DhanHistory.Parse(history.RootElement, from, from, intraday: false)
                        .Where(b => IstTime.DateOf(b.TimestampUtc) == referenceDay && b.Close > 0).Select(b => (decimal?)b.Close).LastOrDefault();
                    if (stored is { } value) cache.RememberDailyClose(symbol, referenceDay, value);
                }
                catch (DhanApiException ex) when (!ex.StopsEverything) { /* Missing history stays unknown; retry on the next refresh. */ }
            }
            if (stored is { } dailyClose) closes[symbol] = dailyClose;
        }
        cache.Replace((time ?? TimeProvider.System).GetUtcNow().UtcDateTime, closes);
    }
}
