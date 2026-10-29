using System.Text.Json;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Application.Exceptions;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Contracts.MarketData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Providers.Fyers;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// A backfill asked for before the morning's FYERS sign-in is a 409 that says
/// where to sign in.
/// </summary>
/// <remarks>
/// FYERS's token ends at 06:00 IST every day and its refresh is switched off, so
/// until the owner signs in every history request finds no session. Until 29 Oct
/// 2026 <c>POST /api/Backfill/history</c> let that escape as an
/// InvalidOperationException, and the console showed "An unexpected error
/// occurred" (500). The path here is the real one from the controller to the
/// FYERS provider; only the database (in memory) and the router are stand-ins.
/// </remarks>
public class BackfillHistoryTests
{
    private const string Nifty = "NSE:NIFTY50-INDEX";

    [Fact]
    public async Task A_backfill_with_no_FYERS_session_is_409_saying_where_to_sign_in()
    {
        await using var db = NewDb();
        db.Instruments.Add(new Instrument { Symbol = Nifty, Exchange = "NSE", Segment = "CM", InstrumentType = "INDEX", IsEnabled = true });
        await db.SaveChangesAsync();

        var result = await Controller(db).BackfillHistory(Request(), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        var body = JsonSerializer.SerializeToElement(conflict.Value);
        Assert.Equal(FyersMarketDataProvider.NotSignedInMessage, body.GetProperty("message").GetString());
        Assert.Equal("fyers", body.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task The_message_names_the_page_and_survives_the_consoles_error_line()
    {
        var provider = Fyers();

        var refused = await Assert.ThrowsAsync<VendorNotConnectedException>(() =>
            provider.GetHistoryAsync(Nifty, "5", new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Equal("fyers", refused.ProviderKey);
        Assert.Contains("Sign in on Connectors → FYERS", refused.Message);
        // The console's InlineError shows a message up to its first " at ", where a stack trace's frames begin.
        Assert.DoesNotContain(" at ", refused.Message);
        // Still an InvalidOperationException: the endpoints that answer one with 400 and its message keep doing so.
        Assert.IsAssignableFrom<InvalidOperationException>(refused);
    }

    [Fact]
    public async Task Any_other_failure_is_left_to_the_exception_handler()
    {
        var controller = new BackfillController(new EnsureHistoryCoverageUseCase(new FailingUniverse()), archive: null!);

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.BackfillHistory(Request(), CancellationToken.None));
    }

    private static BackfillHistoryRequest Request() => new()
    {
        Symbol = Nifty,
        Resolution = "5",
        FromDate = new DateOnly(2026, 10, 12),
        ToDate = new DateOnly(2026, 10, 16),
    };

    private static BackfillController Controller(TradingDbContext db)
    {
        var marketData = new MarketDataService(new FyersOnly(Fyers()), candleStore: null!, db, NullLogger<MarketDataService>.Instance);
        return new BackfillController(new EnsureHistoryCoverageUseCase(new SymbolUniverseService(db, marketData)), archive: null!);
    }

    /// <summary>The FYERS provider with no session stored: it returns before it needs credentials, a mapper or HTTP.</summary>
    private static FyersMarketDataProvider Fyers() => new(
        Options.Create(new FyersSettings()), credentials: null!, new NoSessions(), symbolMapper: null!, httpClientFactory: null!,
        NullLogger<FyersMarketDataProvider>.Instance);

    private static TradingDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase($"backfill-{Guid.NewGuid():N}").Options);

    private sealed class FyersOnly(IMarketDataProvider fyers) : IProviderRouter
    {
        public Task<IReadOnlyList<IMarketDataProvider>> ResolveDataChainAsync(ProviderCapability capability, string? segment = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IMarketDataProvider>>([fyers]);
        public Task<IMarketDataProvider> ResolveDataAsync(ProviderCapability capability, string? segment = null, CancellationToken cancellationToken = default)
            => Task.FromResult(fyers);
        public Task<IBrokerProvider> ResolveBrokerAsync(long? brokerAccountId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class NoSessions : IBrokerSessionStore
    {
        public Task<BrokerSession?> GetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult<BrokerSession?>(null);
        public Task<BrokerSession?> GetForAccountAsync(long brokerAccountId, CancellationToken cancellationToken = default) => Task.FromResult<BrokerSession?>(null);
        public Task ClearAccountAsync(long brokerAccountId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<BrokerSession?> GetForProviderAsync(string providerKey, CancellationToken cancellationToken = default) => Task.FromResult<BrokerSession?>(null);
        public Task SaveAsync(BrokerSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearAsync(string? providerKey = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FailingUniverse : ISymbolUniverseService
    {
        public Task<BackfillHistoryResponse> EnsureHistoryCoverageAsync(BackfillHistoryRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("FYERS history API failed. HTTP 422: Invalid symbol provided");
    }
}
