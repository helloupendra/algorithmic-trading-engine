using AlgoTrading.Application.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTrading.Infrastructure.Providers.Angel;

/// <summary>
/// The Angel One connector registers itself, like every other vendor.
/// </summary>
/// <remarks>
/// Deliberately inert: no hosted service, no feed, no timer. Registering it
/// adds an entry to the Connectors page and a client that is used only when
/// someone asks. FYERS and Dhan are untouched.
/// </remarks>
public static class AngelRegistration
{
    public static IServiceCollection AddAngelProvider(
        this IServiceCollection services,
        IConfiguration configuration,
        ProviderCatalogSeed catalog,
        ProviderCredentialFallbacks credentialFallbacks)
    {
        var section = configuration.GetSection("Angel");
        services.Configure<AngelSettings>(section);
        services.PostConfigure<AngelSettings>(s =>
        {
            // .env is the fallback for every field: the console cannot hold a
            // PIN or a TOTP secret yet.
            if (string.IsNullOrWhiteSpace(s.ApiKey)) s.ApiKey = configuration["ANGEL_API_KEY"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(s.ClientCode)) s.ClientCode = configuration["ANGEL_CLIENT_CODE"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(s.Pin)) s.Pin = configuration["ANGEL_PIN"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(s.TotpSecret)) s.TotpSecret = configuration["ANGEL_TOTP_SECRET"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(s.StaticIp)) s.StaticIp = configuration["ANGEL_STATIC_IP"] ?? string.Empty;
            string? root = configuration["ANGEL_ROOT_URL"];
            if (!string.IsNullOrWhiteSpace(root) && section["RootUrl"] is null) s.RootUrl = root;
        });

        catalog.Add(AngelProvider.Descriptor);

        credentialFallbacks.Add(
            AngelProvider.Key,
            section["ClientCode"] ?? configuration["ANGEL_CLIENT_CODE"] ?? string.Empty,
            section["TotpSecret"] ?? configuration["ANGEL_TOTP_SECRET"] ?? string.Empty,
            redirectUri: null);

        services.AddHttpClient(AngelProvider.Key, client => client.Timeout = TimeSpan.FromSeconds(60));
        services.AddScoped<AngelApiClient>();

        // The movers screen: six SmartAPI calls behind one cached snapshot.
        services.AddScoped<AngelMarketMovers>();
        return services;
    }
}
