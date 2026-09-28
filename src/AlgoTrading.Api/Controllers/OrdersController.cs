// src/AlgoTrading.Api/Controllers/OrdersController.cs
using System.Globalization;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// The day's orders across every run and manual book: Trade → Orders.
/// </summary>
/// <remarks>
/// Behind the strategies grant, like the runs they belong to. Ownership comes
/// from the token, exactly as for the run list and the open positions: a
/// trader sees their own orders whatever userId they pass; an admin sees
/// every account's, or one account's with userId.
/// </remarks>
[RequireModule(PlatformModules.Strategies)]
[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly OrdersBuilder _orders;

    public OrdersController(OrdersBuilder orders)
    {
        _orders = orders;
    }

    /// <summary>
    /// One IST day (date yyyy-MM-dd, default today) of paper orders and of the
    /// orders the risk gate refused, newest first, <paramref name="take"/> (at
    /// most 500) from <paramref name="skip"/>. Filters: runId, symbol (exact),
    /// status (Filled | Pending | Cancelled | Rejected; any when absent), mode
    /// (LivePaper, the default, or OfflineReplay for backtests), and for an
    /// admin userId. The answer also counts the day's rows by run and by
    /// status over every account the caller may see, before those filters,
    /// for the filters to offer; see <see cref="OrdersResponse"/>.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<OrdersResponse>> GetOrders(
        [FromQuery] string? date,
        [FromQuery] long? runId,
        [FromQuery] string? symbol,
        [FromQuery] string? status,
        [FromQuery] string? mode,
        [FromQuery] long? userId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = OrdersFilter.DefaultTake,
        CancellationToken cancellationToken = default)
    {
        DateOnly day;
        if (string.IsNullOrWhiteSpace(date))
        {
            day = IstTime.DateOf(DateTime.UtcNow);
        }
        else if (!DateOnly.TryParseExact(date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
        {
            return BadRequest(new { message = "date must be an IST calendar day in yyyy-MM-dd form." });
        }

        string? runMode = NormalizeMode(mode);
        if (runMode is null)
            return BadRequest(new { message = $"mode must be {PaperTradingService.LivePaperMode} or {PaperTradingService.OfflineReplayMode}." });

        string? orderStatus = null;
        if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status.Trim(), "any", StringComparison.OrdinalIgnoreCase))
        {
            orderStatus = OrdersBuilder.Statuses.FirstOrDefault(s => string.Equals(s, status.Trim(), StringComparison.OrdinalIgnoreCase));
            if (orderStatus is null)
                return BadRequest(new { message = $"status must be one of {string.Join(", ", OrdersBuilder.Statuses)}, or any." });
        }

        if (take < 1 || take > OrdersFilter.MaxTake)
            return BadRequest(new { message = $"take must be between 1 and {OrdersFilter.MaxTake}." });

        if (skip < 0)
            return BadRequest(new { message = "skip must be zero or positive." });

        string? contract = string.IsNullOrWhiteSpace(symbol) ? null : symbol.Trim();

        // The counts cover everything the caller may see, so an admin's account
        // filter can offer every account; the rows, the account asked for.
        var filter = new OrdersFilter(day, runMode, User.ScopeUserId(null), User.ScopeUserId(userId), runId, contract, orderStatus, skip, take);
        return Ok(await _orders.BuildAsync(filter, cancellationToken));
    }

    /// <summary>The run mode in its stored spelling; LivePaper when none is given, null when it is neither.</summary>
    private static string? NormalizeMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return PaperTradingService.LivePaperMode;
        string trimmed = mode.Trim();
        if (string.Equals(trimmed, PaperTradingService.LivePaperMode, StringComparison.OrdinalIgnoreCase)) return PaperTradingService.LivePaperMode;
        if (string.Equals(trimmed, PaperTradingService.OfflineReplayMode, StringComparison.OrdinalIgnoreCase)) return PaperTradingService.OfflineReplayMode;
        return null;
    }
}
