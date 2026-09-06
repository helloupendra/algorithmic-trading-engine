using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using AlgoTrading.Contracts.LiveData;

namespace AlgoTrading.Api.Hubs;

/// <summary>
/// SignalR Hub for broadcasting live data feeds (ticks and quotes) to the frontend.
/// </summary>
/// <remarks>
/// Authorized. What travels over it is live market data the platform pays a
/// broker for, and it used to go to anyone who knew the URL.
/// </remarks>
[Authorize]
public class LiveFeedHub : Hub
{
    // Clients just subscribe and listen to "ReceiveTick" or "ReceiveQuote" events
    // Broadcasts will be pushed from controllers or background services.
}
