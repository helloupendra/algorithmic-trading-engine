using System.Net;
using AlgoTrading.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.UnitTests;

/// <summary>
/// What a search engine sees at an address the console does not own (Search Console, 19 Oct 2026): every unknown path
/// answered the landing page with a 200 ("soft 404"), and a missing docs page answered 401.
/// </summary>
public sealed class ConsoleSiteTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "console-site-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "architecture"));
        await File.WriteAllTextAsync(Path.Combine(_root, "index.html"), "<!doctype html><title>console</title>");
        await File.WriteAllTextAsync(Path.Combine(_root, "ira.html"), "<!doctype html><title>Ira</title>");
        await File.WriteAllTextAsync(Path.Combine(_root, "docs", "index.html"), "<!doctype html><title>docs</title>");
        await File.WriteAllTextAsync(Path.Combine(_root, "docs", "architecture", "index.html"), "<!doctype html><title>architecture</title>");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test", ContentRootPath = _root, WebRootPath = _root });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.UseDefaultFiles();
        _app.UseStaticFiles();
        _app.UseRouting();
        _app.MapConsoleFallbacks();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/desk")]
    [InlineData("/portfolio")]
    [InlineData("/ai/assistant")]
    [InlineData("/today")]
    [InlineData("/admin/users")]
    public async Task The_landing_page_and_every_console_route_are_the_app_with_a_200(string path)
    {
        using var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("<!doctype html><title>console</title>", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/this-page-does-not-exist")]
    [InlineData("/broker")]
    [InlineData("/desktop")]
    [InlineData("/old/strategies/page")]
    public async Task An_address_the_console_does_not_own_is_a_404_the_app_can_still_render_and_not_indexed(string path)
    {
        using var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("noindex, nofollow", string.Join(',', response.Headers.GetValues("X-Robots-Tag")));
        Assert.Equal("<!doctype html><title>console</title>", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_missing_docs_page_is_a_404_page_not_a_401_and_real_docs_pages_still_load()
    {
        using var missing = await _client.GetAsync("/docs/no-such-page/");
        using var real = await _client.GetAsync("/docs/architecture/");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("Page not found", await missing.Content.ReadAsStringAsync());
        Assert.Equal("noindex, nofollow", string.Join(',', missing.Headers.GetValues("X-Robots-Tag")));
        Assert.Equal(HttpStatusCode.OK, real.StatusCode);
        Assert.Equal("<!doctype html><title>architecture</title>", await real.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_api_and_ira_keep_their_own_answers()
    {
        using var api = await _client.GetAsync("/api/nothing");
        using var ira = await _client.GetAsync("/ira/settings");

        Assert.Equal(HttpStatusCode.NotFound, api.StatusCode);
        Assert.Empty(await api.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, ira.StatusCode);
        Assert.Equal("<!doctype html><title>Ira</title>", await ira.Content.ReadAsStringAsync());
    }
}
