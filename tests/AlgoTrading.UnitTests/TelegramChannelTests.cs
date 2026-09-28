using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AlgoTrading.Api.Services;
using AlgoTrading.Infrastructure.Patterns;
using AlgoTrading.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Two Telegram channels: trades, and the desk's own system notices. Until
/// 27 Sep everything went to the live channel, deploys and candle patterns
/// among the trades. On 28 Sep the owner moved the candle patterns (and the
/// indicator alerts with them) back to the channel the trade alerts go to,
/// as a setting: <c>PatternAlerts:TelegramChannel</c>.
/// </summary>
public class TelegramChannelTests
{
    private static TelegramSender Sender(string? systemChat) =>
        new(new NoHttp(), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = "123:abc",
            ["Telegram:ChatId"] = "-100111",
            ["Telegram:SystemChatId"] = systemChat,
        }).Build(), NullLogger<TelegramSender>.Instance);

    [Fact]
    public void System_messages_go_to_the_system_chat_and_trades_stay_where_they_were()
    {
        var sender = Sender("-100222");
        Assert.Equal("-100222", sender.ChatIdFor(TelegramChannel.System));
        Assert.Equal("-100111", sender.ChatIdFor(TelegramChannel.Trades));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Without_a_system_chat_everything_still_goes_out(string? systemChat)
    {
        Assert.Equal("-100111", Sender(systemChat).ChatIdFor(TelegramChannel.System));
    }

    [Theory]
    [InlineData("process", TelegramChannel.System)]
    [InlineData("Connector", TelegramChannel.System)]
    [InlineData("system", TelegramChannel.System)]
    [InlineData("strategyrun", TelegramChannel.Trades)]
    [InlineData("risk", TelegramChannel.Trades)]
    [InlineData("something-new", TelegramChannel.Trades)]
    [InlineData(null, TelegramChannel.Trades)]
    public void Each_alert_source_has_its_channel(string? source, TelegramChannel channel)
    {
        Assert.Equal(channel, AlertSubscriberService.ChannelFor(source));
    }

    // ---------------------------------------------- market alerts' channel --

    private static IConfiguration Settings(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    [Fact]
    public void Market_alerts_go_to_the_trades_channel_unless_a_setting_says_system()
    {
        // Nothing set: the trades channel, the owner's choice on 28 Sep.
        Assert.Equal(TelegramChannel.Trades, AlertChannel.ForPatterns(Settings(), out var none));
        Assert.Null(none);
        Assert.Equal(TelegramChannel.Trades, AlertChannel.ForIndicators(Settings(), out none));
        Assert.Null(none);

        // Any case, spaces around ignored.
        Assert.Equal(TelegramChannel.System, AlertChannel.ForPatterns(Settings(("PatternAlerts:TelegramChannel", " System ")), out none));
        Assert.Null(none);
        Assert.Equal(TelegramChannel.Trades, AlertChannel.ForPatterns(Settings(("PatternAlerts:TelegramChannel", "TRADES")), out none));
        Assert.Null(none);

        // The indicators follow the patterns unless told otherwise.
        Assert.Equal(TelegramChannel.System, AlertChannel.ForIndicators(Settings(("PatternAlerts:TelegramChannel", "system")), out _));
        Assert.Equal(TelegramChannel.Trades, AlertChannel.ForIndicators(
            Settings(("PatternAlerts:TelegramChannel", "system"), ("IndicatorAlerts:TelegramChannel", "trades")), out _));
        Assert.Equal(TelegramChannel.System, AlertChannel.ForIndicators(Settings(("IndicatorAlerts:TelegramChannel", "system")), out _));
        Assert.Equal(TelegramChannel.Trades, AlertChannel.ForPatterns(Settings(("IndicatorAlerts:TelegramChannel", "system")), out _));
        // Blank is not set.
        Assert.Equal(TelegramChannel.System, AlertChannel.ForIndicators(
            Settings(("PatternAlerts:TelegramChannel", "system"), ("IndicatorAlerts:TelegramChannel", " ")), out _));

        Assert.Equal("trades", AlertChannel.Name(TelegramChannel.Trades));
        Assert.Equal("system", AlertChannel.Name(TelegramChannel.System));
    }

    [Theory]
    [InlineData("PatternAlerts:TelegramChannel", "desk")]
    [InlineData("PatternAlerts:TelegramChannel", "trade")]
    [InlineData("IndicatorAlerts:TelegramChannel", "Desk System")]
    public void A_value_that_is_neither_channel_is_the_trades_channel_and_is_said(string key, string value)
    {
        // Never an exception: a background service that throws stops the whole API (AlertSwitch).
        var settings = Settings((key, value));
        var channel = key.StartsWith("Indicator", StringComparison.Ordinal)
            ? AlertChannel.ForIndicators(settings, out var problem)
            : AlertChannel.ForPatterns(settings, out problem);

        Assert.Equal(TelegramChannel.Trades, channel);
        Assert.Equal($"{key} is \"{value}\", which is neither trades nor system; sent to the trades channel.", problem);
    }

    [Fact]
    public async Task Candle_patterns_reach_the_trades_chat_by_default_and_the_system_chat_when_set()
    {
        var byDefault = new Rig();
        Assert.True(await byDefault.Patterns().SendAsync("doji", CancellationToken.None));
        Assert.Equal(["-100111"], byDefault.Telegram.Chats);

        var system = new Rig(("PatternAlerts:TelegramChannel", "system"));
        Assert.True(await system.Patterns().SendAsync("doji", CancellationToken.None));
        Assert.Equal(["-100222"], system.Telegram.Chats);
        Assert.Empty(system.Log.Warnings);
    }

    [Fact]
    public async Task Indicator_alerts_follow_the_patterns_channel_unless_their_own_setting_names_one()
    {
        var byDefault = new Rig();
        Assert.True(await byDefault.Indicators().SendAsync("rsi", CancellationToken.None));
        Assert.Equal(["-100111"], byDefault.Telegram.Chats);

        var following = new Rig(("PatternAlerts:TelegramChannel", "system"));
        Assert.True(await following.Indicators().SendAsync("rsi", CancellationToken.None));
        Assert.Equal(["-100222"], following.Telegram.Chats);

        var own = new Rig(("PatternAlerts:TelegramChannel", "system"), ("IndicatorAlerts:TelegramChannel", "trades"));
        Assert.True(await own.Indicators().SendAsync("rsi", CancellationToken.None));
        Assert.Equal(["-100111"], own.Telegram.Chats);
    }

    [Fact]
    public async Task A_bad_channel_value_sends_to_the_trades_chat_with_one_warning_not_one_per_message()
    {
        var t = new Rig(("PatternAlerts:TelegramChannel", "desk"));
        var patterns = t.Patterns();
        var indicators = t.Indicators();

        for (int i = 0; i < 3; i++)
        {
            Assert.True(await patterns.SendAsync($"doji {i}", CancellationToken.None));
            Assert.True(await indicators.SendAsync($"rsi {i}", CancellationToken.None));
        }

        Assert.All(t.Telegram.Chats, chat => Assert.Equal("-100111", chat));
        Assert.Equal(6, t.Telegram.Chats.Count);
        Assert.Equal(
        [
            "Candle-pattern alerts: PatternAlerts:TelegramChannel is \"desk\", which is neither trades nor system; sent to the trades channel.",
            "Indicator alerts: PatternAlerts:TelegramChannel is \"desk\", which is neither trades nor system; sent to the trades channel.",
        ], t.Log.Warnings.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The two services over a sender whose HTTP is a fake Telegram that
    /// records the chat each message was posted to: no request leaves a test.
    /// </summary>
    private sealed class Rig
    {
        public FakeTelegram Telegram { get; } = new();
        public ListLogger Log { get; } = new();
        private readonly IConfiguration _config;
        private readonly TelegramSender _sender;
        private readonly IServiceScopeFactory _scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        public Rig(params (string Key, string? Value)[] settings)
        {
            _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:BotToken"] = "123:abc",
                ["Telegram:ChatId"] = "-100111",
                ["Telegram:SystemChatId"] = "-100222",
            }.Concat(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))).Build();

            // No pacing wait: the second message to a chat goes at once.
            _sender = new TelegramSender(new Factory(Telegram), _config, NullLogger<TelegramSender>.Instance,
                delay: (_, _) => Task.CompletedTask, callerWait: TimeSpan.FromSeconds(30));
        }

        public CandlePatternAlertService Patterns() =>
            new(_scopes, new PatternScannerState(), _sender, _config, Log.For<CandlePatternAlertService>());

        public IndicatorAlertService Indicators() =>
            new(_scopes, new IndicatorScannerState(), new IndicatorAlertConfigSource(Path.GetTempPath(), null), _sender, _config,
                Log.For<IndicatorAlertService>());
    }

    private sealed class FakeTelegram : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _chats = new();
        public List<string> Chats => [.. _chats];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            _chats.Enqueue(JsonDocument.Parse(body).RootElement.GetProperty("chat_id").GetString() ?? string.Empty);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true}""") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ListLogger
    {
        private readonly ConcurrentQueue<string> _warnings = new();
        public List<string> Warnings => [.. _warnings];

        public ILogger<T> For<T>() => new Typed<T>(this);

        private sealed class Typed<T>(ListLogger owner) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning) owner._warnings.Enqueue(formatter(state, exception));
            }
        }
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
