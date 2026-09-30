using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AlgoTrading.Api.Services.AiTools;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AgentMemory;

/// <summary>A request the memory service refuses, in words for the owner; <see cref="Status"/> is the HTTP status that fits.</summary>
public sealed class AiMemoryException(string message, int status = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Why a memory needs the owner's eye, from the answers it was part of; null when it does not.</summary>
public sealed record AiMemoryReview(bool Review, string? Reason);

/// <summary>One day of the memory's effect: the daily check's score, the memories active that night, the owner's verdicts.</summary>
public sealed record AiMemoryDay(string Date, int? CheckPassed, int? CheckTotal, double? Score, int ActiveMemories, int Ups, int Downs);

/// <summary>
/// How the agents' memories are written and changed: the owner's notes,
/// the owner's verdicts on answers, lessons from the daily check, and the
/// owner's decisions on them. The console, the Telegram bot and the check all
/// go through here, so the rules are one set.
/// </summary>
/// <remarks>
/// <para>
/// A note (<c>/remember</c>, Add a note) and a correction (👎 with what it
/// should have said) are the owner's own words and are active at once. A
/// lesson the check proposes waits for the owner (owner, 1 Oct): a wrong
/// lesson in the prompt would repeat its mistake on every answer.
/// </para>
/// <para>
/// Texts are masked like the tools' output before they are kept (a secret's
/// shape, the server's name, addresses, home paths): a memory is sent to the
/// provider on every answer.
/// </para>
/// </remarks>
public sealed class AiMemoryService(
    TradingDbContext db,
    AiMemoryBook book,
    IOptionsMonitor<AiSettings> settings,
    TimeProvider? time = null)
{
    /// <summary>Lessons a rejected one blocks from being proposed again for the same kind of question.</summary>
    public static readonly TimeSpan RejectedBlocksFor = TimeSpan.FromDays(14);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>The agents that have memory, in the catalog's order.</summary>
    public IReadOnlyList<AiAgentDef> Agents() =>
        AiCatalog.Agents.Where(a => settings.CurrentValue.HasMemory(a.Key)).ToList();

    /// <summary>A note the owner wrote: active at once.</summary>
    public async Task<AiMemory> RememberAsync(string? agentKey, string? text, string by, string via, CancellationToken cancellationToken)
    {
        string agent = AgentWithMemory(agentKey);
        var memory = new AiMemory
        {
            AgentKey = agent,
            Kind = AiMemoryKind.Note,
            Status = AiMemoryStatus.Active,
            Text = CleanText(text),
            Source = AiMemorySource.Owner,
            Via = via,
            CreatedBy = by,
            CreatedUtc = Now,
            UpdatedUtc = Now,
            ActivatedUtc = Now,
            DecidedBy = by,
            DecidedUtc = Now,
        };
        db.AiMemories.Add(memory);
        await db.SaveChangesAsync(cancellationToken);
        await book.EmbedAsync(memory, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return memory;
    }

    /// <summary>
    /// The owner's verdict on an answer: 1, -1, or 0 to take it back. It is
    /// counted on every memory the answer was given, and a correction with a
    /// 👎 becomes an active memory (one per answer: a second one replaces it).
    /// </summary>
    public async Task<(AiCall Call, AiMemory? Memory)> FeedbackAsync(
        long callId, int score, string? correction, string by, string via, CancellationToken cancellationToken)
    {
        if (score is not (1 or -1 or 0)) throw new AiMemoryException("score is 1, -1 or 0.");
        bool correcting = !string.IsNullOrWhiteSpace(correction);
        if (correcting && score != -1) throw new AiMemoryException("A correction goes with a thumbs down (score -1).");

        var call = await db.AiCalls.FirstOrDefaultAsync(c => c.Id == callId, cancellationToken)
            ?? throw new AiMemoryException($"No call {callId}.", StatusCodes.Status404NotFound);
        if (call.Outcome != AiCallOutcome.Ok) throw new AiMemoryException($"Call {callId} was not answered ({call.Outcome}).", StatusCodes.Status409Conflict);

        int before = call.FeedbackScore ?? 0;
        if (before != score)
        {
            var ids = AiGateway.MemoryIds(call.MemoryIdsJson);
            if (ids.Count > 0)
            {
                foreach (var m in await db.AiMemories.Where(m => ids.Contains(m.Id)).ToListAsync(cancellationToken))
                {
                    if (before == 1) m.Ups = Math.Max(0, m.Ups - 1);
                    if (before == -1) m.Downs = Math.Max(0, m.Downs - 1);
                    if (score == 1) m.Ups++;
                    if (score == -1) m.Downs++;
                }
            }
        }

        call.FeedbackScore = score == 0 ? null : score;
        call.FeedbackBy = score == 0 ? string.Empty : Cut(by, 100);
        call.FeedbackUtc = score == 0 ? null : Now;
        if (score != -1) call.FeedbackNote = string.Empty;

        AiMemory? memory = null;
        if (correcting)
        {
            string agent = AgentWithMemory(call.AgentKey);
            string text = CleanText(correction);
            call.FeedbackNote = text;
            memory = await db.AiMemories.FirstOrDefaultAsync(
                m => m.SourceCallId == callId && m.Kind == AiMemoryKind.Correction, cancellationToken);
            if (memory is null)
            {
                memory = new AiMemory
                {
                    AgentKey = agent,
                    Kind = AiMemoryKind.Correction,
                    Status = AiMemoryStatus.Active,
                    Context = ContextOf(call.Summary),
                    Source = AiMemorySource.Feedback,
                    Via = via,
                    SourceCallId = callId,
                    CreatedBy = by,
                    CreatedUtc = Now,
                    ActivatedUtc = Now,
                    DecidedBy = by,
                    DecidedUtc = Now,
                };
                db.AiMemories.Add(memory);
            }
            else if (memory.Status != AiMemoryStatus.Active)
            {
                Activate(memory, by);
            }

            memory.Text = text;
            memory.UpdatedUtc = Now;
            await db.SaveChangesAsync(cancellationToken);
            await book.EmbedAsync(memory, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return (call, memory);
    }

    /// <summary>Edits a memory's text or moves it: approve or restore (active), reject, retire.</summary>
    public async Task<AiMemory> UpdateAsync(long id, string? text, string? status, string by, CancellationToken cancellationToken)
    {
        var memory = await db.AiMemories.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            ?? throw new AiMemoryException($"No memory M{id}.", StatusCodes.Status404NotFound);

        bool reembed = false;
        if (text is not null)
        {
            string clean = CleanText(text);
            if (clean != memory.Text)
            {
                memory.Text = clean;
                reembed = true;
            }
        }

        if (status is not null && status != memory.Status)
        {
            switch (status, memory.Status)
            {
                case (AiMemoryStatus.Active, _):
                    Activate(memory, by);
                    break;
                case (AiMemoryStatus.Rejected, AiMemoryStatus.Proposed):
                    memory.Status = AiMemoryStatus.Rejected;
                    memory.DecidedBy = Cut(by, 100);
                    memory.DecidedUtc = Now;
                    break;
                case (AiMemoryStatus.Retired, AiMemoryStatus.Active):
                    memory.Status = AiMemoryStatus.Retired;
                    memory.DecidedBy = Cut(by, 100);
                    memory.DecidedUtc = Now;
                    memory.RetiredUtc = Now;
                    break;
                default:
                    throw new AiMemoryException(status is AiMemoryStatus.Rejected or AiMemoryStatus.Retired or AiMemoryStatus.Active
                        ? $"M{id} is {memory.Status}: only a proposed memory is rejected, and only an active one retired."
                        : "status is active, rejected or retired.");
            }
        }

        memory.UpdatedUtc = Now;
        await db.SaveChangesAsync(cancellationToken);
        if (reembed || (memory.Status == AiMemoryStatus.Active && memory.EmbeddingModel != AiCatalog.EmbeddingModel))
        {
            await book.EmbedAsync(memory, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        return memory;
    }

    /// <summary>
    /// A lesson an agent drew from a question it got wrong, waiting for the
    /// owner. Null when one is already waiting or active for the same kind of
    /// question, or the owner turned one down lately.
    /// </summary>
    public async Task<AiMemory?> ProposeAsync(
        string agentKey, string lesson, string question, long? sourceCallId, long? sourceReportId, CancellationToken cancellationToken)
    {
        string agent = AgentWithMemory(agentKey);
        string text = CleanText(lesson);
        string context = ContextOf(question);
        if (await LessonBlockedAsync(agent, context, cancellationToken)) return null;

        var memory = new AiMemory
        {
            AgentKey = agent,
            Kind = AiMemoryKind.Lesson,
            Status = AiMemoryStatus.Proposed,
            Text = text,
            Context = context,
            Source = AiMemorySource.Check,
            Via = "check",
            SourceCallId = sourceCallId,
            SourceReportId = sourceReportId,
            CreatedBy = AiCatalog.AssistantCheck,
            CreatedUtc = Now,
            UpdatedUtc = Now,
        };
        db.AiMemories.Add(memory);
        await db.SaveChangesAsync(cancellationToken);
        return memory;
    }

    /// <summary>
    /// Whether a lesson for this kind of question is already waiting or active,
    /// or one was turned down in the last <see cref="RejectedBlocksFor"/>: then
    /// the Judge is not asked for another.
    /// </summary>
    public async Task<bool> LessonBlockedAsync(string agentKey, string question, CancellationToken cancellationToken)
    {
        string shape = Shape(ContextOf(question));
        var since = Now - RejectedBlocksFor;
        var contexts = await db.AiMemories.AsNoTracking()
            .Where(m => m.AgentKey == agentKey && m.Kind == AiMemoryKind.Lesson
                && (m.Status == AiMemoryStatus.Proposed || m.Status == AiMemoryStatus.Active
                    || (m.Status == AiMemoryStatus.Rejected && m.DecidedUtc >= since)))
            .Select(m => m.Context)
            .ToListAsync(cancellationToken);
        return contexts.Any(c => Shape(c) == shape);
    }

    /// <summary>A check question graded: counted on every memory the answer was given.</summary>
    public async Task CountCheckAsync(long? callId, bool passed, CancellationToken cancellationToken)
    {
        if (callId is not long id) return;
        string? json = await db.AiCalls.AsNoTracking().Where(c => c.Id == id).Select(c => c.MemoryIdsJson).FirstOrDefaultAsync(cancellationToken);
        var ids = AiGateway.MemoryIds(json);
        if (ids.Count == 0) return;
        foreach (var m in await db.AiMemories.Where(m => ids.Contains(m.Id)).ToListAsync(cancellationToken))
        {
            if (passed) m.CheckPasses++;
            else m.CheckFails++;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The owner's eye is wanted when the answers a memory was part of went
    /// badly more often than well: two 👎 and more 👎 than 👍, or three failed
    /// check answers and more failed than passed. Only a flag: the owner decides.
    /// </summary>
    public static AiMemoryReview Review(AiMemory m)
    {
        if (m.Status != AiMemoryStatus.Active) return new AiMemoryReview(false, null);
        if (m.Downs >= 2 && m.Downs > m.Ups)
        {
            return new AiMemoryReview(true, $"{m.Downs} 👎 against {m.Ups} 👍 in answers it was part of.");
        }

        if (m.CheckFails >= 3 && m.CheckFails > m.CheckPasses)
        {
            return new AiMemoryReview(true, $"{m.CheckFails} failed check answers against {m.CheckPasses} passed while it was read.");
        }

        return new AiMemoryReview(false, null);
    }

    /// <summary>
    /// The days of the last <paramref name="days"/> with a check or a verdict,
    /// oldest first: the check's score, the memories active at the day's end,
    /// the owner's 👍 and 👎. Whether memory helps, as numbers.
    /// </summary>
    public async Task<IReadOnlyList<AiMemoryDay>> ProgressAsync(int days, CancellationToken cancellationToken)
    {
        days = Math.Clamp(days, 1, 180);
        var today = IstTime.DateOf(Now);
        var first = today.AddDays(-(days - 1));
        var fromUtc = IstTime.StartOfDayUtc(first);

        var checks = await db.AiReports.AsNoTracking()
            .Where(r => r.AgentKey == AiCatalog.AssistantCheck && r.SubjectType == AiReportSubject.Check && r.CreatedUtc >= fromUtc)
            .Select(r => new { r.SubjectId, r.DataJson })
            .ToListAsync(cancellationToken);
        var verdicts = await db.AiCalls.AsNoTracking()
            .Where(c => c.FeedbackUtc != null && c.FeedbackUtc >= fromUtc)
            .Select(c => new { c.FeedbackUtc, c.FeedbackScore })
            .ToListAsync(cancellationToken);
        var memories = await db.AiMemories.AsNoTracking()
            .Where(m => m.ActivatedUtc != null)
            .Select(m => new { m.Status, m.ActivatedUtc, m.RetiredUtc })
            .ToListAsync(cancellationToken);

        var list = new List<AiMemoryDay>();
        for (var day = first; day <= today; day = day.AddDays(1))
        {
            string key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var check = checks.FirstOrDefault(c => c.SubjectId == key);
            int ups = verdicts.Count(v => v.FeedbackScore == 1 && IstTime.DateOf(v.FeedbackUtc!.Value) == day);
            int downs = verdicts.Count(v => v.FeedbackScore == -1 && IstTime.DateOf(v.FeedbackUtc!.Value) == day);
            if (check is null && ups == 0 && downs == 0) continue;

            int? passed = null, total = null;
            if (check is not null && JsonNode.Parse(check.DataJson) is JsonObject data)
            {
                passed = data["passed"]?.GetValue<int>();
                total = data["total"]?.GetValue<int>();
            }

            var end = IstTime.StartOfDayUtc(day.AddDays(1));
            int active = memories.Count(m => m.ActivatedUtc <= end
                && (m.Status == AiMemoryStatus.Active || (m.Status == AiMemoryStatus.Retired && m.RetiredUtc > end)));
            double? score = passed is int p && total is int t && t > 0 ? Math.Round((double)p / t, 3) : null;
            list.Add(new AiMemoryDay(key, passed, total, score, active, ups, downs));
        }

        return list;
    }

    /// <summary>What the questions of one kind share: the question with its numbers taken out ("run 339's net" and "run 341's net" are one kind).</summary>
    public static string Shape(string question) =>
        Regex.Replace(question.Trim().ToLowerInvariant(), @"\d[\d,.]*", "#");

    private static string ContextOf(string question) =>
        Cut(AiToolFormat.Text(question, AiMemoryBook.MaxContext) ?? string.Empty, AiMemoryBook.MaxContext);

    private void Activate(AiMemory memory, string by)
    {
        memory.Status = AiMemoryStatus.Active;
        memory.ActivatedUtc = Now;
        memory.RetiredUtc = null;
        memory.DecidedBy = Cut(by, 100);
        memory.DecidedUtc = Now;
    }

    private string AgentWithMemory(string? agentKey)
    {
        string agent = string.IsNullOrWhiteSpace(agentKey) ? AiCatalog.DeskAssistant : agentKey.Trim();
        if (!settings.CurrentValue.HasMemory(agent))
        {
            throw new AiMemoryException($"{AiCatalog.AgentName(agent)} has no memory: Ai:MemoryAgents lists the agents that do.");
        }

        return agent;
    }

    /// <summary>The text as it will be kept: masked, whitespace folded, within the limit, or refused.</summary>
    private static string CleanText(string? text)
    {
        string flat = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length == 0) throw new AiMemoryException("The memory is empty: write what the agent should remember.");
        if (flat.Length > AiMemoryBook.MaxText) throw new AiMemoryException($"A memory is at most {AiMemoryBook.MaxText} characters; this is {flat.Length}. Keep it to one rule or fact.");
        return AiToolFormat.Text(flat, AiMemoryBook.MaxText) ?? flat;
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}
