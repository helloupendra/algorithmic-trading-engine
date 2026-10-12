using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services.AiPhone;
using AlgoTrading.Infrastructure.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// "Hey Siri, Ira": the one endpoint a phone token opens. An Apple Shortcut sends the owner's dictated question and
/// speaks <c>spoken</c> back.
/// </summary>
/// <remarks>
/// <para>
/// Authenticated by the phone token alone (<see cref="AuthorizationPolicies.IraPhone"/>): the console's JWT does not
/// open it, and the phone token opens nothing else (<see cref="IraPhoneAuthentication"/>). The token's owner must be an
/// active admin when it is used, as every AI endpoint requires.
/// </para>
/// <para>
/// Not an <c>[ApiController]</c>: a body the binder cannot read reaches the action as null, and is answered here with a
/// <c>spoken</c> line like every other refusal, so Siri says what went wrong instead of reading out a problem document.
/// </para>
/// </remarks>
[Authorize(Policy = AuthorizationPolicies.IraPhone)]
[Route("api/Ira")]
public sealed class IraPhoneController(IraPhoneAsk ask, IOptionsMonitor<AiSettings> settings) : ControllerBase
{
    /// <summary>
    /// Asks <c>agent</c> (the Desk Assistant by default) <c>text</c> as the token's owner, through the gateway with source
    /// <c>phone</c>, and answers <c>{ text, spoken, callId }</c>: the full answer, the short one for Siri (no tables,
    /// links or citations; an action proposed is "I have proposed: …. Confirm it with your PIN in the Ira app."), and its
    /// call on the Calls tab. Nothing is carried out from here. Refusals carry <c>error</c> and <c>spoken</c>: 400 no
    /// text or an unknown agent, 409 the agent off, 429 the AI rate limit (Retry-After), 503 no model or phone asks off.
    /// A call that starts is answered 200 and kept alive with spaces until its JSON (<see cref="IraPhoneResponse"/>).
    /// </summary>
    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] IraAskRequest? body, CancellationToken cancellationToken)
    {
        var s = settings.CurrentValue.Phone;
        if (!s.Enabled)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = IraPhoneTokens.OffText, spoken = "Ira on the phone is switched off on the desk." });
        }

        string text = body?.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return BadRequest(new { error = "Send the question as JSON: { \"text\": \"…\" }.", spoken = "I did not get a question. Try again." });
        }

        if (text.Length > s.MaxQuestionChars)
        {
            return BadRequest(new { error = $"The question is over {s.MaxQuestionChars:N0} characters.", spoken = "That question is too long for me. Ask a shorter one." });
        }

        string agent = string.IsNullOrWhiteSpace(body!.Agent) ? AiCatalog.DeskAssistant : body.Agent.Trim();
        if (AiCatalog.Agent(agent) is not { } def || !AiCatalog.CanChat(def))
        {
            return BadRequest(new { error = $"No agent {agent} to talk with.", spoken = "I do not know that agent. Ask the Desk Assistant instead." });
        }

        var caller = new IraPhoneCaller(
            User.PhoneTokenId() ?? 0,
            User.FindFirst(IraPhoneAuthentication.LabelClaim)?.Value ?? string.Empty,
            User.GetRequiredUserId(),
            User.GetUserName() ?? string.Empty);

        await using var stream = new IraPhoneResponse(Response, cancellationToken);
        IraPhoneAnswer answer;
        try
        {
            answer = await ask.AskAsync(caller, text, agent, stream, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EmptyResult();
        }

        var payload = new IraAskAnswer(answer.Text, answer.Spoken, answer.CallId, answer.ActionId, answer.Error, answer.RetryAfterSeconds > 0 ? answer.RetryAfterSeconds : null);
        if (!stream.Started)
        {
            if (answer.Status == StatusCodes.Status429TooManyRequests && answer.RetryAfterSeconds > 0)
            {
                Response.Headers.RetryAfter = answer.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return StatusCode(answer.Status, payload);
        }

        if (cancellationToken.IsCancellationRequested) return new EmptyResult();
        try
        {
            await stream.FinishAsync(payload);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The phone left after the answer was recorded: it is on the Calls tab.
        }

        return new EmptyResult();
    }
}

/// <summary>A phone's question: the dictated text, and the agent to ask (the Desk Assistant when left out).</summary>
public sealed record IraAskRequest(string? Text, string? Agent);

/// <summary>
/// The answer for the Shortcut: the full <c>text</c>, the short <c>spoken</c> line Siri says, and the <c>callId</c> on
/// the Calls tab; <c>actionId</c> when it proposed an action, <c>error</c> when it was refused or failed.
/// </summary>
public sealed record IraAskAnswer(string Text, string Spoken, long? CallId, long? ActionId, string? Error, int? RetryAfterSeconds);
