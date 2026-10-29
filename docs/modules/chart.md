# Chart and indicators

Markets → Chart (`/markets/chart`) draws one symbol's candles at 1m, 5m, 15m, 30m, 1h, a day or a week, with indicators over the price (SMA, EMA, session VWAP, Bollinger Bands, Supertrend) and in panes under it (RSI, MACD). The Smart Money Concepts layer reads the same chart ([its rules](../smart-money-concepts.md)).

The resolution and range buttons offer only what the installation holds for the symbol (`GET /api/MarketData/coverage`), so a reader never lands on an empty chart without knowing why.

## Where the candles come from

Two sources, laid end to end in the browser (`web/src/lib/chart.ts`, `stitch`):

- **Stored candles**, `GET /api/MarketData/history/local`, served as stored. The nightly archive writes 1, 5 and 15 minute candles from each day's live 1-minute bars; broker backfills add their own, daily candles included.
- **Today's live 1-minute bars**, `GET /api/LiveData/bars`, laid after the last stored candle and rolled up to the stored series' resolution.

| Resolution | Built from |
|---|---|
| 1m, 5m, 15m | Stored candles of that resolution, then the live minutes rolled up on the UTC clock (the server's `LiveBarRollup` rule, which lands on the 09:15 and 09:00 opens for these three). |
| 30m, 1h | The coarsest stored intraday series the symbol has (15m, else 5m, else 1m) and the live minutes, folded into candles that start at the session's open. |
| Day | Stored daily candles. |
| Week | Stored daily candles, folded into IST weeks. |

Nothing stores 30m, 1h or weekly candles, so they are built in the browser, where the live minutes are already rolled up. A server-side version would still need the same rule in the browser for today's live tail, so there is one implementation of it instead of two.

### 30m and 1h start at the open

A UTC hour starts at 09:30 IST, so an hourly candle floored to the clock would run 09:30-10:30. Candles count from the session's open instead, the rule `SessionBarAggregator` uses for the indicator alerts:

- **NSE and BSE** open at 09:15. Half hours start at 09:15, 09:45 and so on up to 15:15. Hours run 09:15-10:15 up to 14:15-15:15, and the last one, 15:15-15:30, is cut short by the close.
- **MCX** opens at 09:00. Its hours run 09:00, 10:00 and on into the evening.

A candle with missing minutes is built from the minutes it has. A stretch with no data has no candle, so a feed outage shows as the gap it was, not as a flat candle.

### The week

A week is the days an IST week traded, Monday to Friday. A holiday is simply missing. The week opens at the first day's open, closes at the last day's close, and takes the extremes and the summed volume. It is drawn at its first trading day. A weekend session (Muhurat trading has fallen on a Sunday) joins the week it closes. When a range window starts in the middle of a week, that week is dropped, because its first days were never fetched.

Daily candles are stamped at 00:00 IST, or at 00:00 UTC (as the stored NIFTY, RELIANCE and SBIN days are, and the option history's). Either way the candle is a date, not a minute. Until 30 Oct 2026 the NSE/BSE pre-open rule (nothing before 09:15) was also applied to daily candles, and every one stamped 00:00 UTC was dropped by it, which left those symbols' day chart empty.

### The forming candle

The live bars are polled every 15 seconds. Between polls, the last candle moves with the symbol's pushed price (`withLiveTick`), and a price past its bucket opens the next candle by the same session-anchored rule. On 30m and 1h, today's minutes keep filling the last bucket as they arrive. The day and the week come from stored daily candles alone, so today's session joins them once its daily candle is stored.

## Indicators

All of them are pure functions in `web/src/lib/indicators.ts`. Where the strategies compute the same thing, the chart follows `strategies/indicators.py` in the Python engine line for line, and `indicators.test.ts` holds both to numbers the Python functions printed on the same candles. A chart that drew RSI one way while a strategy traded it another would be showing a number nothing acts on.

| Indicator | Drawn | Definition | Same as the strategies |
|---|---|---|---|
| SMA (period, default 50) | over the price | The mean of the last N closes. | `sma` |
| EMA (period, default 20) | over the price | Seeded with the SMA of the first N closes, then `close × k + previous × (1 − k)` with `k = 2 / (N + 1)`. | `ema` |
| VWAP (session) | over the price, intraday only | The typical price `(high + low + close) / 3` weighted by volume, from the session's first candle. It starts again every IST session. Candles with no volume add nothing, so an index has no VWAP. | `vwap(session_bars(…))` |
| Bollinger Bands (20, 2) | over the price | The 20-candle SMA, plus and minus two population standard deviations of the same closes. | not in the Python file |
| Supertrend (10, 3) | over the price | Wilder's ATR(10), bands at the median price ± 3 ATRs. Each band only tightens while the trend holds, and a close through the active band turns it. It starts pointing up and gives its first value on candle 11. | `supertrend` (unrounded) |
| RSI (14) | pane, 70 and 30 marked | Wilder's: the first 14 changes are averaged, then smoothed. A window with no losses is 100 if it rose and 50 if it did not move. | `rsi` |
| MACD (12, 26, 9) | pane | EMA(12) less EMA(26), each seeded as above. The signal is the EMA(9) of that, and the histogram is the gap between them. | built from `ema` |

The alerts' `IndicatorMath.cs` ports the same four (EMA, RSI, Supertrend, VWAP), so the chart, the strategies and the alerts agree.

Two differences to know about when reading a value off the chart:

- **The chart reads only the candles in its range.** Near the left edge, an EMA or an RSI has seen less history than a strategy's warm-up gives it. About five periods in (four for an EMA), the seed weighs under 1% of the value. That is the settle rule the alerts insist on before they alert.
- **The chart's VWAP weighs the candles drawn.** On 1m that is the alerts' definition, which reads 1-minute bars. On 15m it weighs fifteen-minute typical prices, as a strategy running on 15m bars does.

The menu at the end of the toolbar turns each indicator on and off and sets the two periods. The choice is remembered in the browser (`localStorage`, key `openfno.chart.indicators`). Every read and write is guarded, so a private window starts on the plain chart instead of breaking. VWAP's switch is off on a day or a week chart. The menu is off while SMC is on, because that layer draws its own chart.

### Speed

Each indicator is a fold over the candles: a state carried from one candle to the next. The closed candles are folded once. Each pushed price steps every indicator once on the forming candle and moves the last point of each line. Nothing is recomputed over the whole series, and no line is handed its data again, which matters on a day of 1-minute candles with a price arriving several times a second.

## SMC on the new resolutions

The SMC layer reads stored candles on the server (`GET /api/Smc/ladder`), and none are stored at 30m, 1h or a week. So it is offered on 1m, 5m, 15m and the day. While it is on, the three other resolutions are off.

## Code

- `web/src/lib/chart.ts`: resolutions, ranges, coverage, `stitch`, the session-anchored `aggregate`, `weekly`, the forming candle, and the indicator settings.
- `web/src/lib/indicators.ts`: the indicator maths, `series` and the `cursor` the forming candle is read with.
- `web/src/components/charts.tsx`: `PriceChart`, the series and panes, and the per-tick update of the indicator lines.
- `web/src/pages/markets/chart/`: the page and the indicators menu.
- Tests: `web/src/lib/chart.test.ts` and `web/src/lib/indicators.test.ts`.
