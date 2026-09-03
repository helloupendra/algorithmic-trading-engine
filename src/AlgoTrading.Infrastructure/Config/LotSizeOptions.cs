// src/AlgoTrading.Infrastructure/Config/LotSizeOptions.cs
using Microsoft.Extensions.Configuration;

namespace AlgoTrading.Infrastructure.Config;

/// <summary>
/// Fallback lot sizes by underlying, used when the instrument master carries no
/// LotSize for a contract. Bound from the "LotSizes" appsettings section; the
/// in-code defaults match the Sep-2026 FYERS master and are overridden key by key.
/// </summary>
public sealed class LotSizeOptions
{
    public const string SectionName = "LotSizes";

    private static readonly Dictionary<string, int> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NIFTY"] = 65,
        ["BANKNIFTY"] = 30,
        ["FINNIFTY"] = 60,
        ["MIDCPNIFTY"] = 120,
        ["NIFTYNXT50"] = 25,
        ["SENSEX"] = 20,
        ["BANKEX"] = 30
    };

    private readonly Dictionary<string, int> _byUnderlying;

    public LotSizeOptions(IDictionary<string, int>? overrides = null)
    {
        _byUnderlying = new Dictionary<string, int>(Defaults, StringComparer.OrdinalIgnoreCase);
        if (overrides is null) return;
        foreach (var (key, value) in overrides)
        {
            if (!string.IsNullOrWhiteSpace(key) && value > 0)
                _byUnderlying[key.Trim()] = value;
        }
    }

    /// <summary>
    /// Reads the "LotSizes" section (a flat object of underlying -> lot size).
    /// </summary>
    public static LotSizeOptions FromConfiguration(IConfiguration configuration)
    {
        var overrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in configuration.GetSection(SectionName).GetChildren())
        {
            if (int.TryParse(child.Value, out var n) && n > 0)
                overrides[child.Key] = n;
        }
        return new LotSizeOptions(overrides);
    }

    public bool TryGet(string underlying, out int lotSize)
        => _byUnderlying.TryGetValue((underlying ?? string.Empty).Trim(), out lotSize);
}
