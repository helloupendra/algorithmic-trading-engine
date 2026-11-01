namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One mutual fund scheme as AMFI lists it, one row per AMFI scheme code, cleaned
/// by us: AMFI's own file leaves the plan blank on about 40% of its rows and
/// spells the growth option a dozen ways. Table <c>mf_schemes</c>.
/// </summary>
/// <remarks>
/// An AMFI scheme code names one plan and one option of a fund ("UTI Nifty 50
/// Index Fund, Direct Plan, Growth" is 120716), so a fund with direct and
/// regular plans, each with growth and IDCW, is four rows here. The daily file
/// (<c>NAVAll.txt</c>) is the source of the name, the AMC and the category; the
/// history download adds the schemes that have since closed. See
/// docs/modules/mutual_funds_module.md.
/// </remarks>
public class MutualFundScheme
{
    /// <summary>AMFI's scheme code: the key everything else joins on.</summary>
    public int SchemeCode { get; set; }

    /// <summary>The growth ISIN, or the IDCW payout ISIN (AMFI's "ISIN Div Payout/ ISIN Growth"). Null when AMFI gives none or something that is not an ISIN.</summary>
    public string? IsinGrowth { get; set; }

    /// <summary>The IDCW reinvestment ISIN, when the option has one.</summary>
    public string? IsinReinvest { get; set; }

    /// <summary>The fund house, from the AMC line above the scheme in AMFI's file ("UTI Mutual Fund").</summary>
    public string Amc { get; set; } = string.Empty;

    /// <summary>
    /// The name as AMFI writes it. In the daily file that is the fund's base name
    /// ("UTI Nifty 50 Index Fund"); for a scheme only the history download knows,
    /// it is the full NAV name with the plan and option in it.
    /// </summary>
    public string SchemeName { get; set; } = string.Empty;

    /// <summary><see cref="MutualFundPlans"/>: direct, regular or unknown.</summary>
    public string Plan { get; set; } = MutualFundPlans.Unknown;

    /// <summary><see cref="MutualFundOptions"/>: growth, idcw, other or unknown.</summary>
    public string Option { get; set; } = MutualFundOptions.Unknown;

    /// <summary>AMFI's own words for the option ("Monthly IDCW Option"), which say what <see cref="Option"/> does not: how often an IDCW pays.</summary>
    public string? OptionLabel { get; set; }

    /// <summary>The section line the scheme sits under, e.g. "Open Ended Schemes(Equity Scheme - Large Cap Fund)".</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>The oldest NAV date stored for the scheme.</summary>
    public DateOnly? FirstNavDate { get; set; }

    /// <summary>The newest NAV date AMFI has given for the scheme. Old for a dormant scheme: some still show a 2018 NAV.</summary>
    public DateOnly? LastNavDate { get; set; }

    /// <summary>The NAV on <see cref="LastNavDate"/>.</summary>
    public decimal? LastNav { get; set; }

    /// <summary>A NAV within 30 days of the newest NAV day in AMFI's latest file.</summary>
    public bool IsActive { get; set; }

    /// <summary>When a sync first read the scheme.</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>When a sync last read the scheme in AMFI's daily file (or, for a closed scheme, in its history).</summary>
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>The values of <see cref="MutualFundScheme.Plan"/>.</summary>
public static class MutualFundPlans
{
    public const string Direct = "direct";
    public const string Regular = "regular";

    /// <summary>AMFI gave no plan and the name does not say: ETFs, and many closed or old schemes.</summary>
    public const string Unknown = "unknown";
}

/// <summary>The values of <see cref="MutualFundScheme.Option"/>.</summary>
public static class MutualFundOptions
{
    public const string Growth = "growth";

    /// <summary>Income distribution cum capital withdrawal: the old "dividend" option, payout or reinvestment.</summary>
    public const string Idcw = "idcw";

    /// <summary>A named option that is neither: bonus, unclaimed amounts.</summary>
    public const string Other = "other";

    /// <summary>AMFI gave no option and neither the name nor the ISINs say.</summary>
    public const string Unknown = "unknown";
}
