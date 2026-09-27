using AlgoTrading.Api.Services;
using AlgoTrading.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Two Telegram channels: trades, and the desk's own system notices. Until
/// 27 Sep everything went to the live channel, deploys and candle patterns
/// among the trades.
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

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
