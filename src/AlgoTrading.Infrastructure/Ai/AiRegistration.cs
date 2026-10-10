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

        // Claude and Titan on Amazon Bedrock (Ai:Bedrock). Off by default, and then never called: the SDK clients are
        // made on the first call, and their credentials are the machine's default AWS chain (the EC2 instance
        // profile), never a key in the desk's settings.
        services.AddHttpClient(BedrockClaudeClient.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<BedrockClaudeClient>();

        // The open models on Bedrock (Ai:BedrockOpen), OpenAI-compatible. On by default, yet nothing calls AWS, or even
        // looks for credentials, until a chain reaches one of them: each request is then signed with SigV4 by the
        // machine's default AWS chain.
        services.AddHttpClient(BedrockOpenChatClient.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<IBedrockRequestSigner>(_ => new AwsSigV4BedrockSigner());
        services.AddSingleton<BedrockOpenChatClient>();
        services.AddSingleton(sp => new AiChatRouter(
            sp.GetRequiredService<NvidiaChatClient>(), sp.GetRequiredService<BedrockClaudeClient>(), sp.GetRequiredService<BedrockOpenChatClient>()));
        services.AddSingleton<IBedrockRuntimeInvoker, AwsBedrockRuntimeInvoker>();
        services.AddSingleton<BedrockEmbeddingClient>();
        services.AddSingleton<AiEmbedder>();
        services.AddScoped<AiReembedder>();

        services.AddSingleton<AiModelCatalog>();
        services.AddSingleton<AiRateLimiter>();
        services.AddSingleton<AiModelHealth>();
        services.AddSingleton<AiDocIndex>();
        services.AddHostedService<AiHealthProbe>();
        services.AddScoped<AiSettingsStore>();
        // The desk tools (IAiTool) are registered by the API, which owns the read services they use.
        services.AddScoped<AiToolbox>();
        services.AddScoped<AiMemoryBook>();
        services.AddScoped<AiGateway>();
        return services;
    }
}
