using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Providers.Dhan;
using AlgoTrading.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services;

public sealed record ChainUnderlyingHealth(string Underlying, bool MarketOpen, DateTime? LastCapturedUtc,
    double? CaptureLagSeconds, string State, string? LastOutcome);
public sealed record ChainWriterHealth(string Provider, string Writer, bool Required, bool Enabled,
    string State, int StaleAfterSeconds, int CaptureWindowSeconds, DateTime? LastCapturedUtc, double? CaptureLagSeconds,
    IReadOnlyList<ChainUnderlyingHealth> Underlyings);
public sealed record OptionChainHealth(string SelectedProvider, string State, bool ConflictingWriters,
    DateTime CheckedUtc, IReadOnlyList<ChainWriterHealth> Writers);

/// <summary>
/// Reports the writer selected by the recorder switches. Dhan's enabled switch
/// selects its hosted recorder; otherwise the Python FYERS fallback is expected.
/// Authentication alone does not select a writer. A second live writer is a
/// conflict because their captures would interleave in the same history.
/// </summary>
public sealed class OptionChainHealthService(
    OptionChainService chain,
    DhanChainPollerState dhan,
    IOptions<DhanSettings> settings,
    IMarketSessionService sessions,
    IConfiguration configuration,
    TimeProvider? time = null)
{
    public async Task<OptionChainHealth> ReadAsync(bool pythonRunning, CancellationToken ct = default)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        bool dhanEnabled = dhan.Enabled;
        string selected = dhanEnabled ? "dhan" : "fyers";
        bool conflict = dhanEnabled && pythonRunning;
        var dhanConfig = settings.Value.ChainPoller;
        var fyersNames = (configuration["CHAIN_UNDERLYINGS"] ?? "BANKNIFTY,NIFTY")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.ToUpperInvariant()).Distinct().ToArray();
        var writers = new[]
        {
            await ReadWriter("dhan", "Dhan API recorder", dhanEnabled, dhanConfig.UnderlyingList,
                Math.Max(120, Math.Max(15, dhanConfig.IntervalSeconds) * 2 + 30)),
            await ReadWriter("fyers", "FYERS Python poller", pythonRunning, fyersNames, 120),
        };
        return new(selected, conflict ? "conflict" : writers.Single(w => w.Required).State, conflict, now, writers);

        async Task<ChainWriterHealth> ReadWriter(string provider, string writer, bool enabled,
            IReadOnlyList<string> names, int staleAfter)
        {
            bool required = selected == provider;
            // Bound reads by capture time so Timescale can exclude old chunks.
            // A disabled backup must not walk months of the active writer's history.
            int window = Math.Max(900, staleAfter * 2);
            var rows = new List<ChainUnderlyingHealth>();
            foreach (string name in names)
            {
                string exchange = name is "SENSEX" or "BANKEX" ? "BSE"
                    : DhanInstruments.IndexUnderlyings.ContainsKey(name) ? "NSE" : "MCX";
                bool open = sessions.IsMarketOpen(now, exchange, exchange == "MCX" ? "COM" : "FO");
                var captured = required || enabled
                    ? await chain.GetLastCaptureUtcAsync(ct, provider, name, now.AddSeconds(-window)) : null;
                double? lag = captured is { } at ? Math.Max(0, (now - at).TotalSeconds) : null;
                var outcome = provider == "dhan" ? dhan.Outcomes.FirstOrDefault(o => o.Underlying == name) : null;
                // Old failures from yesterday/another session are not today's verdict.
                bool failed = outcome is not null && (now - outcome.AttemptUtc).TotalSeconds <= staleAfter
                    && outcome.State is not ("recorded" or "idle")
                    && (captured is null || outcome.AttemptUtc >= captured);
                string state = !required && !enabled ? "disabled" : !open ? "idle" : !enabled ? "stopped"
                    : lag is null || lag > staleAfter ? "stale" : failed ? "degraded" : "healthy";
                rows.Add(new(name, open, captured, lag, state, outcome?.State));
            }
            string aggregate = !required && !enabled ? "disabled" : rows.Count == 0 ? "unconfigured"
                : rows.Any(r => r.State == "stopped") ? "stopped"
                : rows.Any(r => r.State == "stale") ? "stale"
                : rows.Any(r => r.State == "degraded") ? "degraded"
                : rows.Any(r => r.State == "healthy") ? "healthy" : "idle";
            var last = rows.Select(r => r.LastCapturedUtc).Max();
            return new(provider, writer, required, enabled, aggregate, staleAfter, window, last,
                last is { } stamp ? Math.Max(0, (now - stamp).TotalSeconds) : null, rows);
        }
    }
}
