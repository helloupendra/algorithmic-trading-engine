using AlgoTrading.Infrastructure.Services;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The instrument importer read a weekly option's strike as "the last four to six digits", so the
/// expiry day's last digit joined the strike: NIFTY2670724350CE (7 Jul 2026, 24350) became 724350.
/// </summary>
public class InstrumentImportStrikeTests
{
    [Theory]
    [InlineData("NSE:NIFTY2670724350CE", 24350, "CE")]
    [InlineData("NSE:NIFTY2662324050PE", 24050, "PE")]
    [InlineData("NSE:NIFTY2661623350CE", 23350, "CE")]
    [InlineData("NSE:NIFTY2660223900PE", 23900, "PE")]
    [InlineData("NSE:NIFTY26O0624350CE", 24350, "CE")]
    [InlineData("BSE:SENSEX2670783500PE", 83500, "PE")]
    [InlineData("NSE:BANKNIFTY26SEP57600CE", 57600, "CE")]
    [InlineData("NSE:NIFTY26JUN23900PE", 23900, "PE")]
    public void Weekly_and_monthly_strikes_come_from_the_symbol_grammar(string symbol, decimal strike, string type)
    {
        var parsed = LocalCsvInstrumentImportService.ParseDerivativeFields(symbol, string.Empty);
        Assert.Equal(strike, parsed.strike);
        Assert.Equal(type, parsed.optionType);
    }

    [Fact]
    public void A_description_still_works_when_the_symbol_is_not_in_the_grammar()
    {
        var parsed = LocalCsvInstrumentImportService.ParseDerivativeFields("NSE:ODDNAME-24350CE", "ODD 26 Jul 07 24350 CE");
        Assert.Equal(24350m, parsed.strike);
    }
}

public class ContractLabelStrikeTests
{
    [Fact]
    public void The_label_reads_the_strike_from_the_symbol_when_the_master_row_is_wrong()
    {
        var inst = new AlgoTrading.Api.Services.PositionViewBuilder.InstrumentLite("NSE:NIFTY2670724350CE", "NIFTY", 724350m, "CE", new DateOnly(2026, 7, 7));
        var c = AlgoTrading.Api.Services.PositionViewBuilder.BuildContract("NSE:NIFTY2670724350CE", inst);
        Assert.Equal(24350m, c.Strike);
        Assert.StartsWith("NIFTY 24350 CE", c.Label);
    }
}
