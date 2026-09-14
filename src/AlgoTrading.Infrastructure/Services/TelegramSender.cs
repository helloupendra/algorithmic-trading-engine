using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// The API's one client for the Telegram Bot API, configured by
/// <c>Telegram:BotToken</c> and <c>Telegram:ChatId</c> — the same two settings
/// the alert subscriber has always read and the Alerts page reports on.
/// </summary>
/// <remarks>
/// Configuration is read at every send rather than captured at startup, so
/// appsettings.Local.json (loaded with reloadOnChange) takes effect without a
/// restart. Sending never throws: a Telegram outage is logged and reported as
/// a failed send, never allowed to stop the thing being reported on. The token
/// is part of the request URL, so a failure is logged by status or exception
/// type only — never the exception text, which can carry the URL.
/// </remarks>
public sealed class TelegramSender
{
    public const string HttpClientName = "telegram";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TelegramSender> _logger;

    public TelegramSender(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<TelegramSender> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_configuration["Telegram:BotToken"]) &&
        !string.IsNullOrWhiteSpace(_configuration["Telegram:ChatId"]);

    /// <summary>Sends <paramref name="text"/> with parse_mode HTML. False when not configured or refused.</summary>
    public async Task<bool> SendHtmlAsync(string text, CancellationToken cancellationToken = default)
    {
        var token = _configuration["Telegram:BotToken"];
        var chatId = _configuration["Telegram:ChatId"];
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId)) return false;

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsJsonAsync(
                $"https://api.telegram.org/bot{token}/sendMessage",
                new { chat_id = chatId, text, parse_mode = "HTML", disable_web_page_preview = true },
                cancellationToken);

            if (response.IsSuccessStatusCode) return true;

            _logger.LogWarning("Telegram refused a message: HTTP {Status}.", (int)response.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Telegram send failed: {Type}.", ex.GetType().Name);
            return false;
        }
    }
}
