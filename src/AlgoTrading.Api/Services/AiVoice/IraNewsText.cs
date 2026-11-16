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

        return Fit($"{head}, asar {f.Impact}/3{(from.Length > 0 ? $" ({from})" : string.Empty)}: {what} NIFTY pe asar: {tilt}{swing}.{nowLine}{Effects(f)}{why}{watch}");
    }

    /// <summary>" Stocks: ONGC neeche, INDIGO upar. Sector: aviation upar." Empty when the story names none.</summary>
    private static string Effects(MarketImpactFacts f)
    {
        static string Word(string effect) => effect switch { "up" => "upar", "down" => "neeche", _ => "dono taraf" };
        string stocks = string.Join(", ", (f.Stocks ?? []).Select(s => $"{s.Name} {Word(s.Effect)}"));
        string sectors = string.Join(", ", (f.Sectors ?? []).Select(s => $"{s.Name} {Word(s.Effect)}"));
        return (stocks.Length > 0 ? $" Stocks: {stocks}." : string.Empty) + (sectors.Length > 0 ? $" Sector: {sectors}." : string.Empty);
    }

    /// <summary>
    /// "Update: yeh khabar ab 5 jagah hai (CNBC, Reuters, Mint, ET, Al Jazeera): Iran ne peace talks ka ishaara diya. Jitni
    /// zyaada jagah, utni badi khabar." Once per story, when enough publishers carry it.
    /// </summary>
    public static string NewsTally(MarketImpactFacts story, IReadOnlyList<string> publishers)
    {
        string shown = string.Join(", ", publishers.Take(6)) + (publishers.Count > 6 ? $" aur {publishers.Count - 6}" : string.Empty);
        string what = Sentence(Cut(Flat(story.What.Length > 0 ? story.What : story.Headline), 180));
        return Fit($"Update: yeh khabar ab {publishers.Count} jagah hai ({shown}): {what} Jitni zyaada jagah, utni badi khabar.");
    }

    /// <summary>
    /// The mausam card: "Aaj ka mausam (Tue 17 Nov): TEZ. NIFTY ki range ka andaaza 1.42% (aam 1.10%), trend din ka chance
    /// 38% (aam 30%). Strategy: TrendRider default (filter D on). Events: 10:00 RBI policy. Raat ki badi khabrein: 2, sabse
    /// badi: …" What matters most first, so a long card loses the news, not the day's call.
    /// </summary>
    public static string Mausam(IraMausam m, DateOnly day)
    {
        string label = m.RangeMedian is decimal r && m.RangeBaseline is decimal b && b > 0
            ? (r / b >= 1.15m ? "TEZ" : r / b <= 0.85m ? "SHAANT" : "AAM")
            : "PATA NAHI (forecast nahi mila)";
        var parts = new List<string> { $"Aaj ka mausam ({day.ToString("ddd d MMM", CultureInfo.InvariantCulture)}): {label}." };
        if (m.RangeMedian is decimal rm)
        {
            string range = $"NIFTY ki range ka andaaza {rm.ToString("0.00", CultureInfo.InvariantCulture)}%"
                           + (m.RangeBaseline is decimal rb ? $" (aam {rb.ToString("0.00", CultureInfo.InvariantCulture)}%)" : string.Empty);
            string trend = m.TrendP is double tp
                ? $", trend din ka chance {tp * 100:0}%" + (m.TrendBaseline is double tb ? $" (aam {tb * 100:0}%)" : string.Empty)
                : string.Empty;
            parts.Add(range + trend + ".");
        }

        parts.Add("Strategy: TrendRider default (filter D on); 3 saal mein yahi tika, mausam se badalna test mein fail hua.");
        if (m.Events.Count > 0)
        {
            parts.Add("Events: " + string.Join("; ", m.Events.Take(3).Select(e => (e.Time.Length > 0 ? e.Time + " " : string.Empty) + Flat(e.Title)))
                      + ". Inke aas-paas nayi entry se bacho (sujhaav, test nahi).");
        }

        parts.Add(m.BigNews == 0
            ? "Raat mein koi badi khabar nahi."
            : $"Raat ki badi khabrein: {m.BigNews}" + (m.TopNews is { Length: > 0 } top ? $", sabse badi: {Sentence(Cut(Flat(top), 120))}" : "."));
        return Fit(string.Join(" ", parts));
    }

    /// <summary>"+0.62%", "-3.10%", "0.00%".</summary>
    private static string Percent(decimal value)
    {
        decimal r = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        return (r > 0 ? "+" : string.Empty) + r.ToString("0.00", CultureInfo.InvariantCulture) + "%";
    }
}
