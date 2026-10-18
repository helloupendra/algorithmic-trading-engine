using System.Text.Json.Serialization;

namespace AlgoTrading.Contracts.Portfolio;

// The Portfolio page's API (/api/Portfolio), as the console reads it. Every amount is in rupees. Units, as the page reads
// them: every weight, coveredWeight and hhi is a FRACTION from 0 to 1 (hhi is the sum of the squared weights); every
// field named "...Pct" is a PERCENT (12.5 is 12.5%), and maxDrawdownPct is a positive magnitude of a fall. A null is a
// figure that is not known, never a zero. docs/modules/portfolio.md describes each one.

/// <summary>One value the user pastes to link a token-mode broker.</summary>
public sealed record PortfolioTokenField(string Key, string Label, string Help);

/// <summary>One broker on the page: whether it is linked, and how to link it.</summary>
/// <param name="Status">not_connected, connected, expired or error.</param>
/// <param name="ConnectMode">redirect (the user signs in at the broker) or token (the user pastes a token they generated).</param>
public sealed record PortfolioConnectionView(
    string Broker,
    string Name,
    string Status,
    string? AccountLabel,
    DateTime? ConnectedUtc,
    DateTime? ExpiresUtc,
    DateTime? LastSyncUtc,
    string? LastError,
    string ConnectMode,
    IReadOnlyList<PortfolioTokenField> TokenFields);

public sealed record PortfolioConnectionsResponse(IReadOnlyList<PortfolioConnectionView> Brokers);

/// <summary>How to link: send the browser to <see cref="Url"/>, or ask for <see cref="Fields"/>.</summary>
public sealed record PortfolioStartResponse(
    string Mode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Url = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PortfolioTokenField>? Fields = null);

/// <summary>The pasted values, by <see cref="PortfolioTokenField.Key"/>.</summary>
public sealed record PortfolioTokenRequest(Dictionary<string, string>? Fields);

public sealed record PortfolioSyncResult(string Broker, bool Ok, string? Error);

public sealed record PortfolioSyncResponse(DateTime TakenUtc, IReadOnlyList<PortfolioSyncResult> Results);

/// <summary>Where a report's figures came from: each broker's last good read, and whether the latest attempt worked.</summary>
/// <param name="TakenUtc">The last good read: the snapshot the report's figures for this broker come from.</param>
/// <param name="Ok">False when the latest sync of this broker failed, or the broker is disconnected.</param>
/// <param name="Error">What failed, or "disconnected; last read …".</param>
/// <param name="AttemptedUtc">When the failed attempt was made; null when the latest attempt worked.</param>
public sealed record PortfolioSource(string Broker, DateTime TakenUtc, bool Ok, string? Error, DateTime? AttemptedUtc);

/// <summary>The portfolio's totals, over the holdings that have a price.</summary>
public sealed record PortfolioTotals(
    decimal Invested,
    decimal Current,
    decimal Pnl,
    decimal? PnlPct,
    decimal? DayChange,
    decimal? DayChangePct,
    int HoldingsCount);

/// <summary>One holding, merged across brokers by ISIN.</summary>
public sealed record PortfolioHoldingView(
    string Symbol,
    string Isin,
    string? Name,
    IReadOnlyList<string> Brokers,
    decimal Qty,
    decimal AvgPrice,
    decimal? Ltp,
    decimal Invested,
    decimal? Value,
    decimal? Pnl,
    decimal? PnlPct,
    decimal? DayChange,
    decimal? Weight,
    string Sector,
    string MarketCapBand);

public sealed record PortfolioSlice(string Name, decimal Value, decimal Weight);

public sealed record PortfolioAllocation(
    IReadOnlyList<PortfolioSlice> BySector,
    IReadOnlyList<PortfolioSlice> ByBroker,
    IReadOnlyList<PortfolioSlice> ByMarketCap);

public sealed record PortfolioConcentration(decimal? Top1Weight, decimal? Top5Weight, decimal? Hhi, IReadOnlyList<string> Largest);

/// <param name="LookbackDays">The trading days the figures cover (daily returns).</param>
/// <param name="CoveredWeight">The fraction of value whose holdings have a close on every one of those trading days.</param>
public sealed record PortfolioRisk(
    int LookbackDays,
    decimal? VolatilityAnnualPct,
    decimal? BetaVsNifty,
    decimal? MaxDrawdownPct,
    decimal CoveredWeight,
    string Note);

public sealed record PortfolioPositionView(
    string Broker,
    string Symbol,
    string Product,
    decimal Qty,
    decimal AvgPrice,
    decimal? Ltp,
    decimal? Pnl,
    bool IsDerivative);

public sealed record PortfolioDerivatives(int OpenPositions, decimal GrossExposure, string Note);

public sealed record PortfolioFundsView(string Broker, decimal? Available, decimal? Used, decimal? Total);

/// <summary>The written paragraph, shown only after it passed the number check and the advice guard.</summary>
public sealed record PortfolioSummary(string Text, string Model, bool Checked, DateTime GeneratedUtc);

public sealed record PortfolioReport(
    DateTime AsOfUtc,
    IReadOnlyList<PortfolioSource> Sources,
    PortfolioTotals Totals,
    IReadOnlyList<PortfolioHoldingView> Holdings,
    PortfolioAllocation Allocation,
    PortfolioConcentration Concentration,
    PortfolioRisk Risk,
    IReadOnlyList<PortfolioPositionView> Positions,
    PortfolioDerivatives Derivatives,
    IReadOnlyList<PortfolioFundsView> Funds,
    PortfolioSummary? Summary,
    IReadOnlyList<string> DataNotes,
    string Disclaimer);

public sealed record PortfolioHistoryPoint(DateOnly Date, decimal Invested, decimal Current, decimal Pnl);

/// <summary>For an admin: how many users linked each broker, by status. No amounts, no holdings, no names.</summary>
public sealed record PortfolioBrokerCount(string Broker, int Connected, int Expired, int Error);
