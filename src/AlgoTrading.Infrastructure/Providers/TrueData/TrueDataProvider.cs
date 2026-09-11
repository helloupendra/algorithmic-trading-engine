using AlgoTrading.Application.Providers;

namespace AlgoTrading.Infrastructure.Providers.TrueData;

/// <summary>
/// The TrueData connector's identity card: its key, and an honest statement of
/// what the feed actually delivers.
/// </summary>
/// <remarks>
/// TrueData is a data vendor only — it sells prices and takes no orders — so it
/// registers in the market-data registry and nowhere near the broker one.
///
/// The reason it is worth having alongside FYERS is in two of the flags below.
/// Its session is obtained with a username and password over REST, so there is
/// no daily interactive sign-in: the failure that cost this desk a morning on
/// 2026-09-10 and again on 2026-09-11 cannot happen on this feed. And its tick
/// stream carries open interest, which the FYERS subscription does not, so the
/// rules that have never been able to fire for want of OI finally have a source.
/// </remarks>
public static class TrueDataProvider
{
    /// <summary>
    /// Stable key. It is written into the SourceKey column of every price row
    /// this connector produces, so it must never change.
    /// </summary>
    public const string Key = "truedata";

    public static readonly ProviderDescriptor Descriptor = new(
        Key,
        "TrueData",
        ProviderKind.Data,
        // Username and password exchanged for a bearer token over REST. Not
        // OAuthDaily: nothing here needs a human in front of a browser, which
        // is the whole point of adding it.
        ProviderAuthKind.ApiKey,
        new ProviderCapabilities
        {
            History = true,
            LiveTicks = true,
            Quotes = true,
            OptionChain = true,

            // A data vendor. There is no order path and there will not be one.
            Orders = false,

            // Best bid/ask and their sizes are in both the touchline and the
            // tick stream (documentation v2.6, "trade" and "bidask" headers).
            Depth = true,

            // The field this connector exists for. Claimed because the "trade"
            // message carries OI and previous OI close for every derivative;
            // an index tick has no OI and reports zero, which is correct.
            OpenInterest = true,

            // Greeks are a separate, separately-charged TrueData product. Not
            // in this subscription, so not claimed — the platform prices its
            // own greeks from the chain instead.
            Greeks = false,

            // TrueData has its own grammar: "NIFTY 50" for the index,
            // "NIFTY26091518200CE" for a weekly option (two-digit month, where
            // the canonical form uses one character), "CRUDEOIL-I" for the near
            // month future. Every symbol is translated at this adapter's
            // boundary, and the ones that are not purely lexical come from
            // instrument_vendor_symbols.
            UsesCanonicalSymbols = false,

            // The trial allows 50 symbols on one connection; a paid plan raises
            // it. Kept honest rather than optimistic: the streamer refuses to
            // subscribe past this and says why, instead of being cut off.
            MaxStreamSymbols = 50,

            // The trial serves 15 days of bar history and 3 years of EOD. There
            // is no documented per-call day cap, so this is the window that
            // actually holds data rather than a request limit.
            HistoryMaxDaysPerCall = 15,

            Resolutions = new[] { "1", "5", "15", "D" },
            Segments = new[] { "CM", "FO", "CD", "MCX" },
        })
    {
        // Behind FYERS until it has been run in shadow for a while and compared.
        // Ahead of files and replay, which cannot produce a live price at all.
        FallbackRank = 10,
    };
}
