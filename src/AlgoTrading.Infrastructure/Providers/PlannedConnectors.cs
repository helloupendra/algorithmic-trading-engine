using AlgoTrading.Application.Providers;

namespace AlgoTrading.Infrastructure.Providers;

/// <summary>
/// A vendor the platform intends to support but has no adapter for yet.
/// </summary>
/// <param name="Key">The key its adapter will use once it ships.</param>
/// <param name="Note">Why it is on the list — shown to the operator as-is.</param>
public sealed record PlannedConnector(
    string Key,
    string DisplayName,
    ProviderKind Kind,
    string Note);

/// <summary>
/// The connector roadmap, surfaced in the console so the Connectors list reads as
/// a real directory instead of a list of one. These have no adapter and cannot be
/// configured — the console says exactly that rather than offering a form that
/// would not work.
/// </summary>
/// <remarks>
/// Kept in step with <c>docs/roadmap/broker-and-data-provider-module.md</c>. An
/// entry moves off this list by being implemented, at which point its descriptor
/// takes over and the key must match.
/// </remarks>
public static class PlannedConnectors
{
    public static readonly IReadOnlyList<PlannedConnector> All = new[]
    {
        new PlannedConnector(
            "dhan",
            "Dhan",
            ProviderKind.Both,
            "Free API with a websocket feed, chosen as the first real second vendor so failover can be tested against something live."),
    };
}
