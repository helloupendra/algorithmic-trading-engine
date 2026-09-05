using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Persistence;

/// <summary>
/// The primary Entity Framework Core database context for the application.
/// Manages connection to the PostgreSQL database and maps domain entities to tables.
/// </summary>
public class TradingDbContext : DbContext
{
    public TradingDbContext(DbContextOptions<TradingDbContext> options)
        : base(options)
    {
    }
    public DbSet<Candle> Candles => Set<Candle>();
    public DbSet<BrokerSession> BrokerSessions => Set<BrokerSession>();
    public DbSet<BrokerConfig> BrokerConfigs => Set<BrokerConfig>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TradingDbContext).Assembly);

        // Data lineage: every price row records which connector produced it. The
        // column is shaped once here rather than in each configuration, so a table
        // that gains the property later cannot end up with a different width.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            entityType.FindProperty(nameof(Candle.SourceKey))?.SetMaxLength(32);
        }
    }
    public DbSet<Instrument> Instruments => Set<Instrument>();
    public DbSet<SymbolSyncState> SymbolSyncStates => Set<SymbolSyncState>();
    public DbSet<LiveWatchlistItem> LiveWatchlistItems => Set<LiveWatchlistItem>();
    public DbSet<LiveQuoteLatest> LiveQuotesLatest => Set<LiveQuoteLatest>();
    public DbSet<LiveIngestorStatus> LiveIngestorStatuses => Set<LiveIngestorStatus>();
    public DbSet<LiveTick> LiveTicks => Set<LiveTick>();
    public DbSet<LiveBar> LiveBars => Set<LiveBar>();
    public DbSet<SimulationRun> SimulationRuns => Set<SimulationRun>();

    public DbSet<SimulationSignal> SimulationSignals => Set<SimulationSignal>();
    public DbSet<PaperOrder> PaperOrders => Set<PaperOrder>();
    public DbSet<PaperPosition> PaperPositions => Set<PaperPosition>();
    public DbSet<SimulationEquitySnapshot> SimulationEquitySnapshots => Set<SimulationEquitySnapshot>();
    public DbSet<StrategyDefinition> Strategies => Set<StrategyDefinition>();

    public DbSet<ExpiryRule> ExpiryRules => Set<ExpiryRule>();

    public DbSet<MarketTick> MarketTicks => Set<MarketTick>();

    public DbSet<EquityGroup> EquityGroups => Set<EquityGroup>();
    public DbSet<EquityGroupMember> EquityGroupMembers => Set<EquityGroupMember>();

    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<UserRefreshToken> UserRefreshTokens => Set<UserRefreshToken>();
    public DbSet<UserModuleGrant> UserModuleGrants => Set<UserModuleGrant>();
    public DbSet<StrategyPackage> StrategyPackages => Set<StrategyPackage>();
    public DbSet<StrategyPackageItem> StrategyPackageItems => Set<StrategyPackageItem>();
    public DbSet<UserStrategyGrant> UserStrategyGrants => Set<UserStrategyGrant>();
    public DbSet<UserWatchlistItem> UserWatchlistItems => Set<UserWatchlistItem>();
    public DbSet<UserInvite> UserInvites => Set<UserInvite>();
    public DbSet<ActivityLogEntry> ActivityLog => Set<ActivityLogEntry>();

    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    public DbSet<RiskEvent> RiskEvents => Set<RiskEvent>();
    public DbSet<AlertEvent> AlertEvents => Set<AlertEvent>();

    // Multi-provider foundation: who we can connect to, who serves what, and
    // what each vendor calls an instrument we already know.
    public DbSet<BrokerAccount> BrokerAccounts => Set<BrokerAccount>();
    public DbSet<ProviderBinding> ProviderBindings => Set<ProviderBinding>();
    public DbSet<InstrumentVendorSymbol> InstrumentVendorSymbols => Set<InstrumentVendorSymbol>();
    public DbSet<DataVendor> DataVendors => Set<DataVendor>();
}