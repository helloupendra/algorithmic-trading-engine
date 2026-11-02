using System.Text.RegularExpressions;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.UnitTests;

/// <summary>
/// What every alert on Redis <c>alerts:new</c> becomes on Telegram. Until 2 Nov
/// 2026 the subscriber sent its title and message as HTML unescaped, so any
/// <c>&lt;</c> had the alert refused and lost, and it bound keys case-sensitively,
/// so LogicEngine's alerts arrived as an empty "🚨 ALERT: !".
/// </summary>
public class AlertSubscriberTextTests
{
    [Theory]
    [InlineData("MTM < -2L", "MTM &lt; -2L")]
    [InlineData("TypeError: '<' not supported between 'str' and 'int'", "TypeError: '&lt;' not supported between 'str' and 'int'")]
    [InlineData("M&M <class 'int'> a>b", "M&amp;M &lt;class 'int'&gt; a&gt;b")]
    public void Backend_text_is_escaped(string message, string escaped)
    {
        string text = AlertSubscriberService.TelegramText(new AlertEventPayload { Title = "Kill switch ACTIVATED", Message = message });

        Assert.Equal($"🚨 ALERT: Kill switch ACTIVATED!\n{escaped}", text);
    }

    [Fact]
    public void A_kill_switch_notice_as_the_controller_publishes_it_arrives_escaped()
    {
        var payload = AlertSubscriberService.ReadPayload(RedisSystemNotifier.Payload(
            NotificationCategory.Risk, NotificationSeverity.Error, "Kill switch ACTIVATED",
            "All strategies paused and every open position flattened. By admin — MTM < -2L.",
            underlying: null, symbol: null, simulationRunId: null, recordOnly: false))!;

        Assert.Contains("By admin — MTM &lt; -2L.", AlertSubscriberService.TelegramText(payload));
    }

    [Fact]
    public void The_title_is_escaped_too()
    {
        string text = AlertSubscriberService.TelegramText(new AlertEventPayload { Title = "M&M <stopped>", Message = "x" });

        Assert.StartsWith("🚨 ALERT: M&amp;M &lt;stopped&gt;!\n", text);
    }

    [Fact]
    public void A_message_the_notifier_formatted_keeps_its_markup_but_not_the_titles()
    {
        const string own = "▶️ <b>Strategy started</b> — <b>M&amp;M</b>\n\nRisk: SL &lt; 5";

        string text = AlertSubscriberService.TelegramText(new AlertEventPayload { Title = "[admin] Strategy started · M&M", Message = own });

        Assert.Equal($"🚨 ALERT: [admin] Strategy started · M&amp;M!\n{own}", text);
    }

    [Fact]
    public void A_heading_alone_is_the_rule_so_plain_text_with_tags_is_escaped()
    {
        string text = AlertSubscriberService.TelegramText(new AlertEventPayload { Title = "t", Message = "<b>not ours</b>" });

        Assert.EndsWith("&lt;b&gt;not ours&lt;/b&gt;", text);
    }

    [Fact]
    public void An_alert_with_nothing_in_it_still_says_it_is_an_alert()
    {
        Assert.Equal("🚨 ALERT: Alert!\n", AlertSubscriberService.TelegramText(new AlertEventPayload()));
    }

    [Fact]
    public void The_notifiers_headings_are_the_notifiers()
    {
        var script = File.ReadAllText(Path.Combine(PortfolioTestKit.RepoRoot(), "scripts", "telegram_notifier.py"));
        var tuple = Regex.Match(script, @"^OWN_HEADINGS = \((?<items>.*)\)$", RegexOptions.Multiline).Groups["items"].Value;
        var theirs = Regex.Matches(tuple, "\"(?<h>[^\"]+)\"").Select(m => m.Groups["h"].Value).ToList();

        Assert.NotEmpty(theirs);
        Assert.Equal(theirs, TelegramHtml.NotifierHeadings);
    }

    [Fact]
    public void Lowercase_keys_bind_as_logic_engine_used_to_publish_them()
    {
        // _publish_alert in strategies/logic_engine.py until 2 Nov 2026, and any runner still running that code.
        const string published = """
            {"title":"NIFTY BEAR TRAP","message":"Logic: divergence. Action: Watch 24800 PE. Premium: ₹120","source":"logic_engine",
             "underlying":"NIFTY","severity":"warning","symbol":"NSE:NIFTY26N0324800PE","simulationRunId":77}
            """;

        var payload = AlertSubscriberService.ReadPayload(published)!;

        Assert.Equal("NIFTY BEAR TRAP", payload.Title);
        Assert.Equal("logic_engine", payload.Source);
        Assert.Equal("NIFTY", payload.Underlying);
        Assert.Equal("warning", payload.Severity);
        Assert.Equal("NSE:NIFTY26N0324800PE", payload.Symbol);
        Assert.Equal(77, payload.SimulationRunId);
        Assert.Equal("🚨 ALERT: NIFTY BEAR TRAP!\nLogic: divergence. Action: Watch 24800 PE. Premium: ₹120", AlertSubscriberService.TelegramText(payload));
    }

    [Fact]
    public void PascalCase_keys_still_bind_as_every_other_publisher_writes_them()
    {
        var payload = AlertSubscriberService.ReadPayload("""{"Title":"NIFTY BEAR TRAP","Message":"m","Source":"logic_engine","Underlying":"NIFTY","Severity":"warning","Symbol":"S","SimulationRunId":null,"RecordOnly":true}""")!;

        Assert.Equal(("NIFTY BEAR TRAP", "m", "NIFTY"), (payload.Title, payload.Message, payload.Underlying));
        Assert.True(AlertSubscriberService.IsRecordOnly(payload));
    }

    [Theory]
    [InlineData("kill switch < now")]                     // not JSON at all
    [InlineData("""{"Title":"x","SimulationRunId":"not a number"}""")]   // a field of the wrong type
    public void Text_that_is_not_an_event_still_goes_out(string published)
    {
        var payload = AlertSubscriberService.ReadPayload(published)!;

        Assert.Equal(published, payload.Message);
        Assert.Contains("Alert in an unknown format", AlertSubscriberService.TelegramText(payload));
        Assert.Contains(TelegramHtml.Escape(published), AlertSubscriberService.TelegramText(payload));
    }
}
