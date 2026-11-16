using System.Globalization;
using AlgoTrading.Api.Services.AiAgents;

namespace AlgoTrading.Api.Services.AiVoice;

/// <summary>
/// A big-news alert, in Hinglish (owner, 16 Nov 2026: "alert Hinglish mein, text aur awaaz, Telegram pe"). Code puts
/// it together from the Market Impact record, whose three lines were checked against the headline, and from the Market
/// pulse; no model writes the frame or a number in it.
/// </summary>
public static partial class IraAlertText
{
    /// <summary>
    /// "Bahut badi khabar, asar 3/3 (Reuters, 10:05 IST): Iran ne peace talks ka ishaara diya. NIFTY pe asar: upar,
    /// din zyaada hil sakta hai. Abhi aaj: NIFTY +0.62%, crude -3.10%. Kyon: … Dekhna: …" The parts that matter most
    /// come first, so a long message loses the last line, not the news.
    /// </summary>
    public static string News(MarketImpactFacts f, IraMarketNow market)
    {
        string head = f.Impact >= 3 ? "Bahut badi khabar" : "Badi khabar";
        string when = f.FirstSeenUtc is DateTime seen ? $"{Clock(seen)} IST" : string.Empty;
        string from = string.Join(", ", new[] { Flat(f.Source), when }.Where(s => s.Length > 0));
        string what = Sentence(Cut(Flat(f.What.Length > 0 ? f.What : f.Headline), 200));

        string tilt = f.Nifty switch
        {
            "up" => "upar",
            "down" => "neeche",
            "mixed" => "dono taraf",
            _ => "disha saaf nahi",
        };
        string swing = f.Volatility switch
        {
            "higher" => ", din zyaada hil sakta hai",
            "lower" => ", din shaant reh sakta hai",
            _ => string.Empty,
        };

        var now = new List<string>();
        if (market.NiftyPercent is decimal n) now.Add($"NIFTY {Percent(n)}");
        if (market.CrudePercent is decimal c) now.Add($"crude {Percent(c)}");
        string nowLine = now.Count > 0 ? $" Abhi aaj: {string.Join(", ", now)}." : string.Empty;

        string why = f.Why.Length > 0 ? $" Kyon: {Sentence(Flat(f.Why))}" : string.Empty;
        string watch = f.Watch.Length > 0 ? $" Dekhna: {Sentence(Flat(f.Watch))}" : string.Empty;

        return Fit($"{head}, asar {f.Impact}/3{(from.Length > 0 ? $" ({from})" : string.Empty)}: {what} NIFTY pe asar: {tilt}{swing}.{nowLine}{why}{watch}");
    }

    /// <summary>"+0.62%", "-3.10%", "0.00%".</summary>
    private static string Percent(decimal value)
    {
        decimal r = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        return (r > 0 ? "+" : string.Empty) + r.ToString("0.00", CultureInfo.InvariantCulture) + "%";
    }
}
