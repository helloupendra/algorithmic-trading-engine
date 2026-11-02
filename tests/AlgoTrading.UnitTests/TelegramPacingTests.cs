using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AlgoTrading.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// On 25 Sep 2026 the runners' feed alerts reached TelegramSender in bursts
/// and every one was posted at once: Telegram answered 350 with 429 "Too Many
/// Requests: retry after N" and each was logged and thrown away. Now one chat's
/// messages go out about a second apart, a 429 holds the chat for the time
/// Telegram names and the same message goes again, and no caller waits long.
/// </summary>
public class TelegramPacingTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Messages_to_one_chat_go_out_a_second_apart()
    {
        var t = new Rig();

        var results = await Task.WhenAll(
            t.Sender.SendHtmlAsync("one", TelegramChannel.System),
            t.Sender.SendHtmlAsync("two", TelegramChannel.System),
            t.Sender.SendHtmlAsync("three", TelegramChannel.System));

        Assert.All(results, Assert.True);
        Assert.Equal(new[] { "one", "two", "three" }, t.Telegram.Posts.Select(p => p.Text));
        Assert.Equal(new double[] { 0, 1, 2 }, t.Telegram.Posts.Select(p => (p.At - Start).TotalSeconds));
    }

    [Fact]
    public async Task A_429_holds_the_chat_for_retry_after_and_the_same_message_goes_again()
    {
        var t = new Rig();
        t.Telegram.Answers.Enqueue(() => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"ok":false,"error_code":429,"description":"Too Many Requests: retry after 7","parameters":{"retry_after":7}}"""),
        });

        var results = await Task.WhenAll(
            t.Sender.SendHtmlAsync("stalled", TelegramChannel.Trades),
            t.Sender.SendHtmlAsync("recovered", TelegramChannel.Trades));

        Assert.All(results, Assert.True);   // nothing dropped
        Assert.Equal(new[] { "stalled", "stalled", "recovered" }, t.Telegram.Posts.Select(p => p.Text));
        Assert.Equal(new double[] { 0, 7, 8 }, t.Telegram.Posts.Select(p => (p.At - Start).TotalSeconds));
        Assert.Contains(t.Log.Lines, l => l.Contains("Telegram asked to slow down (429); holding the Trades channel for 7 s"));
    }

    [Fact]
    public async Task Without_a_body_the_retry_after_header_is_honoured()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("slow down") };
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(12));

        Assert.Equal(TimeSpan.FromSeconds(12), await TelegramSender.RetryAfterAsync(response, CancellationToken.None));
    }

    [Fact]
    public async Task Two_chats_do_not_wait_for_each_other()
    {
        var t = new Rig(systemChat: "-100222");

        await Task.WhenAll(
            t.Sender.SendHtmlAsync("trade", TelegramChannel.Trades),
            t.Sender.SendHtmlAsync("desk", TelegramChannel.System));

        Assert.All(t.Telegram.Posts, p => Assert.Equal(Start, p.At));
    }

    [Fact]
    public async Task The_caller_is_not_held_behind_a_slow_chat()
    {
        var t = new Rig(callerWait: TimeSpan.FromMilliseconds(50));
        t.Telegram.Gate = new TaskCompletionSource();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool result = await t.Sender.SendHtmlAsync("slow", TelegramChannel.System);
        watch.Stop();

        Assert.True(result);   // queued and going out, not failed
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"held {watch.Elapsed}");
        t.Telegram.Gate.SetResult();
    }

    [Fact]
    public async Task A_full_queue_drops_the_oldest_waiting_message_never_the_one_in_flight()
    {
        var t = new Rig(callerWait: TimeSpan.FromSeconds(30));
        t.Telegram.Gate = new TaskCompletionSource();

        var inFlight = t.Sender.SendHtmlAsync("#0", TelegramChannel.System);
        await t.Telegram.FirstPost.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var waiting = Enumerable.Range(1, TelegramSender.MaxQueuedPerChat + 1)
            .Select(i => t.Sender.SendHtmlAsync($"#{i}", TelegramChannel.System))
            .ToList();

        // #1 was the oldest waiting when #201 arrived.
        Assert.False(await waiting[0].WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains(t.Log.Lines, l => l.StartsWith(TelegramSender.LostLine + ": the System channel's queue is full") && l.Contains("dropped the oldest message: #1"));

        t.Telegram.Gate.SetResult();
        Assert.True(await inFlight.WaitAsync(TimeSpan.FromSeconds(10)));
        await Task.WhenAll(waiting.Skip(1)).WaitAsync(TimeSpan.FromSeconds(20));

        var sent = t.Telegram.Posts.Select(p => p.Text).ToList();
        Assert.Equal(TelegramSender.MaxQueuedPerChat + 1, sent.Count);
        Assert.Equal("#0", sent[0]);
        Assert.DoesNotContain("#1", sent);
        Assert.Equal($"#{TelegramSender.MaxQueuedPerChat + 1}", sent[^1]);
    }

    private static HttpResponseMessage BadRequest(string description) => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent($$"""{"ok":false,"error_code":400,"description":"{{description}}"}"""),
    };

    [Fact]
    public async Task A_message_refused_for_its_html_goes_once_more_as_plain_text()
    {
        var t = new Rig();
        t.Telegram.Answers.Enqueue(() => BadRequest("Bad Request: can't parse entities: Unsupported start tag"));

        bool sent = await t.Sender.SendHtmlAsync("<b>Kill switch</b> MTM &lt; -2L <oops>", TelegramChannel.Trades);

        Assert.True(sent);
        var posts = t.Telegram.Posts.ToList();
        Assert.Equal(2, posts.Count);
        Assert.Equal("HTML", posts[0].ParseMode);
        Assert.Null(posts[1].ParseMode);
        Assert.Equal("Kill switch MTM < -2L <oops>", posts[1].Text);
        Assert.Contains(t.Log.Lines, l => l.Contains("Telegram refused a message (HTTP 400 Bad Request: can't parse entities: Unsupported start tag); sending it again as plain text"));
        Assert.DoesNotContain(t.Log.Lines, l => l.StartsWith(TelegramSender.LostLine));
    }

    [Fact]
    public async Task A_message_too_long_for_telegram_goes_as_plain_text_cut_to_fit()
    {
        var t = new Rig();
        t.Telegram.Answers.Enqueue(() => BadRequest("Bad Request: message is too long"));

        Assert.True(await t.Sender.SendHtmlAsync(new string('x', 6000), TelegramChannel.System));

        var plain = t.Telegram.Posts.ToList()[1];
        Assert.Null(plain.ParseMode);
        Assert.Equal(TelegramHtml.MaxMessageChars, plain.Text.Length);
        Assert.EndsWith("…", plain.Text);
    }

    [Fact]
    public async Task A_message_refused_as_html_and_as_plain_text_is_reported_lost_and_the_next_one_still_goes()
    {
        var t = new Rig();
        t.Telegram.Answers.Enqueue(() => BadRequest("Bad Request: chat not found"));
        t.Telegram.Answers.Enqueue(() => BadRequest("Bad Request: chat not found"));

        var results = await Task.WhenAll(
            t.Sender.SendHtmlAsync("kill switch", TelegramChannel.Trades),
            t.Sender.SendHtmlAsync("next", TelegramChannel.Trades));

        Assert.Equal(new[] { false, true }, results);
        Assert.Equal(new[] { "kill switch", "kill switch", "next" }, t.Telegram.Posts.Select(p => p.Text));   // once as HTML, once plain, never more
        Assert.Contains(t.Log.Lines, l => l == "Telegram alert lost: HTTP 400 Bad Request: chat not found: kill switch");
    }

    [Fact]
    public async Task A_429_on_the_plain_text_retry_is_waited_out_and_stays_plain()
    {
        var t = new Rig();
        t.Telegram.Answers.Enqueue(() => BadRequest("Bad Request: can't parse entities"));
        t.Telegram.Answers.Enqueue(() => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"ok":false,"parameters":{"retry_after":3}}"""),
        });

        Assert.True(await t.Sender.SendHtmlAsync("<x>", TelegramChannel.Trades));

        Assert.Equal(new string?[] { "HTML", null, null }, t.Telegram.Posts.Select(p => p.ParseMode));
    }

    [Fact]
    public async Task A_send_that_fails_on_the_way_is_tried_again_and_a_5xx_too()
    {
        var t = new Rig();
        t.Telegram.Answers.Enqueue(() => throw new HttpRequestException("no route"));
        t.Telegram.Answers.Enqueue(() => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>") });

        Assert.True(await t.Sender.SendHtmlAsync("stalled", TelegramChannel.System));

        Assert.Equal(new double[] { 0, 5, 10 }, t.Telegram.Posts.Select(p => (p.At - Start).TotalSeconds));
        Assert.DoesNotContain(t.Log.Lines, l => l.StartsWith(TelegramSender.LostLine));
    }

    [Fact]
    public async Task A_send_that_keeps_failing_is_reported_lost_without_the_url()
    {
        var t = new Rig();
        for (int i = 0; i < TelegramSender.MaxTransientAttempts; i++)
        {
            t.Telegram.Answers.Enqueue(() => throw new HttpRequestException("failed for https://api.telegram.org/bot123:abc/sendMessage"));
        }

        Assert.False(await t.Sender.SendHtmlAsync("stalled", TelegramChannel.System));

        Assert.Equal(TelegramSender.MaxTransientAttempts, t.Telegram.Posts.Count);
        Assert.Contains(t.Log.Lines, l => l == "Telegram alert lost: HttpRequestException: stalled");
        Assert.DoesNotContain(t.Log.Lines, l => l.Contains("123:abc"));
    }

    [Fact]
    public async Task A_message_429d_too_many_times_is_reported_lost()
    {
        var t = new Rig();
        for (int i = 0; i < TelegramSender.MaxRateLimitAttempts; i++)
        {
            t.Telegram.Answers.Enqueue(() => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("""{"parameters":{"retry_after":1}}""") });
        }

        Assert.False(await t.Sender.SendHtmlAsync("flood", TelegramChannel.System));

        Assert.Contains(t.Log.Lines, l => l == $"Telegram alert lost: refused with 429 {TelegramSender.MaxRateLimitAttempts} times running; giving it up: flood");
    }

    [Fact]
    public void Plain_text_keeps_what_was_said()
    {
        Assert.Equal("Run P&L: -₹20 x < y", TelegramHtml.ToPlain("Run P&amp;L: <b>-₹20</b> x &lt; y"));
        Assert.Equal("TypeError: <class 'int'> '<' not", TelegramHtml.ToPlain("TypeError: <class 'int'> '<' not"));
        Assert.Equal("a link", TelegramHtml.ToPlain("<a href=\"https://x\">a link</a>"));
    }

    [Fact]
    public void The_container_still_builds_the_sender_with_its_real_clock()
    {
        // The test seams are optional parameters; the API registers the type bare.
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddHttpClient()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<TelegramSender>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.False(provider.GetRequiredService<TelegramSender>().IsConfigured);
    }

    // ------------------------------------------------------------------

    private sealed class Rig
    {
        public Clock Clock { get; } = new(Start);
        public FakeTelegram Telegram { get; }
        public ListLogger Log { get; } = new();
        public TelegramSender Sender { get; }

        public Rig(string? systemChat = null, TimeSpan? callerWait = null)
        {
            Telegram = new FakeTelegram(Clock);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:BotToken"] = "123:abc",
                ["Telegram:ChatId"] = "-100111",
                ["Telegram:SystemChatId"] = systemChat,
            }).Build();

            // The pump's waits move the fake clock instead of passing real time.
            Sender = new TelegramSender(new Factory(Telegram), config, Log, Clock,
                (span, _) =>
                {
                    Clock.Advance(span);
                    return Task.CompletedTask;
                },
                callerWait ?? TimeSpan.FromSeconds(30));
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private readonly object _lock = new();
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() { lock (_lock) return _now; }
        public void Advance(TimeSpan span) { lock (_lock) _now += span; }
    }

    private sealed class FakeTelegram(Clock clock) : HttpMessageHandler
    {
        public ConcurrentQueue<(DateTimeOffset At, string Text, string? ParseMode)> Posts { get; } = new();
        public ConcurrentQueue<Func<HttpResponseMessage>> Answers { get; } = new();
        public TaskCompletionSource? Gate { get; set; }
        public TaskCompletionSource FirstPost { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var root = JsonDocument.Parse(body).RootElement;
            var text = root.GetProperty("text").GetString() ?? string.Empty;
            Posts.Enqueue((clock.GetUtcNow(), text, root.TryGetProperty("parse_mode", out var mode) ? mode.GetString() : null));
            FirstPost.TrySetResult();
            if (Gate is { } gate) await gate.Task;
            return Answers.TryDequeue(out var answer)
                ? answer()
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true}""") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ListLogger : ILogger<TelegramSender>
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Enqueue(formatter(state, exception));
    }
}
