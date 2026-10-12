namespace AlgoTrading.Api.Services;

/// <summary>
/// The console's pages from wwwroot: the React app's index.html for any route it owns, and the Ira phone app's own
/// shell (ira.html) for /ira.
/// </summary>
/// <remarks>
/// <para>
/// Real files (bundles, icons, <c>ira.webmanifest</c>, <c>ira-sw.js</c>) are served before any of this by the static
/// file middleware, with their own types (.webmanifest is <c>application/manifest+json</c>). The fallbacks below only
/// answer what no file matched.
/// </para>
/// <para>
/// API and hub paths are excluded on purpose: an unknown /api route must answer 404, never a cacheable HTML document
/// that a client then mistakes for JSON. So are a missing web manifest and the service worker's script: a browser that
/// asks for them must get a 404, not the console's HTML read as a manifest or a script. The documentation site under
/// /docs is static HTML of its own.
/// </para>
/// <para>
/// /ira gets ira.html: index.html with the phone app's manifest, icon and name in its head (web/vite.config.ts writes
/// it), so Android installs "Ira" and an iPhone's Add to Home Screen names it "Ira" without waiting for any script. A
/// wwwroot built before it existed falls back to index.html, where the page still works.
/// </para>
/// </remarks>
public static class ConsoleSite
{
    /// <summary>The routes the console's index.html answers: everything but the API, the hubs, Swagger, the docs, /ira and a missing manifest or worker.</summary>
    public const string FallbackPattern =
        @"{*path:regex(^(?!api(/|$)|hubs(/|$)|swagger(/|$)|docs(/|$)|ira(/|$)|[^/]*\.webmanifest$|ira-sw\.js$).*$)}";

    /// <summary>/ira and anything under it: the phone app's shell.</summary>
    public const string IraPattern = "ira/{*path}";

    public const string IraShell = "ira.html";

    /// <summary>Maps the two fallbacks; both are open to anyone, as a page's HTML is (the data behind it is not).</summary>
    public static void MapConsoleFallbacks(this WebApplication app)
    {
        app.MapFallbackToFile(FallbackPattern, "index.html").AllowAnonymous();
        app.MapFallback(IraPattern, ServeIraShellAsync).AllowAnonymous();
    }

    private static async Task ServeIraShellAsync(HttpContext context)
    {
        var files = context.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider;
        var file = files.GetFileInfo(IraShell);
        if (!file.Exists) file = files.GetFileInfo("index.html");
        if (!file.Exists)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        // The shell names the bundle of the day: a phone must ask again rather than launch yesterday's.
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.SendFileAsync(file, context.RequestAborted);
    }
}
