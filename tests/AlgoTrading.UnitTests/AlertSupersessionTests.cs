using AlgoTrading.Api.Services;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The backend's terse run start/stop ("X started on Y") is left to the
/// notifier's full message; nothing else is. The supervised notifier runs with
/// --no-forward, so its own filter never ran and every start and stop reached
/// Telegram twice until 27 Sep. The cases are the ones in
/// ForwarderSuppressionTests (test_telegram_notifier.py): one rule, two ports.
/// </summary>
public class AlertSupersessionTests
{
    private static bool Superseded(string title, string source)
        => AlertSubscriberService.IsSupersededByNotifier(new AlertEventPayload { Title = title, Source = source });

    [Theory]
    [InlineData("GhostTangentCrossings stopped on SENSEX")]
    [InlineData("FulcrumBuy started on NIFTY")]
    public void The_backends_run_start_and_stop_are_superseded(string title)
    {
        Assert.True(Superseded(title, "strategyrun"));
    }

    [Theory]
    [InlineData("Feed stalled — FulcrumBuy on BANKNIFTY", "strategyrun")]
    [InlineData("[admin] Feed stalled — FulcrumBuy on BANKNIFTY", "strategyrun")]
    [InlineData("Feed recovered — FulcrumBuy on BANKNIFTY", "strategyrun")]
    [InlineData("Kill switch engaged", "risk")]
    [InlineData("Trading resumed", "risk")]
    [InlineData("Market data stopped", "process")]
    [InlineData("Dhan feed stopped", "process")]
    [InlineData("FYERS feed started", "process")]
    [InlineData("FulcrumBuy started on NIFTY", "process")]
    public void Feed_risk_and_process_alerts_are_kept(string title, string source)
    {
        Assert.False(Superseded(title, source));
    }

    [Theory]
    [InlineData("Strategy started · FulcrumBuy · NIFTY")]
    [InlineData("Strategy stopped · FulcrumBuy · NIFTY · -₹344.50")]
    [InlineData("[admin] Strategy started · FulcrumBuy · NIFTY")]
    [InlineData("[coderforchange] Strategy stopped · FulcrumBuy · NIFTY · -₹344.50")]
    [InlineData("[admin] FulcrumBuy · NIFTY · rolled - 1 out, 1 in")]
    public void The_notifiers_own_messages_are_never_superseded(string title)
    {
        Assert.False(Superseded(title, "strategyrun"));
    }

    [Fact]
    public void A_payload_without_a_title_or_source_is_kept()
    {
        Assert.False(AlertSubscriberService.IsSupersededByNotifier(new AlertEventPayload()));
    }
}
