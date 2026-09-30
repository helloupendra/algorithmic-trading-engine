using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTrading.Infrastructure.Ai;

public static class AiRegistration
{
    /// <summary>
    /// The hosted-model gateway. <c>Ai:*</c> from appsettings.Local.json, with
    /// <c>NVIDIA_API_KEY</c> / <c>NVIDIA_API_BASE_URL</c> as the fallback, as
    /// Angel's settings fall back to its .env names.
    /// </summary>
    public static IServiceCollection AddAi(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiSettings>(configuration.GetSection("Ai"));
        services.PostConfigure<AiSettings>(s =>
        {
            if (string.IsNullOrWhiteSpace(s.ApiKey)) s.ApiKey = configuration["NVIDIA_API_KEY"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(s.BaseUrl)) s.BaseUrl = configuration["NVIDIA_API_BASE_URL"] ?? AiCatalog.DefaultBaseUrl;
            s.ApiKey = s.ApiKey.Trim();
        });

        // The client keeps its own time limits: first token, silence, ceiling.
        services.AddHttpClient(NvidiaChatClient.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<NvidiaChatClient>();
        services.AddSingleton<AiModelCatalog>();
        services.AddSingleton<AiRateLimiter>();
        services.AddSingleton<AiModelHealth>();
        services.AddHostedService<AiHealthProbe>();
        services.AddScoped<AiSettingsStore>();
        // The desk tools (IAiTool) are registered by the API, which owns the read services they use.
        services.AddScoped<AiToolbox>();
        services.AddScoped<AiGateway>();
        return services;
    }
}
