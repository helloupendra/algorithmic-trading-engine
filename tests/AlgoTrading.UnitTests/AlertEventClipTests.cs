using System.Text.Json;
using AlgoTrading.Api.Services;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// 28 Sep: 387 alerts reached Telegram but not the alert log, each one's save
/// failing on "22001: value too long for type character varying(1000)".
/// </summary>
public class AlertEventClipTests
{
    [Fact]
    public void A_long_message_is_cut_to_its_column_and_kept_whole_in_the_metadata()
    {
        var message = new string('x', 1500);
        var payload = new AlertEventPayload { Title = "Feed stalled", Message = message, Source = "strategy", Underlying = "NIFTY" };

        var row = AlertSubscriberService.ToAlertEvent(payload, delivered: true, DateTime.UtcNow);

        Assert.Equal(1000, row.Message.Length);
        Assert.EndsWith("…", row.Message);
        Assert.Equal(message, JsonSerializer.Deserialize<AlertEventPayload>(row.MetadataJson!)!.Message);
        Assert.True(row.DeliveredToTelegram);
    }

    [Fact]
    public void Every_text_fits_its_column()
    {
        var payload = new AlertEventPayload
        {
            Title = new string('t', 300), Message = "m", Source = new string('s', 80),
            Underlying = new string('u', 50), Severity = new string('v', 30), Symbol = new string('y', 150),
        };

        var row = AlertSubscriberService.ToAlertEvent(payload, delivered: false, DateTime.UtcNow);

        Assert.Equal(200, row.Title.Length);
        Assert.Equal(60, row.Source.Length);
        Assert.Equal(40, row.Underlying.Length);
        Assert.Equal(20, row.Severity.Length);
        Assert.Equal(100, row.Symbol!.Length);
    }

    [Fact]
    public void Short_texts_and_defaults_are_kept_as_they_are()
    {
        var row = AlertSubscriberService.ToAlertEvent(new AlertEventPayload { Message = "ok" }, delivered: false, DateTime.UtcNow);

        Assert.Equal("ok", row.Message);
        Assert.Equal("Alert", row.Title);
        Assert.Equal("system", row.Source);
        Assert.Equal("UNKNOWN", row.Underlying);
        Assert.Equal("info", row.Severity);
        Assert.Null(row.Symbol);
    }
}
