using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Worker.MarketData.Configuration;
using AlgoTrading.Worker.MarketData.Models;
using AlgoTrading.Worker.MarketData.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// What the market-data worker writes. The API is the one writer of
/// live_quotes_latest; the worker only archives, unless it is told it is the
/// writer — and then it keeps the API's rule that a quote never moves backwards
/// in exchange time.
/// </summary>
public class TickBatchProcessorTests
{
    private const string Symbol = "NSE:NIFTY2693025000CE";
    private static readonly DateTime Newer = new(2026, 9, 28, 4, 0, 5, DateTimeKind.Utc);
    private static readonly DateTime Older = new(2026, 9, 28, 4, 0, 1, DateTimeKind.Utc);

    private static (TickBatchProcessor Processor, TradingDbContext Db) Build(bool projectLatestQuotes = false)
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase($"tick-batch-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new TradingDbContext(options);
        var processor = new TickBatchProcessor(
            db,
            NullLogger<TickBatchProcessor>.Instance,
            Microsoft.Extensions.Options.Options.Create(new RedisStreamOptions { ProjectLatestQuotes = projectLatestQuotes }));
        return (processor, db);
    }

    private static MarketTickStreamMessage Tick(decimal ltp, DateTime exchangeUtc, bool isReplay = false,
        DateTime? receivedUtc = null) => new()
    {
        Symbol = Symbol,
        LastTradedPrice = ltp,
        ExchangeTimestampUtc = exchangeUtc,
        ReceivedUtc = receivedUtc ?? exchangeUtc.AddMilliseconds(40),
        IsReplay = isReplay,
        SourceKey = "dhan",
        Delta = 0.52m,
    };

    [Fact]
    public async Task By_default_the_latest_quote_table_is_left_to_the_api()
    {
        var (processor, db) = Build();

        await processor.ProcessAsync([Tick(120m, Newer)]);

        Assert.Empty(db.LiveQuotesLatest);
        Assert.Single(db.MarketTicks);
    }

    [Fact]
    public async Task A_redelivered_batch_that_was_already_archived_is_not_archived_twice()
    {
        var (processor, db) = Build();
        var first = Tick(120m, Newer);

        await processor.ProcessAsync([first]);
        // The commit landed but the acknowledgement did not: the same tick again,
        // alongside one that is genuinely new.
        await processor.ProcessAsync([Tick(120m, Newer), Tick(121m, Newer.AddSeconds(1))], redelivered: true);

        Assert.Equal(2, db.MarketTicks.Count());
        Assert.Equal(new decimal?[] { 120m, 121m }, db.MarketTicks.OrderBy(x => x.LastTradedPrice).Select(x => x.LastTradedPrice));
    }

    [Fact]
    public async Task The_archive_row_keeps_the_vendor()
    {
        var (processor, db) = Build();
        await processor.ProcessAsync([Tick(120m, Newer)]);
        Assert.Equal("dhan", db.MarketTicks.Single().SourceKey);
    }

    [Fact]
    public async Task When_it_is_the_writer_an_older_live_tick_never_overwrites_a_newer_quote()
    {
        var (processor, db) = Build(projectLatestQuotes: true);

        await processor.ProcessAsync([Tick(120m, Newer)]);
        await processor.ProcessAsync([Tick(99m, Older)]);

        var quote = db.LiveQuotesLatest.AsNoTracking().Single();
        Assert.Equal(120m, quote.LastTradedPrice);
        Assert.Equal(Newer, quote.ExchangeTimestampUtc);
    }

    [Fact]
    public async Task A_replay_is_taken_although_it_is_older_as_the_api_takes_it()
    {
        var (processor, db) = Build(projectLatestQuotes: true);

        await processor.ProcessAsync([Tick(120m, Newer)]);
        await processor.ProcessAsync([Tick(99m, Older, isReplay: true)]);

        Assert.Equal(99m, db.LiveQuotesLatest.AsNoTracking().Single().LastTradedPrice);
    }

    [Fact]
    public async Task A_new_symbol_that_ticks_twice_in_one_batch_gets_one_row()
    {
        // The old projection added a row per tick for a symbol it had not seen,
        // and the unique index on symbol failed the whole batch.
        var (processor, db) = Build(projectLatestQuotes: true);

        await processor.ProcessAsync([Tick(120m, Older), Tick(121m, Newer)]);

        var quote = db.LiveQuotesLatest.AsNoTracking().Single();
        Assert.Equal(121m, quote.LastTradedPrice);
        Assert.Equal(0.52m, quote.Delta);
        Assert.Equal("dhan", quote.SourceKey);
    }

    [Fact]
    public async Task The_quote_is_stamped_when_it_was_written_not_with_the_exchange_clock()
    {
        // Readers age a quote by UpdatedUtc (the runner refuses one over a minute
        // old); stamped with a replay's exchange clock it looked hours stale.
        var (processor, db) = Build(projectLatestQuotes: true);
        var before = DateTime.UtcNow;

        await processor.ProcessAsync([Tick(120m, Older, isReplay: true)]);

        Assert.True(db.LiveQuotesLatest.AsNoTracking().Single().UpdatedUtc >= before);
    }

    [Fact]
    public void A_stamp_read_with_an_offset_is_converted_to_utc_not_relabelled()
    {
        var local = new DateTime(2026, 9, 28, 9, 30, 0, DateTimeKind.Local);
        Assert.Equal(local.ToUniversalTime(), TickBatchProcessor.EnsureUtc(local));

        var unspecified = new DateTime(2026, 9, 28, 4, 0, 0, DateTimeKind.Unspecified);
        var asUtc = TickBatchProcessor.EnsureUtc(unspecified)!.Value;
        Assert.Equal(DateTimeKind.Utc, asUtc.Kind);
        Assert.Equal(unspecified.Ticks, asUtc.Ticks);
    }
}
