using System.Collections.Concurrent;
using System.Globalization;
using AlgoTrading.Api.Services.AiVoice;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiPhone;

/// <summary>
/// How a phone question ended, for the Shortcut: the full answer, the short one Siri says, and its call.
/// </summary>
/// <param name="Status">200, or the refusal's (400, 409 the agent off, 429 the rate limit, 502 no model answered, 503 no model can be asked).</param>
/// <param name="ActionId">The action the answer proposed, waiting for the owner's PIN in the Ira app; null for none.</param>
public sealed record IraPhoneAnswer(int Status, string Text, string Spoken, long? CallId, long? ActionId = null, string? Error = null, int RetryAfterSeconds = 0);

/// <summary>
/// A phone's last turns with the agent it asked, for half an hour, as Telegram keeps a chat's: "and BANKNIFTY?" asked a
/// minute after "How is NIFTY doing?" is answered knowing the first. Kept in memory, so an API restart starts over.
/// </summary>
public sealed class IraPhoneConversations(TimeProvider? time = null)
{
    /// <summary>Every phone conversation's id starts with this (<c>ph-&lt;token&gt;-&lt;random&gt;</c>): the Ira app finds the actions proposed in one by it.</summary>
    public const string Prefix = "ph-";

    /// <summary>Messages carried into the next question at most: the last four questions and answers.</summary>
    public const int HistoryMessages = 8;

    /// <summary>After this long without a question, the next one starts a new conversation.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<long, Conversation> _byToken = new();

    private sealed record Conversation(string Agent, string Id, IReadOnlyList<AiMessage> Turns, DateTime LastUtc);

    /// <summary>The conversation a question from this token to <paramref name="agent"/> belongs to, and its last turns.</summary>
    public (string ConversationId, IReadOnlyList<AiMessage> Turns) Current(long tokenId, string agent)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (_byToken.TryGetValue(tokenId, out var c) && c.Agent == agent && now - c.LastUtc < Idle) return (c.Id, c.Turns);

        var fresh = new Conversation(agent, NewId(tokenId), [], now);
        _byToken[tokenId] = fresh;
        return (fresh.Id, fresh.Turns);
    }

    /// <summary>A question answered: kept, with its answer, for the next one.</summary>
    public void Answered(long tokenId, string agent, string conversationId, string question, string answer)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        _byToken.AddOrUpdate(
            tokenId,
            _ => new Conversation(agent, conversationId, [new AiMessage("user", question), new AiMessage("assistant", answer)], now),
            (_, c) => c.Id != conversationId
                ? c
                : c with
                {
                    Turns = c.Turns.Append(new AiMessage("user", question)).Append(new AiMessage("assistant", answer)).TakeLast(HistoryMessages).ToList(),
                    LastUtc = now,
                });
    }

    /// <summary>A token revoked: its conversation goes with it.</summary>
    public void Forget(long tokenId) => _byToken.TryRemove(tokenId, out _);

    private static string NewId(long tokenId) =>
        $"{Prefix}{tokenId.ToString(CultureInfo.InvariantCulture)}-{Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(5))}";
}

/// <summary>
/// "Hey Siri, Ira": a question from the owner's phone (an Apple Shortcut's dictated text) asked through the same gateway
/// as a console question, as the token's owner, with source <c>phone</c>: the same agent switch, rate limit, chain,
/// tools, memories and Calls row. The answer comes back whole, with a short version for Siri to say.
/// </summary>
/// <remarks>
/// The Desk Assistant may propose an action from the phone, as from the console (<c>phone</c> is a chat source): it is
/// stored as a console action waiting for the PIN, and the spoken answer says so. Nothing is carried out here; the PIN
/// is typed in the Ira app (/ira), whose confirm card is the console's.
/// </remarks>
public sealed class IraPhoneAsk(
    AiGateway gateway,
    TradingDbContext db,
    IraPhoneConversations conversations,
    IOptionsMonitor<AiSettings> settings,
    ILogger<IraPhoneAsk> logger)
{
    private const int MaxTokens = 4096;
    private const double Temperature = 0.2;

    /// <summary>What replaces "Full details are in the text above.": on a phone there is no text above.</summary>
    public const string DetailsOnConsole = "The full answer is on the console, on the AI Calls page.";

    public async Task<IraPhoneAnswer> AskAsync(IraPhoneCaller caller, string question, string agentKey, IAiStreamSink sink, CancellationToken cancellationToken)
    {
        var s = settings.CurrentValue;
        var (conversation, turns) = conversations.Current(caller.TokenId, agentKey);
        var input = new AiAskInput(
            agentKey,
            null,
            turns.Append(new AiMessage("user", question)).ToList(),
            null,
            MaxTokens,
            Temperature,
            conversation,
            AiCatalog.PhoneSource,
            caller.UserName,
            caller.UserId,
            PromptNote: PromptNote(await NameAsync(s, cancellationToken)));

        var result = await gateway.AskAsync(input, sink, cancellationToken);
        string agentName = AiCatalog.AgentName(agentKey);

        if (result.RefusalStatus is int status)
        {
            logger.LogInformation("Ira phone: {User}'s question from {Label} (token #{TokenId}) to the {Agent} was refused ({Status}): {Error}",
                caller.UserName, caller.Label, caller.TokenId, agentName, status, result.Error);
            return new IraPhoneAnswer(status, string.Empty, RefusedSpoken(status, agentName, result.RetryAfterSeconds), result.CallId > 0 ? result.CallId : null,
                Error: result.Error, RetryAfterSeconds: result.RetryAfterSeconds);
        }

        if (result.Outcome != AiCallOutcome.Ok)
        {
            logger.LogInformation("Ira phone: {User}'s question from {Label} (token #{TokenId}): call {CallId} {Outcome}: {Error}",
                caller.UserName, caller.Label, caller.TokenId, result.CallId, result.Outcome, result.Error);
            return new IraPhoneAnswer(StatusCodes.Status502BadGateway, result.Text,
                "I could not get an answer just now: no model answered. Try again in a minute.", result.CallId, Error: result.Error);
        }

        conversations.Answered(caller.TokenId, agentKey, conversation, question, result.Text);

        // The proposal tool stored it against this very call; a second proposal in the same answer replaced the first.
        var proposed = await db.AiActions.AsNoTracking()
            .Where(a => a.CallId == result.CallId && a.Status == AiActionStatus.Pending)
            .OrderByDescending(a => a.Id)
            .Select(a => new { a.Id, a.Summary })
            .FirstOrDefaultAsync(cancellationToken);

        string spoken = proposed is not null
            ? ProposedSpoken(proposed.Summary)
            : AnswerSpoken(result.Text, s.Voice.MaxSpokenChars);

        logger.LogInformation("Ira phone: {User} asked the {Agent} from {Label} (token #{TokenId}): call {CallId}, {Seconds:0.0} s{Proposed}",
            caller.UserName, agentName, caller.Label, caller.TokenId, result.CallId, result.Seconds,
            proposed is null ? string.Empty : $", proposed action #{proposed.Id}");
        return new IraPhoneAnswer(StatusCodes.Status200OK, result.Text, spoken, result.CallId, proposed?.Id);
    }

    /// <summary>The one line a phone question's prompt gets: who is listening, and how an action is confirmed there.</summary>
    public static string PromptNote(string? name) =>
        "The owner asked this from his phone by voice (Siri dictation, written down by machine, so a name may be misheard). " +
        "He hears a short spoken version of your answer: lead with the answer in one or two plain sentences, then any detail. " +
        "An action you propose is confirmed with his PIN in the Ira app on his phone." +
        (string.IsNullOrWhiteSpace(name) ? string.Empty : $" He may call you {name.Trim()}.");

    /// <summary>
    /// What Siri says when the answer proposed an action: the summary code wrote, and where to confirm it. A menu path's
    /// arrow ("AI → Agents") is said as the two words.
    /// </summary>
    public static string ProposedSpoken(string summary)
    {
        string said = string.Join(' ', summary.Replace("→", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' ');
        return $"I have proposed: {said}. Confirm it with your PIN in the Ira app.";
    }

    /// <summary>The answer as Siri says it: <see cref="VoiceText.Spoken"/>, pointing at the console rather than "the text above".</summary>
    public static string AnswerSpoken(string answer, int maxChars)
    {
        string spoken = VoiceText.Spoken(answer, maxChars);
        bool details = spoken.EndsWith(VoiceText.DetailsAbove, StringComparison.Ordinal);
        string said = details ? spoken[..^VoiceText.DetailsAbove.Length].TrimEnd() : spoken;
        if (said.Length == 0) return "The answer is all table or code, nothing to say aloud. " + DetailsOnConsole;
        return details ? $"{said} {DetailsOnConsole}" : said;
    }

    /// <summary>A refusal as Siri says it, with the wait when there is one.</summary>
    public static string RefusedSpoken(int status, string agentName, int retryAfterSeconds) => status switch
    {
        StatusCodes.Status409Conflict => $"I cannot ask the {agentName} right now: it is switched off on the console's AI page.",
        StatusCodes.Status429TooManyRequests => $"That is a lot of questions in a short time. Try again in {WaitWords(retryAfterSeconds)}.",
        StatusCodes.Status503ServiceUnavailable => "No model can be asked right now. Try again later.",
        _ => "I could not ask that right now.",
    };

    /// <summary>"40 seconds", "a minute", "3 minutes".</summary>
    public static string WaitWords(int seconds)
    {
        if (seconds <= 0) return "a moment";
        if (seconds < 60) return $"{seconds} seconds";
        int minutes = (int)Math.Ceiling(seconds / 60.0);
        return minutes == 1 ? "a minute" : $"{minutes} minutes";
    }

    /// <summary>The assistant's name as the owner set it (/name on Telegram), else the desk's setting.</summary>
    private async Task<string> NameAsync(AiSettings s, CancellationToken cancellationToken)
    {
        string? saved = await db.SystemSettings.AsNoTracking()
            .Where(x => x.Key == AiSettingsStore.VoiceNameKey)
            .Select(x => x.Value)
            .FirstOrDefaultAsync(cancellationToken);
        return VoiceText.IsName(saved) ? saved!.Trim() : s.Voice.Name;
    }
}
