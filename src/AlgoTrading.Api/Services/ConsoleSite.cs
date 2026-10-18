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
    /// <summary>
    /// The console's own top-level routes (web/src/App.tsx), and the old /admin and /trader that redirect inside it.
    /// Only these get the console's index.html with a 200; /ira has its own shell.
    /// </summary>
    public static readonly string[] Routes =
        ["account", "admin", "ai", "data", "desk", "forbidden", "invite", "login", "markets", "notebook", "portfolio",
         "research", "system", "today", "trade", "trader"];

    private static readonly string RouteAlternatives = string.Join('|', Routes);

    /// <summary>A console route and anything under it: the React app's index.html.</summary>
    public static readonly string FallbackPattern = $"{{*path:regex(^({RouteAlternatives})(/.*)?$)}}";

    /// <summary>
    /// Any other address outside the API, the hubs, Swagger, the docs, /ira and a missing manifest or worker: the
    /// app's index.html (its own "not found" page) with a 404 and noindex. Until 19 Oct 2026 every such address
    /// answered the landing page with a 200, which Google counted as duplicates of / ("soft 404").
    /// </summary>
    public static readonly string NotFoundPattern =
        $@"{{*path:regex(^(?!api(/|$)|hubs(/|$)|swagger(/|$)|docs(/|$)|ira(/|$)|[^/]*\.webmanifest$|ira-sw\.js$|({RouteAlternatives})(/|$)).+$)}}";

    /// <summary>
    /// A missing page under /docs: a 404 page of its own. Before 19 Oct 2026 it fell through to the API's sign-in
    /// policy and answered 401, which Google reports as "blocked due to unauthorized request".
    /// </summary>
    public const string DocsPattern = "docs/{*path}";

    public const string NoIndex = "noindex, nofollow";

    /// <summary>/ira and anything under it: the phone app's shell.</summary>
    public const string IraPattern = "ira/{*path}";

    public const string IraShell = "ira.html";

    /// <summary>Maps the two fallbacks; both are open to anyone, as a page's HTML is (the data behind it is not).</summary>
    public static void MapConsoleFallbacks(this WebApplication app)
    {
        // The desk, the pager and Sentinel ask GET /health whether the API is up. It had no endpoint of its own and
        // passed only because the fallback answered every unknown path with a 200; with unknown paths now a 404 it
        // must be a real endpoint (19 Oct 2026: the first deploy without it read as "API restart FAILED").
        app.MapGet("/health", () => Results.Text("ok")).AllowAnonymous().ExcludeFromDescription();
        app.MapFallbackToFile(FallbackPattern, "index.html").AllowAnonymous();
        app.MapFallback(IraPattern, ServeIraShellAsync).AllowAnonymous();
        app.MapFallback(NotFoundPattern, ServeAppNotFoundAsync).AllowAnonymous();
        app.MapFallback(DocsPattern, ServeDocsNotFoundAsync).AllowAnonymous();
    }

    private static async Task ServeAppNotFoundAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.Headers["X-Robots-Tag"] = NoIndex;
        var file = context.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider.GetFileInfo("index.html");
        if (!file.Exists) return;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.SendFileAsync(file, context.RequestAborted);
    }

    private static async Task ServeDocsNotFoundAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.Headers["X-Robots-Tag"] = NoIndex;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(DocsNotFoundHtml, context.RequestAborted);
    }

    private const string DocsNotFoundHtml =
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"robots\" content=\"noindex\">"
        + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>Page not found · OpenFNO Docs</title>"
        + "<link rel=\"stylesheet\" href=\"/docs/assets/docs.css\"></head><body><main style=\"max-width:40rem;margin:4rem auto;padding:0 1rem\">"
        + "<h1>Page not found</h1><p>There is no docs page at this address.</p><p><a href=\"/docs/\">Go to the documentation</a></p>"
        + "</main></body></html>";

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
