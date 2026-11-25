using System.Text.Json;
using AlgoTrading.Api.Services;
using AlgoTrading.Contracts.Strategies;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The live view's Parameters: the strategy settings a run was started with, so the run page reads the run's
/// own day_filters or timeframe rather than the catalog's default.
/// </summary>
public class LiveRunSettingsTests
{
    [Fact]
    public void A_runs_settings_are_the_defaults_with_the_starts_own_and_without_the_run_level_keys()
    {
        var overrides = new Dictionary<string, JsonElement>
        {
            ["day_filters"] = JsonSerializer.SerializeToElement("off"),
        };
        string stored = LiveRunParameters.Merge("""{"levels":"prev_day","day_filters":true,"adr_days":10}""",
            overrides, 2, RiskRulesDto.FromLegacy(4000m, null), "NIFTY");

        var settings = LiveRunParameters.StrategySettings(stored);

        Assert.NotNull(settings);
        Assert.Equal("off", settings["day_filters"].GetString());
        Assert.Equal("prev_day", settings["levels"].GetString());
        Assert.Equal(10, settings["adr_days"].GetInt32());
        foreach (var key in new[] { "risk", "lots", "underlying", "stop_loss", "target" })
        {
            Assert.False(settings.ContainsKey(key), key);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void Nothing_readable_is_null(string? json)
        => Assert.Null(LiveRunParameters.StrategySettings(json));
}
