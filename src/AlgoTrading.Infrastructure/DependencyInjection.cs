using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.Auth;
using AlgoTrading.Application.UseCases.Instruments;
using AlgoTrading.Application.UseCases.LiveData;
using AlgoTrading.Application.UseCases.MarketData;
using AlgoTrading.Application.UseCases.Simulator;
using AlgoTrading.Contracts.MarketData;
using AlgoTrading.Infrastructure.Brokers.Fyers;
using AlgoTrading.Infrastructure.Config;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Session;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AlgoTrading.Application.UseCases.Equities;

namespace AlgoTrading.Infrastructure;


using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Services;


/// <summary>
/// Extension methods for configuring infrastructure services in the dependency injection container.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers database contexts, external broker clients, use cases, and repositories.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<FyersSettings>(
            configuration.GetSection("Fyers"));

        services.Configure<RiskManagementSettings>(
            configuration.GetSection("RiskManagement"));

        services.AddDbContext<TradingDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("TradingDb")));

        services.AddScoped<IRiskManagementService, RiskManagementService>();
        services.AddScoped<IBrokerAuthService, FyersAuthService>();
        services.AddScoped<IMarketDataService, FyersMarketDataService>();

        services.AddScoped<StartBrokerAuthUseCase>();
        services.AddScoped<GenerateAccessTokenUseCase>();
        services.AddScoped<SyncHistoryUseCase>();
        services.AddScoped<GetStoredCandlesUseCase>();

        services.AddScoped<IBrokerSessionStore, DatabaseBrokerSessionStore>();

        services.AddScoped<ISymbolUniverseService, SymbolUniverseService>();
        services.AddScoped<EnsureHistoryCoverageUseCase>();

        services.AddScoped<IInstrumentImportService, LocalCsvInstrumentImportService>();
        services.AddScoped<ImportInstrumentsFromFileUseCase>();


        services.AddScoped<ILiveDataService, LiveDataService>();

        services.AddScoped<GetWatchlistUseCase>();
        services.AddScoped<UpsertWatchlistItemUseCase>();
        services.AddScoped<RemoveWatchlistItemUseCase>();
        services.AddScoped<GetLatestQuoteUseCase>();
        services.AddScoped<GetAllLatestQuotesUseCase>();
        services.AddScoped<UpsertLiveQuoteUseCase>();

        services.AddScoped<ILiveDataService, LiveDataService>();

        services.AddScoped<GetWatchlistUseCase>();
        services.AddScoped<UpsertWatchlistItemUseCase>();
        services.AddScoped<RemoveWatchlistItemUseCase>();
        services.AddScoped<GetLatestQuoteUseCase>();
        services.AddScoped<GetAllLatestQuotesUseCase>();
        services.AddScoped<UpsertLiveQuoteUseCase>();

        services.AddScoped<UpsertHeartbeatUseCase>();
        services.AddScoped<GetIngestorStatusUseCase>();
        services.AddScoped<GetAllIngestorStatusesUseCase>();
        services.AddScoped<GetStaleQuotesUseCase>();

        services.AddScoped<UpsertLiveTickUseCase>();
        services.AddScoped<GetRecentTicksUseCase>();
        services.AddScoped<GetRecentBarsUseCase>();

        services.AddSingleton<IMarketSessionService, MarketSessionService>();


        services.AddScoped<ISimulationService, SimulationService>();

        services.AddScoped<CreateSimulationRunUseCase>();
        services.AddScoped<GetSimulationRunUseCase>();
        services.AddScoped<GetSimulationRunsUseCase>();



        services.AddScoped<IReplayFeedProvider, ReplayFeedProvider>();
        services.AddScoped<ISimulationRunner, SimulationRunnerService>();

        services.AddScoped<StartSimulationRunUseCase>();


        services.AddScoped<IDerivativesInstrumentService, DerivativesInstrumentService>();

        services.AddScoped<IPaperTradingService, PaperTradingService>();

        services.AddScoped<CreateSimulationSignalUseCase>();
        services.AddScoped<GetSimulationSignalsUseCase>();
        services.AddScoped<GetPaperOrdersUseCase>();
        services.AddScoped<GetPaperPositionsUseCase>();
        services.AddScoped<GetSimulationPortfolioUseCase>();

        services.AddScoped<RefreshSimulationPortfolioUseCase>();
        services.AddScoped<GetSimulationEquityCurveUseCase>();
        services.AddScoped<GetSimulationPerformanceUseCase>();

        services.AddScoped<IExpiryResolverService, ExpiryResolverService>();
        services.AddScoped<IFyersHistoryClient, FyersHistoryClient>();
        services.AddScoped<IHistoricalCandleStore, HistoricalCandleStore>();
        services.AddScoped<IOptionHistoryBackfillService, OptionHistoryBackfillService>();

        services.AddScoped<IMarketTickArchiveService, MarketTickArchiveService>();


        services.AddSingleton<MarketTickArchiveQueue>();
        services.AddSingleton<IMarketTickArchiveQueue>(sp => sp.GetRequiredService<MarketTickArchiveQueue>());
        services.AddHostedService<MarketTickBatchWriterService>();

        services.AddScoped<IEquityGroupService, EquityGroupService>();

        services.AddScoped<ReferenceDataSeeder>();

        services.AddScoped<IEquityLiveSnapshotService, EquityLiveSnapshotService>();

        services.AddScoped<AddEquityGroupToWatchlistUseCase>();

        // Redis Pub/Sub
        services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(sp => 
        {
            var connStr = configuration.GetConnectionString("Redis") ?? "localhost:6379";
            return StackExchange.Redis.ConnectionMultiplexer.Connect(connStr);
        });
        services.AddScoped<IRedisPublisherService, RedisPublisherService>();

        return services;
    }
}
