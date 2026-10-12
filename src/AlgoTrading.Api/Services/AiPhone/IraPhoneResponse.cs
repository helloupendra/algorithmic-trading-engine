using System.Text;
using System.Text.Json;
using AlgoTrading.Infrastructure.Ai;
using Microsoft.AspNetCore.Http.Features;

namespace AlgoTrading.Api.Services.AiPhone;

/// <summary>
/// The phone's answer as one JSON object, kept alive while the model works: once the call has started, the response is
/// opened (200, <c>application/json</c>) and a single space goes out every <see cref="KeepAliveEvery"/> until the JSON
/// follows. Whitespace before a JSON value is valid JSON, so the Shortcut reads the object as usual.
/// </summary>
/// <remarks>
/// <para>
/// Why: an answer with tools can take a minute or more. Cloudflare cuts a response that sends nothing for 100 seconds,
/// and the phone's own request gives up after a quiet minute; the console streams events for the same reason
/// (<see cref="AiEventStream"/>). A refusal (the agent off, the rate limit, no key) comes before the call starts, so it
/// is still answered with its own status, as plain JSON.
/// </para>
/// <para>
/// Once opened, the status is 200 whatever follows: a call that then fails says so in the object (<c>error</c>, and a
/// <c>spoken</c> line for Siri).
/// </para>
/// </remarks>
public sealed class IraPhoneResponse(HttpResponse response, CancellationToken aborted) : IAiStreamSink, IAsyncDisposable
{
    public static TimeSpan KeepAliveEvery { get; set; } = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly byte[] Space = " "u8.ToArray();

    private readonly SemaphoreSlim _write = new(1, 1);
    private CancellationTokenSource? _stop;
    private Task? _keepAlive;

    /// <summary>The response is open: the answer must be written into it, not returned as a result.</summary>
    public bool Started { get; private set; }

    public async ValueTask StartAsync(long callId, IReadOnlyList<string> chain)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/json; charset=utf-8";
        response.Headers.CacheControl = "no-cache, no-transform";
        response.Headers["X-Accel-Buffering"] = "no";
        response.Headers["X-Ai-Call-Id"] = callId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await response.StartAsync(aborted);
        Started = true;

        _stop = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        _keepAlive = KeepAliveAsync(_stop.Token);
    }

    public ValueTask AttemptAsync(string model, int number, int of, int round) => ValueTask.CompletedTask;

    public ValueTask ReasoningAsync(string text) => ValueTask.CompletedTask;

    public ValueTask DeltaAsync(string text) => ValueTask.CompletedTask;

    public ValueTask FallbackAsync(string model, string reason, string? next) => ValueTask.CompletedTask;

    public ValueTask ToolAsync(AiToolStep step) => ValueTask.CompletedTask;

    /// <summary>Writes the answer's object after whatever spaces went before it, and ends the keep-alive.</summary>
    public async Task FinishAsync(object body)
    {
        await StopKeepAliveAsync();
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, Json));
        await _write.WaitAsync(aborted);
        try
        {
            await response.Body.WriteAsync(bytes, aborted);
            await response.Body.FlushAsync(aborted);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task KeepAliveAsync(CancellationToken stop)
    {
        try
        {
            using var timer = new PeriodicTimer(KeepAliveEvery);
            while (await timer.WaitForNextTickAsync(stop))
            {
                await _write.WaitAsync(stop);
                try
                {
                    await response.Body.WriteAsync(Space, stop);
                    await response.Body.FlushAsync(stop);
                }
                finally
                {
                    _write.Release();
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Stopped, or the phone went away: nothing is listening.
        }
    }

    private async Task StopKeepAliveAsync()
    {
        _stop?.Cancel();
        if (_keepAlive is { } running) await running;
        _keepAlive = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopKeepAliveAsync();
        _stop?.Dispose();
        _write.Dispose();
    }
}
