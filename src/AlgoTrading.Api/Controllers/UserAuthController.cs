
using System.Security.Claims;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.IdentityModel.Tokens.Jwt;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;

namespace AlgoTrading.Api.Controllers
{

    [ApiController]
    [Route("api/[controller]")]
    public class UserAuthController : ControllerBase
    {

        private readonly IAuthService _authService;

        public UserAuthController(IAuthService authService)
        {
            _authService = authService;
        }

        [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
        [HttpPost("register")]
        public async Task<IActionResult> Register(
            [FromBody] RegisterRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                var result = await _authService.RegisterAsync(request, cancellationToken);

                HttpContext.Describe(
                    $"Created the account {result.User.UserName} ({result.User.Role}).",
                    "user",
                    result.User.Id.ToString());

                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [AllowAnonymous]
        [HttpPost("login")]
        public async Task<IActionResult> Login(
            [FromBody] LoginRequest request,
            CancellationToken cancellationToken)
        {
            // A sign-in carries no token yet, so the activity log cannot name the
            // caller from the request itself. Both outcomes name them here: who
            // got in, and — the row that matters more — which name was tried and
            // refused. The submitted username is not a secret; the password is
            // never touched.
            string attempted = request.UserNameOrEmail?.Trim() ?? string.Empty;

            try
            {
                var result = await _authService.LoginAsync(request, cancellationToken);

                HttpContext.AttributeTo(result.User.Id, result.User.UserName, result.User.Role);
                HttpContext.Describe(
                    $"{result.User.UserName} signed in ({result.User.Role}).",
                    "user",
                    result.User.Id.ToString());

                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                HttpContext.Describe($"Failed sign-in for \"{attempted}\".", "user", attempted);
                return BadRequest(new { message = ex.Message });
            }
        }

        [AllowAnonymous]
        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh(
            [FromBody] RefreshTokenRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                var result = await _authService.RefreshAsync(request, cancellationToken);

                HttpContext.AttributeTo(result.User.Id, result.User.UserName, result.User.Role);
                HttpContext.Describe(
                    $"{result.User.UserName} refreshed their session.",
                    "user",
                    result.User.Id.ToString());

                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                // A refused refresh is how a revoked session shows up: the token
                // was cut off and something still tried to come back on it.
                HttpContext.Describe("A refresh token was refused.");
                return BadRequest(new { message = ex.Message });
            }
        }

        [Authorize]
        [HttpPost("logout")]
        public async Task<IActionResult> Logout(
            [FromBody] LogoutRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                await _authService.LogoutAsync(request, cancellationToken);
                return Ok(new { message = "Logged out successfully." });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Lists every account. Admin-only: it exposes usernames, emails and
        /// allocated capital across all traders.
        /// </summary>
        [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
        [HttpGet]
        public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
        {
            var result = await _authService.GetAllUsersAsync(cancellationToken);
            return Ok(result);
        }

        [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(long id, CancellationToken cancellationToken)
        {
            var result = await _authService.GetUserByIdAsync(id, cancellationToken);
            if (result is null)
                return NotFound(new { message = "User not found." });

            return Ok(result);
        }

        [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
        [HttpDelete("{username}")]
        public async Task<IActionResult> DeleteByUsername(string username, CancellationToken cancellationToken)
        {
            var success = await _authService.DeleteUserByUsernameAsync(username, cancellationToken);
            if (!success)
                return NotFound(new { message = "User not found." });

            return Ok(new { message = $"User '{username}' deleted successfully." });
        }

        [Authorize]
        [HttpGet("me")]
        public async Task<IActionResult> Me(CancellationToken cancellationToken)
        {
            var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                      ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (string.IsNullOrWhiteSpace(sub) || !long.TryParse(sub, out var userId))
                return Unauthorized();

            var result = await _authService.GetMeAsync(userId, cancellationToken);
            if (result is null)
                return NotFound(new { message = "User not found." });

            return Ok(result);
        }

    }
}
