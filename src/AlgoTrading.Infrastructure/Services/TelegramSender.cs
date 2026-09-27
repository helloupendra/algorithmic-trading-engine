using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>Which of the desk's two Telegram channels a message belongs in.</summary>
public enum TelegramChannel
{
    /// <summary>Trading: runs started and stopped, legs, risk, the morning plan. <c>Telegram:ChatId</c>.</summary>
    Trades,

    /// <summary>
    /// The desk itself: feeds, sign-ins, deploys, Sentinel, candle patterns.
    /// <c>Telegram:SystemChatId</c>, or the trades channel while that is not set.
    /// </summary>
    System,
}

/// <summary>
/// The API's one client for the Telegram Bot API, configured by
/// <c>Telegram:BotToken</c> and <c>Telegram:ChatId</c> — the same two settings
/// the alert subscriber has always read and the Alerts page reports on — plus
/// <c>Telegram:SystemChatId</c> for the system channel.
/// </summary>
/// <remarks>
/// Configuration is read at every send rather than captured at startup, so
/// appsettings.Local.json (loaded with reloadOnChange) takes effect without a
/// restart. Sending never throws: a Telegram outage is logged and reported as
/// a failed send, never allowed to stop the thing being reported on. The token
/// is part of the request URL, so a failure is logged by status or exception
/// type only — never the exception text, which can carry the URL.
/// <para>
/// Messages to one chat go out one at a time, about one a second, from a
/// queue per chat. On 25 Sep 2026 the runners' feed alerts arrived in bursts
/// (572 that day) and every one was posted at once; Telegram answered 350 of
/// them with 429 "Too Many Requests: retry after N", and each of those was
/// logged and thrown away. Now a 429 holds the chat's queue for the
/// <c>retry_after</c> Telegram names and the same message is tried again. The
/// queue is bounded: past <see cref="MaxQueuedPerChat"/> the oldest message is
/// dropped, with a log line, rather than letting a flood grow without limit.
/// </para>
/// </remarks>
public sealed class TelegramSender : IDisposable
{
    public const string HttpClientName = "telegram";

    /// <summary>Messages waiting per chat before the oldest is dropped.</summary>
    public const int MaxQueuedPerChat = 200;

    /// <summary>
    /// The gap between two messages to one chat. Telegram asks bots to stay
    /// under about one message a second per chat; a group is held to about
    /// twenty a minute, and a 429 there says how long to wait.
    /// </summary>
    public static readonly TimeSpan Spacing = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a caller waits for its own message's answer. A message still
    /// queued after this is reported as sent — it is going out, in order — so
    /// no caller is held behind a burst or a 429 pause.
    /// </summary>
    public static readonly TimeSpan CallerWait = TimeSpan.FromSeconds(3);

    /// <summary>A message refused with 429 this many times running is given up, so one message can never hold a chat forever.</summary>
    public const int MaxRateLimitAttempts = 10;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TelegramSender> _logger;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _callerWait;
    // Lazy: GetOrAdd may run its factory twice under a race, and each queue starts a pump.
    private readonly ConcurrentDictionary<string, Lazy<ChatQueue>> _queues = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();

    /// <param name="time">The clock the pacing reads; the system clock when null.</param>
    /// <param name="delay">
    /// How the pump waits between messages and out a 429. <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
    /// on <paramref name="time"/> when null; a test passes one that moves its own clock instead.
    /// </param>
    /// <param name="callerWait"><see cref="CallerWait"/> unless a test needs it shorter.</param>
    public TelegramSender(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<TelegramSender> logger,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? callerWait = null)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((span, ct) => Task.Delay(span, _time, ct));
        _callerWait = callerWait ?? CallerWait;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_configuration["Telegram:BotToken"]) &&
        !string.IsNullOrWhiteSpace(_configuration["Telegram:ChatId"]);

    /// <summary>
    /// The chat a channel's messages go to. Until 27 Sep everything went to the
    /// one live channel, and the owner found deploys, feed restarts and candle
    /// patterns mixed in with the trades. A system chat that is not configured
    /// falls back to the trades chat: a message in the wrong channel beats one
    /// that is never sent.
    /// </summary>
    public string? ChatIdFor(TelegramChannel channel)
    {
        var trades = _configuration["Telegram:ChatId"];
        if (channel == TelegramChannel.System)
        {
            var system = _configuration["Telegram:SystemChatId"];
            if (!string.IsNullOrWhiteSpace(system)) return system;
        }
        return trades;
    }

    /// <summary>Sends <paramref name="text"/> with parse_mode HTML to the trades channel. False when not configured or refused.</summary>
    public Task<bool> SendHtmlAsync(string text, CancellationToken cancellationToken = default)
        => SendHtmlAsync(text, TelegramChannel.Trades, cancellationToken);

    /// <summary>
    /// Queues <paramref name="text"/> (parse_mode HTML) for <paramref name="channel"/>'s
    /// chat and waits up to <see cref="CallerWait"/> for the answer.
    /// </summary>
    /// <returns>
    /// True when Telegram took it, or when it is still queued behind the pacing
    /// and going out (a refusal after that is logged). False when Telegram is
    /// not configured, refused it within the wait, or it was dropped from a
    /// full queue.
    /// </returns>
    public async Task<bool> SendHtmlAsync(string text, TelegramChannel channel, CancellationToken cancellationToken = default)
    {
        var token = _configuration["Telegram:BotToken"];
        var chatId = ChatIdFor(channel);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId)) return false;

        var message = new Outgoing(text, channel);
        _queues.GetOrAdd(chatId, id => new Lazy<ChatQueue>(() => new ChatQueue(this, id))).Value.Enqueue(message);

        try
        {
            // The caller's wait is real time on purpose: it bounds how long a
            // request thread is held, whatever clock the pacing reads.
            var first = await Task.WhenAny(message.Result, Task.Delay(_callerWait, cancellationToken));
            return first == message.Result ? await message.Result : true;
        }
        catch (OperationCanceledException)
        {
            // The caller gave up waiting; the message is queued and still goes.
            return true;
        }
    }

    /// <summary>Messages waiting for <paramref name="channel"/>'s chat, the one being sent included.</summary>
    public int QueuedFor(TelegramChannel channel)
        => ChatIdFor(channel) is { } chatId && _queues.TryGetValue(chatId, out var queue) ? queue.Value.Count : 0;

    /// <summary>Stops the pumps at shutdown; whatever is still queued is not sent.</summary>
    public void Dispose() => _stopping.Cancel();

    private sealed class Outgoing(string text, TelegramChannel channel)
    {
        private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Text { get; } = text;
        public TelegramChannel Channel { get; } = channel;
        public Task<bool> Result => _result.Task;
        public void Complete(bool sent) => _result.TrySetResult(sent);
    }

    /// <summary>One chat's queue and the single pump that empties it, in order.</summary>
    private sealed class ChatQueue
    {
        private readonly TelegramSender _owner;
        private readonly string _chatId;
        private readonly Channel<Outgoing> _queue;
        private int _count;
        private DateTimeOffset _nextSendAt = DateTimeOffset.MinValue;

        public ChatQueue(TelegramSender owner, string chatId)
        {
            _owner = owner;
            _chatId = chatId;
            _queue = Channel.CreateBounded<Outgoing>(
                new BoundedChannelOptions(MaxQueuedPerChat)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                },
                Dropped);
            _ = Task.Run(PumpAsync);
        }

        public int Count => Volatile.Read(ref _count);

        public void Enqueue(Outgoing message)
        {
            Interlocked.Increment(ref _count);
            if (!_queue.Writer.TryWrite(message))
            {
                // Only after Dispose: the writer is never completed otherwise.
                Interlocked.Decrement(ref _count);
                message.Complete(false);
            }
        }

        private void Dropped(Outgoing message)
        {
            Interlocked.Decrement(ref _count);
            message.Complete(false);
            _owner._logger.LogWarning(
                "Telegram queue for the {Channel} channel is full ({Max} waiting); dropped the oldest message: {Start}",
                message.Channel, MaxQueuedPerChat, Preview(message.Text));
        }

        private async Task PumpAsync()
        {
            var stopping = _owner._stopping.Token;
            try
            {
                while (await _queue.Reader.WaitToReadAsync(stopping))
                {
                    // Taken off the queue before it is sent, so a flood that
                    // drops the oldest can never drop the message in flight.
                    if (!_queue.Reader.TryRead(out var message)) continue;
                    try
                    {
                        message.Complete(await DeliverAsync(message, stopping));
                    }
                    catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                    {
                        message.Complete(false);
                        return;
                    }
                    catch (Exception ex)
                    {
                        // The pump must outlive any one message.
                        _owner._logger.LogWarning("Telegram send failed: {Type}.", ex.GetType().Name);
                        message.Complete(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _count);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
        }

        /// <summary>One message: paced, and tried again after each 429 for as long as Telegram asks.</summary>
        private async Task<bool> DeliverAsync(Outgoing message, CancellationToken stopping)
        {
            for (int attempt = 1; ; attempt++)
            {
                var wait = _nextSendAt - _owner._time.GetUtcNow();
                if (wait > TimeSpan.Zero) await _owner._delay(wait, stopping);

                var (sent, retryAfter) = await _owner.PostAsync(_chatId, message.Text, stopping);
                var now = _owner._time.GetUtcNow();
                if (retryAfter is null)
                {
                    _nextSendAt = now + Spacing;
                    return sent;
                }

                _nextSendAt = now + retryAfter.Value;
                if (attempt >= MaxRateLimitAttempts)
                {
                    _owner._logger.LogWarning(
                        "Telegram refused a message with 429 {Attempts} times running; giving it up: {Start}",
                        attempt, Preview(message.Text));
                    return false;
                }

                _owner._logger.LogWarning(
                    "Telegram asked to slow down (429); holding the {Channel} channel for {Seconds:0} s and sending the same message again ({Waiting} waiting).",
                    message.Channel, retryAfter.Value.TotalSeconds, Count);
            }
        }
    }

    /// <summary>
    /// One POST to sendMessage. RetryAfter is set only for a 429, from the
    /// body's <c>parameters.retry_after</c> (or the Retry-After header).
    /// </summary>
    private async Task<(bool Sent, TimeSpan? RetryAfter)> PostAsync(string chatId, string text, CancellationToken cancellationToken)
    {
        var token = _configuration["Telegram:BotToken"];
        if (string.IsNullOrWhiteSpace(token)) return (false, null);

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsJsonAsync(
                $"https://api.telegram.org/bot{token}/sendMessage",
                new { chat_id = chatId, text, parse_mode = "HTML", disable_web_page_preview = true },
                cancellationToken);

            if (response.IsSuccessStatusCode) return (true, null);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return (false, await RetryAfterAsync(response, cancellationToken));
            }

            _logger.LogWarning("Telegram refused a message: HTTP {Status}.", (int)response.StatusCode);
            return (false, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Telegram send failed: {Type}.", ex.GetType().Name);
            return (false, null);
        }
    }

    /// <summary>
    /// How long a 429 asks for: <c>{"parameters":{"retry_after":N}}</c>, else the
    /// Retry-After header, else five seconds; never less than one.
    /// </summary>
    internal static async Task<TimeSpan> RetryAfterAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        int? seconds = null;
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (body.RootElement.TryGetProperty("parameters", out var parameters)
                && parameters.TryGetProperty("retry_after", out var retry)
                && retry.TryGetInt32(out var value))
            {
                seconds = value;
            }
        }
        catch (JsonException)
        {
            // Not JSON: fall through to the header.
        }

        if (seconds is null && response.Headers.RetryAfter?.Delta is { } delta)
        {
            seconds = (int)Math.Ceiling(delta.TotalSeconds);
        }

        return TimeSpan.FromSeconds(Math.Max(1, seconds ?? 5));
    }

    private static string Preview(string text)
    {
        var line = text.ReplaceLineEndings(" ");
        return line.Length <= 80 ? line : line[..79] + "…";
    }
}
