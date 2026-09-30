using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>The provider's model list as the desk last read it.</summary>
/// <param name="Source"><c>provider</c> (read just now), <c>cache</c> (the last good list) or <c>unavailable</c>.</param>
/// <param name="Error">Why the provider could not be read this time, else null.</param>
public sealed record AiModelList(IReadOnlyList<ProviderModel> Models, string Source, DateTime? FetchedUtc, string? Error)
{
    /// <summary>Every id a chain may name: the provider's list, or the default chains when it is out of reach.</summary>
    public IReadOnlySet<string> KnownIds() =>
        Models.Count > 0
            ? Models.Select(m => m.Id).Concat(AiCatalog.DefaultModels).ToHashSet(StringComparer.Ordinal)
            : AiCatalog.DefaultModels;
}

/// <summary>
/// The provider's model list, read at most every ten minutes. A failed read
/// keeps serving the last good list, and says so.
/// </summary>
public sealed class AiModelCatalog
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(10);

    private readonly NvidiaChatClient _client;
    private readonly IOptionsMonitor<AiSettings> _settings;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private IReadOnlyList<ProviderModel> _models = [];
    private DateTime? _fetchedUtc;

    public AiModelCatalog(NvidiaChatClient client, IOptionsMonitor<AiSettings> settings, TimeProvider? time = null)
    {
        _client = client;
        _settings = settings;
        _time = time ?? TimeProvider.System;
    }

    public async Task<AiModelList> GetAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!_settings.CurrentValue.KeyConfigured)
        {
            return new AiModelList(_models, _models.Count > 0 ? "cache" : "unavailable", _fetchedUtc,
                "No NVIDIA_API_KEY on the server.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        if (!refresh && _fetchedUtc is DateTime at && now - at < FreshFor)
        {
            return new AiModelList(_models, "provider", _fetchedUtc, null);
        }

        await _refresh.WaitAsync(cancellationToken);
        try
        {
            // Another request may have refreshed it while this one waited.
            if (!refresh && _fetchedUtc is DateTime again && _time.GetUtcNow().UtcDateTime - again < FreshFor)
            {
                return new AiModelList(_models, "provider", _fetchedUtc, null);
            }

            try
            {
                var models = await _client.ListModelsAsync(cancellationToken);
                _models = models.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
                _fetchedUtc = _time.GetUtcNow().UtcDateTime;
                return new AiModelList(_models, "provider", _fetchedUtc, null);
            }
            catch (AiAttemptFailedException ex)
            {
                string error = $"The provider's model list could not be read: {ex.Outcome}.";
                return new AiModelList(_models, _models.Count > 0 ? "cache" : "unavailable", _fetchedUtc, error);
            }
        }
        finally
        {
            _refresh.Release();
        }
    }
}
