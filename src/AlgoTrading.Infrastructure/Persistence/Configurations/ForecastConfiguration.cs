using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="Forecast"/>.</summary>
public class ForecastConfiguration : IEntityTypeConfiguration<Forecast>
{
    public void Configure(EntityTypeBuilder<Forecast> builder)
    {
        builder.ToTable("forecasts");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.ModelKey).IsRequired().HasMaxLength(ForecastModelConfiguration.KeyLength);
        builder.Property(x => x.ModelVersion).IsRequired().HasMaxLength(ForecastModelConfiguration.VersionLength);
        builder.Property(x => x.Target).IsRequired().HasMaxLength(16);
        builder.Property(x => x.Underlying).IsRequired().HasMaxLength(16);
        builder.Property(x => x.SessionDate).IsRequired();
        builder.Property(x => x.IssuedUtc).IsRequired();

        // Text, not jsonb: the shapes belong to the Python models and differ by
        // target, and nothing queries inside them. The scoreboard reads the two
        // losses from their own columns.
        builder.Property(x => x.PredictionJson).IsRequired().HasColumnType("text");
        builder.Property(x => x.BaselineJson).IsRequired().HasColumnType("text");
        builder.Property(x => x.InputsJson).IsRequired().HasColumnType("text");
        builder.Property(x => x.OutcomeJson).HasColumnType("text");
        builder.Property(x => x.ScoresJson).HasColumnType("text");

        // A forecast is scored once. Two scorers racing (the scheduler's run and
        // one started by hand) would both read "not scored" and the second would
        // overwrite the first; with the token its update matches nothing and the
        // API answers 409 instead.
        builder.Property(x => x.ScoredUtc).IsConcurrencyToken();

        // One forecast per model version, target, index and session. This is
        // what makes "issue it again with a better number" impossible, and it
        // holds even if two issuing jobs race past the API's own check.
        builder.HasIndex(x => new { x.ModelKey, x.ModelVersion, x.Target, x.Underlying, x.SessionDate })
            .IsUnique()
            .HasDatabaseName("UX_forecasts_model_target_underlying_session");

        // The page's question: what was forecast for these sessions.
        builder.HasIndex(x => x.SessionDate).HasDatabaseName("IX_forecasts_SessionDate");

        // Only a registered version can forecast, so every row on the
        // scoreboard traces to a description and a backtest. Restrict: a model
        // with a live record cannot be deleted out from under it.
        builder.HasOne<ForecastModel>()
            .WithMany()
            .HasForeignKey(x => new { x.ModelKey, x.ModelVersion })
            .HasPrincipalKey(m => new { m.Key, m.Version })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>EF configuration for <see cref="ForecastModel"/>.</summary>
public class ForecastModelConfiguration : IEntityTypeConfiguration<ForecastModel>
{
    /// <summary>Longest model key accepted, e.g. <c>range.har-vix</c>.</summary>
    public const int KeyLength = 64;

    /// <summary>Longest version accepted, e.g. <c>2026-09-27.1</c>.</summary>
    public const int VersionLength = 64;

    public void Configure(EntityTypeBuilder<ForecastModel> builder)
    {
        builder.ToTable("forecast_models");

        builder.HasKey(x => new { x.Key, x.Version });

        builder.Property(x => x.Key).IsRequired().HasMaxLength(KeyLength);
        builder.Property(x => x.Version).IsRequired().HasMaxLength(VersionLength);
        builder.Property(x => x.Target).IsRequired().HasMaxLength(16);
        builder.Property(x => x.Description).IsRequired().HasColumnType("text");
        builder.Property(x => x.BacktestJson).HasColumnType("text");
        builder.Property(x => x.RegisteredUtc).IsRequired();
        builder.Property(x => x.UpdatedUtc).IsRequired();
    }
}
