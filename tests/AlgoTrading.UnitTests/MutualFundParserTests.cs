using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Services.MutualFunds;

namespace AlgoTrading.UnitTests;

/// <summary>
/// AMFI's NAV files, read from trimmed copies of real downloads: the daily
/// <c>NAVAll.txt</c> of 1 Nov 2026 and two NAV history windows (Oct 2026 and
/// April 2006, the first month AMFI's history holds).
/// </summary>
public class MutualFundParserTests
{
    internal static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "AlgoTrading.UnitTests", "Fixtures", "MutualFunds")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "tests", "AlgoTrading.UnitTests", "Fixtures", "MutualFunds", name));
    }

    internal static List<AmfiNavRow> Daily(AmfiParseCounts? counts = null) =>
        AmfiNavParser.Parse(new StringReader(Fixture("NAVAll_20261101_sample.txt")), counts).ToList();

    private static Dictionary<int, AmfiNavRow> DailyByCode() => Daily().ToDictionary(r => r.SchemeCode);

    [Fact]
    public void Uti_nifty_50_direct_growth_is_direct_growth_at_its_published_nav()
    {
        var row = DailyByCode()[120716];

        Assert.Equal(MutualFundPlans.Direct, row.Plan);
        Assert.Equal(MutualFundOptions.Growth, row.Option);
        Assert.Equal(162.3751m, row.Nav);
        Assert.Equal(new DateOnly(2026, 10, 30), row.Date);
        Assert.Equal("UTI Nifty 50 Index Fund", row.SchemeName);
        Assert.Equal("UTI Mutual Fund", row.Amc);
        Assert.Equal("Open Ended Schemes(Other Scheme - Index Funds)", row.Category);
        Assert.Equal("INF789F01XA0", row.IsinGrowth);
        Assert.Null(row.IsinReinvest);
    }

    [Fact]
    public void Uti_nifty_50_regular_growth_is_regular_growth_at_its_published_nav()
    {
        var row = DailyByCode()[100822];

        Assert.Equal(MutualFundPlans.Regular, row.Plan);
        Assert.Equal(MutualFundOptions.Growth, row.Option);
        Assert.Equal(159.8799m, row.Nav);
        Assert.Equal(new DateOnly(2026, 10, 30), row.Date);
    }

    [Fact]
    public void Every_scheme_line_is_read_under_its_own_section_and_amc()
    {
        var counts = new AmfiParseCounts();
        var rows = Daily(counts);

        Assert.Equal(37, rows.Count);
        Assert.Equal(37, counts.Rows);
        Assert.Equal(0, counts.Unreadable);
        Assert.Equal(rows.Count, rows.Select(r => r.SchemeCode).Distinct().Count());

        var byCode = rows.ToDictionary(r => r.SchemeCode);
        // The section line carries a curly apostrophe; the AMC line its own parentheses.
        Assert.Equal("Open Ended Schemes(Children’s Fund - Childrens' Fund)", byCode[135762].Category);
        Assert.Equal("Axis Mutual Fund", byCode[135762].Amc);
        Assert.Equal("IL&FS Mutual Fund (IDF)", byCode[130565].Amc);
        Assert.Equal("Close Ended Schemes(Income)", byCode[130565].Category);
        Assert.Equal("Interval Fund Schemes(Income)", byCode[118804].Category);
        Assert.Equal("Open Ended Schemes(Exchange Traded Funds (ETFs) - Equity ETF)", byCode[106929].Category);
        // A new AMC inside the same section.
        Assert.Equal("Edelweiss Mutual Fund", byCode[111935].Amc);
        Assert.Equal("Open Ended Schemes(Equity Scheme - Large Cap Fund)", byCode[111935].Category);
    }

    [Theory]
    // AMFI's own Plan and Option columns, in their many spellings.
    [InlineData(135762, MutualFundPlans.Direct, MutualFundOptions.Growth)]      // "Growth Option"
    [InlineData(135765, MutualFundPlans.Direct, MutualFundOptions.Idcw)]        // "IDCW Option"
    [InlineData(119528, MutualFundPlans.Direct, MutualFundOptions.Growth)]      // "GROWTH"
    [InlineData(145896, MutualFundPlans.Regular, MutualFundOptions.Growth)]     // "Cumulative"
    [InlineData(124312, MutualFundPlans.Direct, MutualFundOptions.Idcw)]        // "Payout/Reinvestment"
    [InlineData(124681, MutualFundPlans.Regular, MutualFundOptions.Idcw)]       // "Dividend Option"
    [InlineData(151835, MutualFundPlans.Direct, MutualFundOptions.Idcw)]        // "Daily - IDCW"
    [InlineData(118807, MutualFundPlans.Direct, MutualFundOptions.Other)]       // "Bonus Option"
    [InlineData(139582, MutualFundPlans.Unknown, MutualFundOptions.Other)]      // "Unclaimed IDCW Less than 3 years"
    // "Dividend Yield" is the fund's name, not its option.
    [InlineData(119507, MutualFundPlans.Direct, MutualFundOptions.Growth)]
    [InlineData(119506, MutualFundPlans.Direct, MutualFundOptions.Idcw)]
    // Blank Plan and Option: the name, then the reinvestment ISIN, else unknown.
    [InlineData(115022, MutualFundPlans.Unknown, MutualFundOptions.Idcw)]       // "... Quarterly Dividend Payout"
    [InlineData(119527, MutualFundPlans.Unknown, MutualFundOptions.Idcw)]       // has a reinvestment ISIN
    [InlineData(111935, MutualFundPlans.Unknown, MutualFundOptions.Unknown)]    // growth ISIN only: growth or IDCW payout
    [InlineData(130003, MutualFundPlans.Unknown, MutualFundOptions.Unknown)]    // "ICICI Prudential Growth Fund - Series 1"
    [InlineData(106929, MutualFundPlans.Unknown, MutualFundOptions.Unknown)]    // an ETF
    [InlineData(147956, MutualFundPlans.Direct, MutualFundOptions.Unknown)]     // a segregated portfolio, option blank
    public void Plan_and_option_are_cleaned(int code, string plan, string option)
    {
        var row = DailyByCode()[code];

        Assert.Equal(plan, row.Plan);
        Assert.Equal(option, row.Option);
    }

    [Fact]
    public void Amfi_option_words_are_kept_as_the_label()
    {
        var byCode = DailyByCode();

        Assert.Equal("Daily - IDCW", byCode[151835].OptionLabel);
        Assert.Null(byCode[119527].OptionLabel);
    }

    [Fact]
    public void Only_well_formed_isins_are_kept()
    {
        var byCode = DailyByCode();

        Assert.Null(byCode[130565].IsinReinvest);             // "Redeemed"
        Assert.Equal("INF613Q01025", byCode[130565].IsinGrowth);
        Assert.Null(byCode[152713].IsinReinvest);             // "HDFCNIVODG"
        Assert.Null(byCode[147442].IsinGrowth);               // "-"
        Assert.Null(byCode[151835].IsinGrowth);
        Assert.Equal("INF0QA701029", byCode[151835].IsinReinvest);
    }

    [Fact]
    public void A_zero_nav_is_not_a_nav_and_is_counted()
    {
        var counts = new AmfiParseCounts();
        var row = Daily(counts).Single(r => r.SchemeCode == 147956);

        Assert.Null(row.Nav);
        Assert.Equal(new DateOnly(2020, 7, 20), row.Date);
        Assert.Equal(1, counts.RowsWithoutNav);
    }

    [Theory]
    [InlineData("N.A.")]
    [InlineData("")]
    [InlineData("NA")]
    [InlineData("-")]
    [InlineData("0")]
    [InlineData("-1.5")]
    public void Na_text_zero_and_negative_navs_are_missing(string text) => Assert.Null(AmfiNavParser.ParseNav(text));

    [Fact]
    public void Navs_keep_every_decimal_amfi_wrote()
    {
        Assert.Equal(1680494.0463m, AmfiNavParser.ParseNav("1680494.0463"));
        Assert.Equal(10.12345678m, AmfiNavParser.ParseNav("10.12345678"));
        Assert.Equal(1680494.0463m, DailyByCode()[130565].Nav);
    }

    [Fact]
    public void A_line_with_na_for_a_nav_is_kept_for_the_master_but_has_no_nav()
    {
        const string file = "Scheme Code;ISIN Div Payout/ ISIN Growth;ISIN Div Reinvestment;Scheme Name;Plan;Option;Net Asset Value;Date\r\n"
                            + "\r\nOpen Ended Schemes(Debt Scheme - Liquid Fund)\r\n\r\nSome Mutual Fund\r\n\r\n"
                            + "100001;INF000000001;-;Some Liquid Fund;Direct Plan;Growth;N.A.;30-Oct-2026\r\n";
        var counts = new AmfiParseCounts();

        var row = Assert.Single(AmfiNavParser.Parse(new StringReader(file), counts));

        Assert.Null(row.Nav);
        Assert.Equal(1, counts.RowsWithoutNav);
    }

    [Fact]
    public void Weekend_navs_of_liquid_funds_keep_their_own_date()
    {
        var byCode = DailyByCode();

        Assert.Equal(new DateOnly(2026, 11, 1), byCode[151833].Date);
        Assert.Equal(1249.5933m, byCode[151833].Nav);
    }

    [Fact]
    public void The_history_download_is_read_by_its_own_header()
    {
        var rows = AmfiNavParser.Parse(new StringReader(Fixture("NAVHistory_tp1_20261029_20261030_sample.txt"))).ToList();

        var uti = rows.Where(r => r.SchemeCode == 120716).OrderBy(r => r.Date).ToList();
        Assert.Equal(2, uti.Count);
        Assert.Equal(new DateOnly(2026, 10, 29), uti[0].Date);
        Assert.Equal(161.5569m, uti[0].Nav);
        Assert.Equal(162.3751m, uti[1].Nav);
        Assert.Equal(MutualFundPlans.Direct, uti[1].Plan);
        Assert.Equal(MutualFundOptions.Growth, uti[1].Option);
        Assert.Equal("INF789F01XA0", uti[1].IsinGrowth);

        // "Open Ended Schemes ( Equity Scheme - Multi Cap Fund )" is written as the daily file writes it.
        Assert.Equal("Open Ended Schemes(Equity Scheme - Multi Cap Fund)", rows.First(r => r.SchemeCode == 148921).Category);
    }

    [Fact]
    public void The_history_names_fill_the_plan_the_daily_file_leaves_blank()
    {
        var byCode = AmfiNavParser.Parse(new StringReader(Fixture("NAVHistory_tp1_20261029_20261030_sample.txt")))
            .GroupBy(r => r.SchemeCode).ToDictionary(g => g.Key, g => g.Last());

        // Blank Plan and Option in both files; the history's NAV name says what they are.
        Assert.Equal((MutualFundPlans.Direct, MutualFundOptions.Idcw), (byCode[119527].Plan, byCode[119527].Option));      // "-DIRECT - IDCW"
        Assert.Equal((MutualFundPlans.Direct, MutualFundOptions.Growth), (byCode[145834].Plan, byCode[145834].Option));    // "- Direct Growth"
        Assert.Equal((MutualFundPlans.Unknown, MutualFundOptions.Growth), (byCode[111935].Plan, byCode[111935].Option));   // "-Plan B - Growth option"
    }

    [Fact]
    public void The_first_month_of_amfi_history_has_no_isins_and_no_plan_columns()
    {
        var rows = AmfiNavParser.Parse(new StringReader(Fixture("NAVHistory_tp1_20060401_20060404_sample.txt"))).ToList();
        var byCode = rows.GroupBy(r => r.SchemeCode).ToDictionary(g => g.Key, g => g.OrderBy(r => r.Date).ToList());

        Assert.Equal(7, rows.Count);
        Assert.All(rows, r => Assert.Null(r.IsinGrowth));
        Assert.Equal("ABN AMRO Monthly Income Plan-Regular Plan-Growth Option", byCode[102652][0].SchemeName);   // two spaces in AMFI's name
        Assert.Equal("ABN AMRO Mutual Fund", byCode[102652][0].Amc);
        Assert.Equal((MutualFundPlans.Regular, MutualFundOptions.Growth), (byCode[102652][0].Plan, byCode[102652][0].Option));
        Assert.Equal((MutualFundPlans.Regular, MutualFundOptions.Idcw), (byCode[102653][0].Plan, byCode[102653][0].Option));
        // "Institutional Plan" is from before direct plans: neither.
        Assert.Equal((MutualFundPlans.Unknown, MutualFundOptions.Idcw), (byCode[102645][0].Plan, byCode[102645][0].Option));
        Assert.Equal(new DateOnly(2006, 4, 2), byCode[102645][0].Date);   // a Sunday NAV
        Assert.Equal(10m, byCode[102645][0].Nav);
    }

    [Fact]
    public void Amfis_download_form_means_no_navs_for_the_range()
    {
        // What AMFI sends, with HTTP 200, for a range before April 2006 or a week with no interval fund.
        const string form = "\r\n\r\n<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Frameset//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-frameset.dtd\">\r\n<html>\r\n<head><title>\r\n\tView/Download NAV History\r\n</title>";

        Assert.Throws<AmfiNoDataException>(() => AmfiNavParser.Parse(new StringReader(form)).ToList());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Something else;entirely\r\n1;2;3")]
    [InlineData("Scheme Code;Scheme Name;Plan\r\n1;A;Direct")]
    public void Anything_else_is_not_a_nav_file(string text) =>
        Assert.Throws<FormatException>(() => AmfiNavParser.Parse(new StringReader(text)).ToList());

    [Fact]
    public void A_line_too_short_for_the_header_is_counted_not_read()
    {
        const string file = "Scheme Code;ISIN Div Payout/ ISIN Growth;ISIN Div Reinvestment;Scheme Name;Plan;Option;Net Asset Value;Date\r\n"
                            + "100001;INF000000001;-;Cut short\r\n"
                            + "100002;INF000000002;-;Whole;Direct Plan;Growth;10.5;30-Oct-2026\r\n";
        var counts = new AmfiParseCounts();

        var row = Assert.Single(AmfiNavParser.Parse(new StringReader(file), counts));

        Assert.Equal(100002, row.SchemeCode);
        Assert.Equal(1, counts.Unreadable);
    }
}
