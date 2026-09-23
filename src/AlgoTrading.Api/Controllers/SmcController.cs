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
    /// <param name="zones">When price coming back into an order block or a gap counts as having used it: "touch" (default), "midpoint" or "close".</param>
    /// <param name="fvgMinSize">Report only gaps at least this wide, in points. Zero, the default, applies no threshold — the teaching names no number.</param>
    /// <param name="standingZonesOnly">Report only the zones still standing at the last candle: unmitigated blocks and unfilled gaps. Off by default, so the history comes back whole.</param>
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
        [FromQuery] string zones = "touch",
        [FromQuery] decimal fvgMinSize = 0m,
        [FromQuery] bool standingZonesOnly = false,
        [FromQuery] bool includeLive = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new { message = "symbol is required." });

        return Ok(await ReadAsync(symbol, ResolutionCodes.ToCandle(resolution), fromDate, toDate, method, strength,
            breakOn, inducement, zones, fvgMinSize, standingZonesOnly, includeLive, cancellationToken));
    }

    /// <summary>One timeframe, read and shaped for the wire. Both endpoints go through here.</summary>
    private async Task<SmcStructureResponse> ReadAsync(
        string symbol, string candleCode, DateOnly? fromDate, DateOnly? toDate, string method, int strength,
        string breakOn, string inducement, string zones, decimal fvgMinSize, bool standingZonesOnly,
        bool includeLive, CancellationToken cancellationToken)
    {
        var swingMethod = method.Equals("fractal", StringComparison.OrdinalIgnoreCase)
            ? SwingMethod.Fractal
            : SwingMethod.ValidPullback;
        var trigger = breakOn.Equals("wick", StringComparison.OrdinalIgnoreCase)
            ? BreakTrigger.Wick
            : BreakTrigger.Close;
        var inducementMode = inducement.Equals("first", StringComparison.OrdinalIgnoreCase)
            ? InducementMode.First
            : InducementMode.Last;
        var zoneMitigation = zones.Equals("midpoint", StringComparison.OrdinalIgnoreCase)
            ? ZoneMitigation.Midpoint
            : zones.Equals("close", StringComparison.OrdinalIgnoreCase)
                ? ZoneMitigation.Close
                : ZoneMitigation.Touch;

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

        // The reader is handed a list with a hole in it every night, because the
        // rows outside 09:15-15:30 were dropped above. Telling it how long a bar
        // is lets it refuse to call yesterday's close and this morning's open
        // three candles in a row, which would otherwise invent a fair-value gap
        // at every gap open. Daily candles pass null: there the overnight gap is
        // the fair-value gap, and filtering it out would be the error.
        var barMinutes = ResolutionCodes.MinutesOf(candleCode);
        var barInterval = barMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : (TimeSpan?)null;

        var result = MarketStructure.Read(
            candles.Select(c => new StructureBar(c.TimestampUtc, c.Open, c.High, c.Low, c.Close)).ToList(),
            swingMethod, strength, trigger, inducementMode, zoneMitigation, barInterval, fvgMinSize);

        return new SmcStructureResponse
        {
            Symbol = symbol,
            Resolution = ResolutionCodes.Label(candleCode),
            Method = swingMethod == SwingMethod.Fractal ? "fractal" : "validPullback",
            Strength = strength,
            BreakOn = trigger == BreakTrigger.Wick ? "wick" : "close",
            Zones = zoneMitigation switch
            {
                ZoneMitigation.Midpoint => "midpoint",
                ZoneMitigation.Close => "close",
                _ => "touch",
            },
            FvgMinSize = fvgMinSize,
            StandingZonesOnly = standingZonesOnly,
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
            // The engine stamps each zone with both a candle index and that
            // candle's time; only the time crosses the wire, because the candles
            // travel with the marks and every other mark here is placed by time.
            //
            // A zone the market has already spent is almost all of the weight and
            // almost none of the reading. Measured on NSE:NIFTYBANK-INDEX at 5m
            // over 3751 candles: 650 gaps of which 6 were still open, and 150
            // blocks of which 3 were still unmitigated — the spent ones are 139
            // of the 932 KB answer, fetched every fifteen seconds and thrown away
            // by the chart that asked for it. So the caller may ask for the
            // standing ones alone.
            //
            // This filters what is REPORTED, never what is read. The forward pass
            // above has already run over every candle, each mark still carries the
            // ConfirmedTimeUtc it was stamped with, and nothing is drawn earlier or
            // moved. A gap that fills tomorrow simply stops being reported then,
            // which is what the chart's own filter did to it before.
            //
            // Runs are deliberately left whole. A finished run is not spent the way
            // a mitigated block is — it is the record of which way the market was
            // being delivered over a stretch of the time axis, and a chart showing
            // only the current one would show a single band and no context to read
            // it against. They cost little either way: a window holds a few dozen
            // runs against several hundred gaps.
            OrderBlocks = result.OrderBlocks
                .Where(b => !standingZonesOnly || b.MitigatedTimeUtc is null)
                .Select(b => new SmcOrderBlockDto
                {
                    Direction = b.Direction == TrendDirection.Bearish ? "bearish" : "bullish",
                    Top = b.Top,
                    Bottom = b.Bottom,
                    TimeUtc = b.TimeUtc,
                    ConfirmedTimeUtc = b.ConfirmedTimeUtc,
                    MitigatedTimeUtc = b.MitigatedTimeUtc,
                })
                .ToList(),
            Gaps = result.Gaps
                .Where(g => !standingZonesOnly || g.FilledTimeUtc is null)
                .Select(g => new SmcFairValueGapDto
                {
                    Direction = g.Direction == TrendDirection.Bearish ? "bearish" : "bullish",
                    Top = g.Top,
                    Bottom = g.Bottom,
                    TimeUtc = g.TimeUtc,
                    ConfirmedTimeUtc = g.ConfirmedTimeUtc,
                    FilledTimeUtc = g.FilledTimeUtc,
                })
                .ToList(),
            OrderFlowRuns = result.OrderFlowRuns.Select(r => new SmcOrderFlowRunDto
            {
                Direction = r.Direction == TrendDirection.Bearish ? "bearish" : "bullish",
                FromTimeUtc = r.FromTimeUtc,
                ToTimeUtc = r.ToTimeUtc,
                InducedTimeUtc = r.InducedTimeUtc,
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
        };
    }

    /// <summary>
    /// The same structure read on several timeframes at once — the nested
    /// reading Smart Money Concepts works from, where a day's pullback is an
    /// hour's whole trend.
    /// </summary>
    /// <remarks>
    /// The chart's own timeframe comes back in full (candles and every mark);
    /// the higher ones come back as their state and the marks that are lines and
    /// points, so they can be drawn over those candles without fetching each one
    /// separately. Their zones are left out: a box is drawn between two candles,
    /// and a higher timeframe has none here.
    /// Each timeframe is read only from its own closed candles, so a daily
    /// level shown on a 5-minute chart is one the day had already set.
    /// </remarks>
    /// <param name="timeframes">Highest first, comma separated: "1D,15m,5m". The last one is the chart's own.</param>
    [HttpGet("ladder")]
    public async Task<ActionResult<SmcLadderResponse>> GetLadder(
        [FromQuery] string symbol,
        [FromQuery] string timeframes = "1D,15m,5m",
        [FromQuery] DateOnly? fromDate = null,
        [FromQuery] DateOnly? toDate = null,
        [FromQuery] string method = "validPullback",
        [FromQuery] int strength = MarketStructure.DefaultStrength,
        [FromQuery] string breakOn = "close",
        [FromQuery] string inducement = "last",
        [FromQuery] string zones = "touch",
        [FromQuery] decimal fvgMinSize = 0m,
        [FromQuery] bool standingZonesOnly = false,
        [FromQuery] bool includeLive = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new { message = "symbol is required." });

        var wanted = timeframes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ResolutionCodes.ToCandle)
            .Distinct()
            .ToList();
        if (wanted.Count == 0) return BadRequest(new { message = "timeframes is empty." });
        if (wanted.Count > 4) return BadRequest(new { message = "at most four timeframes." });

        var response = new SmcLadderResponse { Symbol = symbol };
        foreach (var code in wanted)
        {
            // The chart's own timeframe is the last one; only it carries candles,
            // and only it reaches back over the whole range the user asked for.
            var isChart = code == wanted[^1];
            var read = await ReadAsync(symbol, code, fromDate, toDate, method, strength, breakOn, inducement,
                zones, fvgMinSize, standingZonesOnly, includeLive, cancellationToken);
            if (isChart) response.Chart = read;
            else
            {
                read.Candles = [];
                // Zones are dropped with the candles for the same reason: a box
                // is drawn between two candles of the chart's own series, and a
                // higher timeframe has none here. Kept, they would add three
                // full sets per rung to every fifteen-second poll for nothing.
                read.OrderBlocks = [];
                read.Gaps = [];
                read.OrderFlowRuns = [];
                response.Higher.Add(read);
            }
        }

        return Ok(response);
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
