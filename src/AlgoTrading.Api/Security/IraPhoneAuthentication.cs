using System.Security.Claims;
using System.Text.Encodings.Web;
using AlgoTrading.Api.Services.AiPhone;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Security;

/// <summary>
/// The phone token's own authentication scheme: an Apple Shortcut's <c>Authorization: Bearer ira_…</c>, accepted by
/// <c>POST api/Ira/ask</c> alone.
/// </summary>
/// <remarks>
/// <para>
/// Why it opens nothing else: the scheme is not the default one, so no endpoint consults it unless its policy names it,
/// and only <see cref="AuthorizationPolicies.IraPhone"/> does, which only <c>IraPhoneController</c> carries. The
/// default scheme, the console's JWT, is told to leave a bearer that starts with <c>ira_</c> alone
/// (<see cref="CarriesPhoneToken"/>), so on every other endpoint a phone token is no credential at all: 401, as with
/// none. And the principal it builds holds no role, so even a policy that asked for any scheme would not find an admin
/// in it.
/// </para>
/// <para>
/// A refusal answers 401 with <c>{ error, spoken }</c>: the Shortcut speaks <c>spoken</c>, so the owner hears why
/// rather than silence. The token is never logged.
/// </para>
/// </remarks>
public static class IraPhoneAuthentication
{
    public const string Scheme = "IraPhone";

    /// <summary>The phone token's row id, on the principal the scheme builds; the policy requires it.</summary>
    public const string TokenIdClaim = "ira:phone-token";

    /// <summary>What the owner called the phone.</summary>
    public const string LabelClaim = "ira:phone-label";

    public const string RefusedText = "This phone's Ira token was not accepted: it is unknown, revoked, or its account is no longer an admin.";

    public const string RefusedSpoken = "This phone's Ira token was not accepted. Make a new one in the Ira app's settings.";

    public static AuthenticationBuilder AddIraPhoneTokens(this AuthenticationBuilder builder) =>
        builder.AddScheme<AuthenticationSchemeOptions, IraPhoneTokenHandler>(Scheme, "Ira phone token", _ => { });

    /// <summary>
    /// Whether the request's bearer is a phone token (it starts with <c>ira_</c>): the console's JWT handler then leaves
    /// it alone, neither validating nor logging it.
    /// </summary>
    public static bool CarriesPhoneToken(HttpRequest request) => IraPhoneTokens.FromAuthorization(request.Headers.Authorization) is not null;

    /// <summary>The phone token a request was authenticated with, from the principal the scheme built; null for any other.</summary>
    public static long? PhoneTokenId(this ClaimsPrincipal principal) =>
        long.TryParse(principal.FindFirstValue(TokenIdClaim), out long id) ? id : null;
}

/// <summary>Reads a phone token, checks it against its stored hash and its owner's account, and names the owner.</summary>
public sealed class IraPhoneTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? token = IraPhoneTokens.FromAuthorization(Request.Headers.Authorization);
        if (token is null) return AuthenticateResult.NoResult();

        var tokens = Context.RequestServices.GetRequiredService<IraPhoneTokens>();
        var caller = await tokens.AuthenticateAsync(token, Context.RequestAborted);
        if (caller is null) return AuthenticateResult.Fail("The phone token is unknown, revoked, or its owner is not an active admin.");

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, caller.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, caller.UserName),
                new Claim(IraPhoneAuthentication.TokenIdClaim, caller.TokenId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim(IraPhoneAuthentication.LabelClaim, caller.Label),
            ],
            IraPhoneAuthentication.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), IraPhoneAuthentication.Scheme));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"ira\"";
        await Response.WriteAsJsonAsync(new { error = IraPhoneAuthentication.RefusedText, spoken = IraPhoneAuthentication.RefusedSpoken });
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        await Response.WriteAsJsonAsync(new { error = IraPhoneAuthentication.RefusedText, spoken = IraPhoneAuthentication.RefusedSpoken });
    }
}
