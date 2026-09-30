using System.Text;
using System.Text.Json;
using AlgoTrading.Infrastructure.Ai;
using Microsoft.AspNetCore.Http.Features;

namespace AlgoTrading.Api.Services;

/// <summary>
/// A call's progress as server-sent events on the response: <c>start</c>,
/// <c>attempt</c>, <c>reasoning</c>, <c>delta</c>, <c>fallback</c>, then
/// <c>done</c> or <c>error</c> (written by the controller).
/// </summary>
/// <remarks>
/// <para>
/// The response starts on the first event, not before: a call the gateway
/// refuses (agent off, rate limit, no key) never starts the stream, so the
/// controller can still answer it as plain JSON with the right status.
/// </para>
/// <para>
/// Once started, a <c>: ping</c> comment goes out every
/// <see cref="PingEvery"/>, so a model that is queued or silent before its
/// first token does not leave the response quiet: Cloudflare cuts one that
/// sends nothing for 100 seconds.
/// Writes are serialised: the ping timer and the model's pieces share one
/// response body.
/// </para>
/// </remarks>
public sealed class AiEventStream : IAiStreamSink, IAsyncDisposable
{
    public static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpResponse _response;
    private readonly CancellationToken _aborted;
    private readonly SemaphoreSlim _write = new(1, 1);
    private CancellationTokenSource? _pingStop;
    private Task? _pinger;

    public AiEventStream(HttpResponse response, CancellationToken aborted)
    {
        _response = response;
        _aborted = aborted;
    }

    public bool Started { get; private set; }

    public async ValueTask StartAsync(long callId, IReadOnlyList<string> chain)
    {
        _response.StatusCode = StatusCodes.Status200OK;
        _response.ContentType = "text/event-stream; charset=utf-8";
        _response.Headers.CacheControl = "no-cache, no-transform";
        // Tells nginx-style proxies not to hold the stream back.
        _response.Headers["X-Accel-Buffering"] = "no";
        _response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await _response.StartAsync(_aborted);
        Started = true;

        await SendAsync("start", new { callId, chain });

        _pingStop = CancellationTokenSource.CreateLinkedTokenSource(_aborted);
        _pinger = PingAsync(_pingStop.Token);
    }

    public ValueTask AttemptAsync(string model, int number, int of) => SendAsync("attempt", new { model, n = number, of });

    public ValueTask ReasoningAsync(string text) => SendAsync("reasoning", new { text });

    public ValueTask DeltaAsync(string text) => SendAsync("delta", new { text });

    public ValueTask FallbackAsync(string model, string reason, string? next) => SendAsync("fallback", new { model, reason, next });

    /// <summary>Writes one event and flushes it, so the browser sees it now.</summary>
    public async ValueTask SendAsync(string name, object data)
    {
        string frame = $"event: {name}\ndata: {JsonSerializer.Serialize(data, Json)}\n\n";
        await WriteAsync(frame);
    }

    private async ValueTask WriteAsync(string frame)
    {
        await _write.WaitAsync(_aborted);
        try
        {
            await _response.Body.WriteAsync(Encoding.UTF8.GetBytes(frame), _aborted);
            await _response.Body.FlushAsync(_aborted);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task PingAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(PingEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                await WriteAsync(": ping\n\n");
            }
        }
        catch (OperationCanceledException)
        {
            // The call ended or the browser left.
        }
        catch (IOException)
        {
            // The connection went; the call's own write will notice.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_pingStop is not null)
        {
            await _pingStop.CancelAsync();
            if (_pinger is not null) await _pinger;
            _pingStop.Dispose();
        }

        _write.Dispose();
    }
}
