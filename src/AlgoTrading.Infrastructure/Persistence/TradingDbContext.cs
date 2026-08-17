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
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TradingDbContext).Assembly);
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

    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
}