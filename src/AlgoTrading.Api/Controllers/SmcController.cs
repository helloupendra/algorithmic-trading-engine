using AlgoTrading.Application.UseCases.LiveData;
using AlgoTrading.Application.UseCases.MarketData;
using AlgoTrading.Contracts.MarketData;
using AlgoTrading.Contracts.Smc;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Smc;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Market structure the way Smart Money Concepts reads it: swing points labelled
/// HH / HL / LH / LL, breaks of structure, changes of character, and the
/// inducement inside the current leg.
/// </summary>
/// <remarks>
/// The marks and the candles they were read from come back together, so a chart
/// cannot draw a mark against a different series than the one it was computed on.
/// Stored history is used, and today's live bars are appended past its last
/// candle when <c>includeLive</c> is on — the same merge the Charts page does.
/// The rules, and where teachers disagree about them, are in
/// <see cref="MarketStructure"/> and in docs/smart-money-concepts.md.
/// </remarks>
[ApiController]
[Route("api/[controller]")]
public class SmcController : ControllerBase
{
    /// <summary>Candles read in one request. Enough for a month of five-minute bars.</summary>
    private const int MaxCandles = 5000;

    private readonly GetStoredCandlesUseCase _storedCandles;
    private readonly GetRecentBarsUseCase _liveBars;

    public SmcController(GetStoredCandlesUseCase storedCandles, GetRecentBarsUseCase liveBars)
    {
        _storedCandles = storedCandles;
        _liveBars = liveBars;
    }

    /// <summary>The structure of one symbol at one timeframe.</summary>
    /// <param name="symbol">Broker symbol, for example NSE:NIFTY50-INDEX.</param>
    /// <param name="resolution">Any spelling: "1", "5m", "15", "1D".</param>
    /// <param name="fromDate">First trading date to read (IST), defaulting to what the store has.</param>
    /// <param name="toDate">Last trading date to read (IST).</param>
    /// <param name="method">"validPullback" (default) or "fractal".</param>
    /// <param name="strength">Candles either side of a fractal swing; ignored by the pullback rule.</param>
    /// <param name="breakOn">"close" (default) or "wick".</param>
    /// <param name="inducement">"last" (default: the pullback the leg is on now) or "first" (as it is taught, but it stalls on a strong trend).</param>
    /// <param name="includeLive">Append today's live bars past the stored history.</param>
    [HttpGet("structure")]
    public async Task<ActionResult<SmcStructureResponse>> GetStructure(
        [FromQuery] string symbol,
        [FromQuery] string resolution = "5m",
        [FromQuery] DateOnly? fromDate = null,
        [FromQuery] DateOnly? toDate = null,
        [FromQuery] string method = "validPullback",
        [FromQuery] int strength = MarketStructure.DefaultStrength,
        [FromQuery] string breakOn = "close",
        [FromQuery] string inducement = "last",
        [FromQuery] bool includeLive = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new { message = "symbol is required." });

        var swingMethod = method.Equals("fractal", StringComparison.OrdinalIgnoreCase)
            ? SwingMethod.Fractal
            : SwingMethod.ValidPullback;
        var trigger = breakOn.Equals("wick", StringComparison.OrdinalIgnoreCase)
            ? BreakTrigger.Wick
            : BreakTrigger.Close;
        var inducementMode = inducement.Equals("first", StringComparison.OrdinalIgnoreCase)
            ? InducementMode.First
            : InducementMode.Last;
        var candleCode = ResolutionCodes.ToCandle(resolution);

        var stored = await _storedCandles.ExecuteAsync(new GetStoredCandlesRequest
        {
            Symbol = symbol,
            Resolution = candleCode,
            FromDate = fromDate,
            ToDate = toDate,
        }, cancellationToken);

        var candles = stored
            .OrderBy(c => c.TimestampUtc)
            .Select(c => new SmcCandleDto
            {
                TimestampUtc = DateTime.SpecifyKind(c.TimestampUtc, DateTimeKind.Utc),
                Open = c.Open,
                High = c.High,
                Low = c.Low,
                Close = c.Close,
                Volume = c.Volume,
            })
            .ToList();

        var dropped = 0;
        if (candleCode != ResolutionCodes.Daily && !symbol.StartsWith("MCX:", StringComparison.OrdinalIgnoreCase))
        {
            var inSession = candles.Where(InSession).ToList();
            dropped = candles.Count - inSession.Count;
            candles = inSession;
        }

        var live = 0;
        if (includeLive && candleCode != ResolutionCodes.Daily)
            live = await AppendLiveAsync(symbol, candleCode, candles, cancellationToken);

        // The newest candles are the ones being read; older ones only add cost.
        if (candles.Count > MaxCandles) candles.RemoveRange(0, candles.Count - MaxCandles);

        var result = MarketStructure.Read(
            candles.Select(c => new StructureBar(c.TimestampUtc, c.Open, c.High, c.Low, c.Close)).ToList(),
            swingMethod, strength, trigger, inducementMode);

        return Ok(new SmcStructureResponse
        {
            Symbol = symbol,
            Resolution = ResolutionCodes.Label(candleCode),
            Method = swingMethod == SwingMethod.Fractal ? "fractal" : "validPullback",
            Strength = strength,
            BreakOn = trigger == BreakTrigger.Wick ? "wick" : "close",
            Candles = candles,
            Swings = result.Swings.Select(s => new SmcSwingDto
            {
                Kind = s.Kind == SwingKind.High ? "high" : "low",
                Label = LabelOf(s.Label),
                Price = s.Price,
                TimeUtc = s.TimeUtc,
                ConfirmedTimeUtc = s.ConfirmedTimeUtc,
                Major = s.Major,
            }).ToList(),
            Events = result.Events.Select(e => new SmcEventDto
            {
                Kind = e.Kind == StructureEventKind.Choch ? "CHOCH" : "BOS",
                Direction = e.Direction == TrendDirection.Bearish ? "bearish" : "bullish",
                Level = e.Level,
                LevelTimeUtc = e.LevelTimeUtc,
                BreakTimeUtc = e.BreakTimeUtc,
                BreakPrice = e.BreakPrice,
            }).ToList(),
            Inducements = result.Inducements.Select(i => new SmcInducementDto
            {
                Kind = i.Kind == SwingKind.High ? "high" : "low",
                Level = i.Level,
                TimeUtc = i.TimeUtc,
                SweptTimeUtc = i.SweptTimeUtc,
                EndedTimeUtc = i.EndedTimeUtc,
            }).ToList(),
            Trend = result.Trend switch
            {
                TrendDirection.Bullish => "bullish",
                TrendDirection.Bearish => "bearish",
                _ => "none",
            },
            ProtectedLevel = result.ProtectedLevel,
            BreakLevel = result.BreakLevel,
            InducementTaken = result.InducementTaken,
            InducementLevel = result.InducementLevel,
            Inducement = inducementMode == InducementMode.Last ? "last" : "first",
            LiveCandles = live,
            DroppedOutsideSession = dropped,
            Note = NoteFor(candles.Count, result, dropped),
        });
    }

    /// <summary>
    /// Candles of the regular NSE/BSE session only. The store holds rows
    /// stamped after the close — the live archive has written flat, zero-volume
    /// candles repeating the day's last price out to the evening — and a flat
    /// hour of them makes swings and breaks out of nothing. Commodities trade
    /// into the night, so their symbols keep every candle.
    /// </summary>
    private static bool InSession(SmcCandleDto candle)
    {
        var ist = IstTime.ToIst(candle.TimestampUtc).TimeOfDay;
        return ist >= IstTime.SessionOpen && ist < IstTime.SessionClose;
    }

    /// <summary>Appends today's live bars that the stored history does not have yet.</summary>
    private async Task<int> AppendLiveAsync(
        string symbol, string candleCode, List<SmcCandleDto> candles, CancellationToken cancellationToken)
    {
        var bars = await _liveBars.ExecuteAsync(symbol, ResolutionCodes.ToStrategy(candleCode), 2000, cancellationToken);
        if (bars.Count == 0) return 0;

        var tail = candles.Count > 0 ? candles[^1].TimestampUtc : DateTime.MinValue;
        var fresh = bars
            .Select(b => new SmcCandleDto
            {
                TimestampUtc = DateTime.SpecifyKind(b.BarStartUtc, DateTimeKind.Utc),
                Open = b.Open,
                High = b.High,
                Low = b.Low,
                Close = b.Close,
                Volume = b.VolumeDelta,
                Live = true,
            })
            .Where(b => b.TimestampUtc > tail)
            .OrderBy(b => b.TimestampUtc)
            .ToList();

        candles.AddRange(fresh);
        return fresh.Count;
    }

    private static string? LabelOf(SwingLabel? label) => label switch
    {
        SwingLabel.HigherHigh => "HH",
        SwingLabel.LowerHigh => "LH",
        SwingLabel.HigherLow => "HL",
        SwingLabel.LowerLow => "LL",
        _ => null,
    };

    /// <summary>Says why a chart is bare, rather than leaving it to look broken.</summary>
    private static string? NoteFor(int candles, MarketStructureResult result, int dropped)
    {
        if (candles == 0 && dropped > 0)
            return $"{dropped} candle(s) in this range are stamped outside the 09:15-15:30 session and were left out.";
        if (candles == 0) return "No candles stored for this symbol and timeframe in this range.";
        if (result.Swings.Count == 0) return $"{candles} candle(s) read, and none of them turned: no swing points yet.";
        if (result.Events.Count == 0) return $"{result.Swings.Count} swing point(s), and no level has been broken yet.";
        return null;
    }
}
