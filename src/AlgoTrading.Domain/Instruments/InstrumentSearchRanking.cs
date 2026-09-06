using System;
using System.Linq.Expressions;
using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Domain.Instruments;

/// <summary>
/// How well an instrument matches what someone typed.
/// </summary>
/// <remarks>
/// Searching "NIFTY50" used to return a page of ETFs and not the index itself.
/// The reason is that the description of every NIFTY<b>500</b> fund contains
/// "NIFTY50" as a substring, and the results were ordered by symbol — so
/// AXISVALUE, EMULTIMQ and EQUAL50 all sorted above NSE:NIFTY50-INDEX, which
/// was in the master the whole time. Alphabetical order is not a relevance
/// order.
/// <para>
/// Lower is better. The tiers are about how much of the SYMBOL the query
/// accounts for, because that is what someone is reaching for when they type;
/// a description-only match is the last resort.
/// </para>
/// </remarks>
public static class InstrumentSearchRanking
{
    /// <summary>The query IS this instrument — typed in full, or resolved through an alias.</summary>
    public const int ExactSymbol = 0;
    public const int ExactAfterExchange = 1;
    public const int SymbolPrefix = 2;
    public const int WholeBaseName = 3;
    public const int BaseNamePrefix = 4;
    public const int SymbolContains = 5;
    public const int DescriptionOnly = 6;

    /// <summary>
    /// The rank expression, built once and used both by the database query and
    /// by the tests. One definition rather than two that can drift: a ranking
    /// that behaves differently in tests than in production is worse than none.
    /// </summary>
    /// <summary>
    /// The instrument the query names outright, when the platform knows it
    /// under a different symbol.
    /// </summary>
    /// <remarks>
    /// "BANKNIFTY" is the name every strategy uses, but the index is listed as
    /// NSE:NIFTYBANK-INDEX — the words are the other way round, so no substring
    /// search can ever reach it. Passing the resolved spot symbol in means the
    /// thing being asked for comes first instead of never.
    /// </remarks>
    public static Expression<Func<Instrument, int>> RankBy(string query, string? aliasSymbol = null)
    {
        string q = (query ?? string.Empty).Trim().ToUpperInvariant();
        string alias = (aliasSymbol ?? string.Empty).Trim().ToUpperInvariant();

        // "NIFTY50" as a complete base name is "…:NIFTY50-" (NSE:NIFTY50-INDEX)
        // or the whole tail (NSE:NIFTY50). Both beat a longer name that merely
        // starts with it, such as NSE:NIFTY50DIVPOINT-INDEX.
        string afterExchange = ":" + q;
        string wholeBase = ":" + q + "-";

        return x =>
            (alias.Length > 0 && x.Symbol.ToUpper() == alias) ? ExactSymbol
            : x.Symbol.ToUpper() == q ? ExactSymbol
            : x.Symbol.ToUpper().EndsWith(afterExchange) ? ExactAfterExchange
            : x.Symbol.ToUpper().StartsWith(q) ? SymbolPrefix
            : x.Symbol.ToUpper().Contains(wholeBase) ? WholeBaseName
            : x.Symbol.ToUpper().Contains(afterExchange) ? BaseNamePrefix
            : x.Symbol.ToUpper().Contains(q) ? SymbolContains
            : DescriptionOnly;
    }

    /// <summary>
    /// What someone searching a symbol box is most likely reaching for.
    /// </summary>
    /// <remarks>
    /// Ordered by how tradable the row is from this platform, because a name
    /// match alone is not enough to be useful: "HDFC" matches a dozen bonds
    /// (HDFC BANK 7.65% 2034) and mutual-fund lines before HDFCBANK-EQ, and
    /// "TCS" matched enough bonds to push NSE:TCS-EQ past the fiftieth row and
    /// out of the results entirely.
    /// <para>
    /// Options come last for the opposite reason — there are 163,000 of them,
    /// and one underlying's chain would fill every result on its own.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <c>OptionType</c> is a non-nullable string that defaults to empty, and
    /// no row in the table is ever null — so testing it for null is always
    /// true and would put every bond and fund in the options bucket. Emptiness
    /// is the real test.
    /// </remarks>
    public static Expression<Func<Instrument, int>> KindRank()
        => x =>
            x.InstrumentType == "INDEX" ? 0
            : x.InstrumentType == "EQ" ? 1
            : x.InstrumentType == "FUT" ? 2
            : (x.InstrumentType != null
               && (x.InstrumentType.Contains("OPT") || x.InstrumentType == "CE" || x.InstrumentType == "PE"))
              || (x.OptionType != null && x.OptionType != "") ? 4
            // Bonds, mutual funds, SME and trade-to-trade series: real rows,
            // but never what a strategy is pointed at.
            : 3;
}
