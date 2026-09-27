using System.Net;
using System.Net.Http.Json;
using AlgoTrading.Api.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The one way a live runner starts: POST /api/Strategy/{id}/start.
/// </summary>
/// <remarks>
/// There used to be a second, POST /api/Strategy/{id}/deploy, that put a runner
/// behind an existing run row. It checked for a duplicate in the row's owner's
/// account but launched in the caller's, never checked that the caller owned
/// the row or that the row was still open, and set it back to Running — so
/// anyone with the Strategies module could put a runner behind another trader's
/// run, stopped ones included. Nothing called it any more; it is gone.
/// </remarks>
public class StrategyStartTests
{
    [Fact]
    public async Task Post_deploy_answers_404()
    {
        // A real host routing the API's own controllers: the question is what
        // the router answers, which reflection over attributes would only guess.
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(StrategyController).Assembly);

        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();

        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        var deploy = await http.PostAsync("/api/Strategy/650824872/deploy", JsonContent.Create(new { runId = 46 }));
        Assert.Equal(HttpStatusCode.NotFound, deploy.StatusCode);

        // The same host does route the start endpoint (it fails there for want
        // of services, not of a route), so the 404 above is the route being
        // gone rather than a host that routes nothing.
        var start = await http.PostAsync("/api/Strategy/650824872/start", JsonContent.Create(new { underlying = "NIFTY" }));
        Assert.NotEqual(HttpStatusCode.NotFound, start.StatusCode);

        await app.StopAsync();
    }
}
