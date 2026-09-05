using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.Auth;
using AlgoTrading.Application.UseCases.Instruments;
using AlgoTrading.Application.UseCases.LiveData;
using AlgoTrading.Application.UseCases.MarketData;
using AlgoTrading.Application.UseCases.Simulator;
using AlgoTrading.Contracts.MarketData;
using AlgoTrading.Application.Providers;
using AlgoTrading.Infrastructure.Config;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Providers;
using AlgoTrading.Infrastructure.Providers.Fyers;
using AlgoTrading.Infrastructure.Providers.Replay;
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
        services.Configure<RiskManagementSettings>(
            configuration.GetSection("RiskManagement"));

        services.AddDbContext<TradingDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("TradingDb")));

        services.AddScoped<IRiskManagementService, RiskManagementService>();
        services.AddSingleton<IRiskLimitsStore, RiskLimitsStore>();

        // Durable pids of the Python children (ingestor, runners), so a restarted
        // API can adopt or stop what the previous instance launched.
        services.AddScoped<IProcessSettingsStore, ProcessSettingsStore>();

        // Market intelligence (news RSS + day movers) — needs an HttpClient and
        // an in-memory cache so dashboard polling never hammers the sources.
        services.AddHttpClient(nameof(MarketIntelService));
        services.AddMemoryCache();
        services.AddScoped<IMarketIntelService, MarketIntelService>();

        // ---- Connectors -------------------------------------------------
        // Every vendor registers itself; this composition root never names one.
        // Which connector actually serves a job is a row in provider_bindings,
        // read at runtime by the router — not a compile-time binding.
        // The shipped adapters are fixed at build time; vendors an operator adds
        // live in data_vendors and are merged in per request by ProviderCatalog.
        var catalogSeed = new ProviderCatalogSeed();
        var credentialFallbacks = new ProviderCredentialFallbacks();
        services.AddSingleton(credentialFallbacks);
        services.AddSingleton(catalogSeed);
        services.AddScoped<IProviderCatalog, ProviderCatalog>();

        services.AddFyersProvider(configuration, catalogSeed, credentialFallbacks);
        services.AddReplayProvider(catalogSeed);

        services.AddScoped<IProviderRegistry, ProviderRegistry>();
        services.AddScoped<IProviderRouter, ProviderRouter>();
        services.AddScoped<ISymbolMapper, SymbolMapper>();

        services.AddScoped<IBrokerCredentialsProvider, DatabaseBrokerCredentialsProvider>();
        services.AddScoped<IMarketDataService, MarketDataService>();

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

        // Lot sizes: master value per contract, else the configured LotSizes table.
        services.AddSingleton(LotSizeOptions.FromConfiguration(configuration));
        services.AddScoped<ILotSizeResolver, LotSizeResolver>();

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
        services.AddScoped<IHistoricalCandleStore, HistoricalCandleStore>();
        services.AddScoped<IOptionHistoryBackfillService, OptionHistoryBackfillService>();

        services.AddScoped<IMarketTickArchiveService, MarketTickArchiveService>();


        services.AddSingleton<MarketTickArchiveQueue>();
        services.AddSingleton<IMarketTickArchiveQueue>(sp => sp.GetRequiredService<MarketTickArchiveQueue>());
        services.AddHostedService<MarketTickBatchWriterService>();

        services.AddScoped<IEquityGroupService, EquityGroupService>();

        services.AddScoped<ReferenceDataSeeder>();
        services.AddScoped<AdminBootstrapper>();

        services.AddScoped<IEquityLiveSnapshotService, EquityLiveSnapshotService>();

        services.AddScoped<AddEquityGroupToWatchlistUseCase>();

        // Redis Pub/Sub
        services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(sp => 
        {
            var connStr = configuration.GetConnectionString("Redis") ?? "localhost:6379";
            return StackExchange.Redis.ConnectionMultiplexer.Connect(connStr);
        });
        services.AddScoped<IRedisPublisherService, RedisPublisherService>();

        // One way for the platform to tell its operator something happened, on the
        // same path the strategy alerter already uses.
        services.AddScoped<ISystemNotifier, RedisSystemNotifier>();

        // Accounts, module grants and the grant check every guarded endpoint asks.
        services.AddScoped<IUserAdminService, UserAdminService>();
        services.AddScoped<IStrategyAccessService, StrategyAccessService>();

        return services;
    }
}
