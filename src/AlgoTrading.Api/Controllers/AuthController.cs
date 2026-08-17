using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.Auth;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AlgoTrading.Domain.Entities;
using Microsoft.AspNetCore.Authorization;

namespace AlgoTrading.Api.Controllers;

    /// <summary>
    /// Exposes endpoints to trigger the OAuth login flow, handle callbacks, and check the current active broker session.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
    private readonly StartBrokerAuthUseCase _startBrokerAuthUseCase;
    private readonly GenerateAccessTokenUseCase _generateAccessTokenUseCase;
    private readonly IBrokerSessionStore _brokerSessionStore;

    public AuthController(
        StartBrokerAuthUseCase startBrokerAuthUseCase,
        GenerateAccessTokenUseCase generateAccessTokenUseCase,
        IBrokerSessionStore brokerSessionStore)
    {
        _startBrokerAuthUseCase = startBrokerAuthUseCase;
        _generateAccessTokenUseCase = generateAccessTokenUseCase;
        _brokerSessionStore = brokerSessionStore;
    }

    [HttpGet("start")]
    public async Task<IActionResult> Start(CancellationToken cancellationToken)
    {
        await _startBrokerAuthUseCase.ExecuteAsync(cancellationToken);

        return Ok(new
        {
            message = "FYERS auth flow started. Complete login in browser. Access token response will be printed in terminal after callback."
        });
    }

    [AllowAnonymous]
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery(Name = "auth_code")] string? authCode,
        [FromQuery] string? state,
        [FromQuery(Name = "s")] string? status,
        [FromQuery] int? code,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authCode))
        {
            return BadRequest(new
            {
                message = "FYERS redirected back, but auth_code was missing.",
                authCode,
                state,
                status,
                code
            });
        }

        JObject tokenResponse = await _generateAccessTokenUseCase.ExecuteAsync(authCode, cancellationToken);

        // Print full token response to terminal / Visual Studio output
        Console.WriteLine("========== FYERS TOKEN RESPONSE ==========");
        Console.WriteLine(tokenResponse.ToString(Formatting.Indented));
        Console.WriteLine("==========================================");

        string accessToken = tokenResponse["TOKEN"]?.ToString() ?? string.Empty;
        string refreshToken = tokenResponse["refresh_token"]?.ToString() ?? string.Empty;

        var session = new BrokerSession
        { 
            BrokerName = "FYERS",
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            CreatedUtc = DateTime.UtcNow
        };

        await _brokerSessionStore.SaveAsync(session, cancellationToken);

        // Return only a simple message to browser
        return Ok(new
        {
            message = "Access token generated successfully. Check terminal / Visual Studio output.",
            isAuthenticated = session.IsAuthenticated,
            state,
            status,
            code
        });
    }

    [HttpGet("session")]
    public async Task<IActionResult> GetSession(CancellationToken cancellationToken)
    {
        var session = await _brokerSessionStore.GetCurrentAsync(cancellationToken);

        if (session is null)
        {
            return Ok(new
            {
                broker = "FYERS",
                isAuthenticated = false,
                accessToken = string.Empty,
                refreshToken = string.Empty
            });
        }

        return Ok(new
        {
            broker = session.BrokerName,
            isAuthenticated = session.IsAuthenticated,
            createdUtc = session.CreatedUtc,
            updatedUtc = session.UpdatedUtc,
            accessToken = session.AccessToken,
            refreshToken = session.RefreshToken
        });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        await _brokerSessionStore.ClearAsync(cancellationToken);

        return Ok(new
        {
            message = "Broker session cleared successfully."
        });
    }
}