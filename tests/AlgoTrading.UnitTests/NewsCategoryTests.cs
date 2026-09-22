using AlgoTrading.Contracts.MarketIntel;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The news section's category list, which the console builds its tabs from. A
/// duplicate key or a group nobody renders does not throw — it quietly costs a
/// tab, or worse, points two tabs at one set of headlines. Nothing here reaches
/// the network: it is the shape of the configuration being guarded, not the
/// publishers' uptime.
/// </summary>
public class NewsCategoryTests
{
    private static MarketIntelService Service()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase($"news-{Guid.NewGuid():N}").Options;

        // GetNewsCategories reads static configuration and touches none of
        // these; they are here because the constructor asks for them.
        return new MarketIntelService(
            httpClientFactory: null!,
            cache: new MemoryCache(new MemoryCacheOptions()),
            dbContext: new TradingDbContext(options),
            logger: NullLogger<MarketIntelService>.Instance);
    }

    [Fact]
    public void Every_category_key_is_unique()
    {
        var keys = Service().GetNewsCategories().Select(c => c.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Every_category_has_a_key_a_label_and_a_group_the_console_renders()
    {
        string[] rendered = [NewsCategoryGroups.Markets, NewsCategoryGroups.Sectors];

        foreach (var category in Service().GetNewsCategories())
        {
            Assert.False(string.IsNullOrWhiteSpace(category.Key));
            Assert.False(string.IsNullOrWhiteSpace(category.Label));
            Assert.Contains(category.Group, rendered);
        }
    }

    [Fact]
    public void The_sectors_the_console_promises_are_all_there()
    {
        // The set the page is written around. A sector may be added; one going
        // missing is a tab that silently disappears.
        string[] expected = ["banking", "pharma", "it", "auto", "energy", "fmcg", "metals", "realty"];

        var sectors = Service().GetNewsCategories()
            .Where(c => c.Group == NewsCategoryGroups.Sectors)
            .Select(c => c.Key)
            .ToList();

        foreach (var key in expected) Assert.Contains(key, sectors);
    }

    [Fact]
    public void The_default_tab_exists()
    {
        // The console opens on "india" and falls back to it from an unknown
        // ?c= in the URL.
        Assert.Contains(Service().GetNewsCategories(), c => c.Key == "india");
    }

    [Theory]
    [InlineData("pharma", "pharma")]
    [InlineData("PHARMA", "pharma")]
    [InlineData("Banking", "banking")]
    public async Task A_category_is_matched_whatever_its_casing(string asked, string expectedKey)
    {
        // The key travels in a URL, where casing is not in our hands, so the
        // wrong case must not read as "unknown category". The response also
        // comes back under the canonical key rather than the one asked with,
        // which is what the cache and the console both key on.
        var response = await Service().GetNewsAsync(asked, CancellationToken.None);
        Assert.Equal(expectedKey, response.Category);
    }

    [Fact]
    public async Task Every_feed_failing_empties_the_section_rather_than_breaking_it()
    {
        // This service has no HTTP client, so every fetch throws. A category
        // whose publishers are all unreachable must still answer — the console
        // says "no headlines right now", which is true, instead of showing the
        // trader an error for something outside their control.
        var response = await Service().GetNewsAsync("pharma", CancellationToken.None);

        Assert.Empty(response.Items);
        Assert.Equal("pharma", response.Category);
    }

    [Fact]
    public async Task An_unknown_category_says_so_and_lists_the_real_ones()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Service().GetNewsAsync("nosuchsector", CancellationToken.None));

        Assert.Contains("nosuchsector", error.Message);
        Assert.Contains("pharma", error.Message);
    }
}
