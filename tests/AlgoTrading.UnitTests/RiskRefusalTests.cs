using System.Net;
using System.Reflection;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Exceptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// An order the risk guard refuses is a 409 and a warning, not an API error.
/// </summary>
/// <remarks>
/// On 30 Sep the Sentinel opened a medium incident "API error:
/// RiskViolationException: MAX DAILY LOSS EXCEEDED …". The refusal reached
/// ASP.NET's exception handler, which logs every exception it handles as
/// <c>fail:</c> before the API's own handler turned it into the 409. The
/// refusal is now answered inside MVC (<see cref="RiskRefusalFilter"/>). The
/// host here is the API's pipeline in miniature: the filter, and the same
/// exception handler Program.cs installs.
/// </remarks>
public class RiskRefusalTests : IAsyncLifetime
{
    private const string Reason = "MAX DAILY LOSS EXCEEDED: Current PnL -5210.50 is below the limit of -5000. Exits remain allowed.";

    private readonly CapturingLoggerProvider _log = new();
    private WebApplication? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(_log);
        builder.Services.AddControllers(options => options.Filters.Add<RiskRefusalFilter>())
            .AddApplicationPart(typeof(RiskRefusalTests).Assembly)
            .ConfigureApplicationPartManager(parts =>
            {
                foreach (var provider in parts.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                    parts.FeatureProviders.Remove(provider);
                parts.FeatureProviders.Add(new OnlySignals());
            });

        _app = builder.Build();
        _app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
        {
            var error = context.Features.Get<IExceptionHandlerPathFeature>()!.Error;
            context.Response.StatusCode = UnhandledExceptionStatus.For(error);
            await context.Response.WriteAsJsonAsync(new { error = error.Message });
        }));
        _app.MapControllers();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
    }

    [Fact]
    public async Task A_refused_signal_is_a_409_with_its_reason_and_a_warning_not_an_error()
    {
        var response = await _client!.PostAsync("/test/signals/refused", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("MAX DAILY LOSS EXCEEDED", await response.Content.ReadAsStringAsync());

        var refusal = Assert.Single(_log.Entries, e => e.Category == typeof(RiskRefusalFilter).FullName);
        Assert.Equal(LogLevel.Warning, refusal.Level);
        Assert.Equal($"Signal refused by the risk guard (409) on POST /test/signals/refused: {Reason}", refusal.Message);
        Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Any_other_exception_is_still_an_error()
    {
        // The control: the same host logs a real failure at Error, so the
        // absence above is the filter's doing and not a deaf logger.
        var response = await _client!.PostAsync("/test/signals/broken", null);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }

    [Fact]
    public void The_API_installs_the_filter_for_every_controller()
    {
        var program = File.ReadAllText(Path.Combine(RepoDirectory(), "src", "AlgoTrading.Api", "Program.cs"));
        Assert.Contains("AddControllers(options => options.Filters.Add<AlgoTrading.Api.Services.RiskRefusalFilter>())", program);
    }

    private static string RepoDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "AlgoTrading.Api")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [ApiController]
    [Route("test/signals")]
    public sealed class SignalsController : ControllerBase
    {
        [HttpPost("refused")]
        public Task<IActionResult> Refused() => throw new RiskViolationException(Reason);

        [HttpPost("broken")]
        public Task<IActionResult> Broken() => throw new InvalidOperationException("a real failure");
    }

    private sealed class OnlySignals : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) => typeInfo.AsType() == typeof(SignalsController);
    }
}
