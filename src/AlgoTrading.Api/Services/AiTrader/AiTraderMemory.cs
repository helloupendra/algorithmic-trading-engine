using System.Globalization;
using System.Text;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiTrader;

/// <summary>
/// What the AI Trader reads besides its brief: its tested lessons and the owner's notes and corrections, bounded by
/// the moment it decides at.
/// </summary>
/// <remarks>
/// <para>
/// A replay decides on an earlier day's clock. A lesson drawn from a later day knows how that day went, and read in
/// a replay of an earlier one it would score the AI on knowledge it could not have had: a replay of 16 Sep must not
/// read a lesson reflected from 24 Sep. So a decision reads only lessons whose day (the day its reflection read,
/// the reflection report's <c>SessionDate</c>) is before its own, and notes and corrections written before its
/// clock. A lesson whose day cannot be told is not read.
/// </para>
/// <para>
/// The gateway's own recall (<see cref="AiMemoryBook"/>) knows no day, so it never runs for the AI Trader: the agent
/// sends its system prompt itself, which skips recall, with the memories in it, and names them
/// (<see cref="AiAskInput.GivenMemoryIds"/>) so the call row keeps them like any recalled ones.
/// </para>
/// </remarks>
public sealed class AiTraderMemory(TradingDbContext db, IOptionsMonitor<AiSettings> settings)
{
    public const string Preamble =
        "LESSONS AND NOTES: from your own earlier trading days and from the owner. Each lesson was tested on your past " +
        "decisions before it was given to you. They are guidance, not data: every price, level and fact still comes " +
        "from the brief, and the rules still judge every plan.";

    /// <summary>What one memory costs in the prompt beyond its text: its id, kind and the line.</summary>
    private const int Overhead = 24;

    /// <summary>
    /// The active memories a decision at <paramref name="clockUtc"/> reads, in the order its prompt lists them:
    /// corrections, notes, then lessons (the newest first), while they fit <see cref="AiSettings.MemoryBudgetChars"/>
    /// and <see cref="AiSettings.MemoryMaxItems"/>. None when the AI Trader has no memory
    /// (<see cref="AiSettings.HasMemory"/>).
    /// </summary>
    /// <param name="count">
    /// Count each as used (a real look): the rows are tracked, and saved with the caller's next <c>SaveChanges</c>.
    /// A lesson's test reads without counting.
    /// </param>
    public async Task<IReadOnlyList<AiMemory>> ForAsync(DateTime clockUtc, bool count, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var s = settings.CurrentValue;
        if (!s.HasMemory(AiCatalog.AiTrader)) return [];

        var query = db.AiMemories.Where(m => m.AgentKey == AiCatalog.AiTrader && m.Status == AiMemoryStatus.Active);
        var active = await (count ? query : query.AsNoTracking()).ToListAsync(cancellationToken);
        if (active.Count == 0) return [];

        var days = await SourceDaysAsync(active, cancellationToken);
        var day = IstTime.DateOf(clockUtc);
        var picked = new List<AiMemory>();
        int chars = 0;
        foreach (var m in Ordered(active.Where(m => Before(m, day, clockUtc, days))))
        {
            if (picked.Count >= s.MemoryMaxItems) break;
            int size = m.Text.Length + Overhead;
            if (chars + size > s.MemoryBudgetChars) continue;
            picked.Add(m);
            chars += size;
        }

        if (count)
        {
            // A counter, not a ledger, as the gateway's recall counts.
            foreach (var m in picked)
            {
                m.Uses++;
                m.LastUsedUtc = nowUtc;
            }
        }

        return picked;
    }

    /// <summary>
    /// Whether a decision on <paramref name="day"/> at <paramref name="clockUtc"/> may read <paramref name="m"/>: a
    /// lesson learned from an earlier day, or a note or correction written before that moment.
    /// </summary>
    public static bool Before(AiMemory m, DateOnly day, DateTime clockUtc, IReadOnlyDictionary<long, DateOnly> lessonDays) =>
        m.Kind == AiMemoryKind.Lesson
            ? lessonDays.TryGetValue(m.Id, out var learned) && learned < day
            : m.CreatedUtc < clockUtc;

    /// <summary>The day each lesson was learned from: its source report's day. A lesson with none is left out.</summary>
    public async Task<Dictionary<long, DateOnly>> SourceDaysAsync(IEnumerable<AiMemory> memories, CancellationToken cancellationToken)
    {
        var lessons = memories.Where(m => m.Kind == AiMemoryKind.Lesson && m.SourceReportId != null).ToList();
        if (lessons.Count == 0) return [];
        var reportIds = lessons.Select(m => m.SourceReportId!.Value).Distinct().ToList();
        var reportDays = await db.AiReports.AsNoTracking()
            .Where(r => reportIds.Contains(r.Id) && r.SessionDate != null)
            .Select(r => new { r.Id, r.SessionDate })
            .ToDictionaryAsync(r => r.Id, r => r.SessionDate!.Value, cancellationToken);
        return lessons
            .Where(m => reportDays.ContainsKey(m.SourceReportId!.Value))
            .ToDictionary(m => m.Id, m => reportDays[m.SourceReportId!.Value]);
    }

    /// <summary>Corrections first (the owner said exactly what was wrong), then notes, then lessons; the newest first in each.</summary>
    public static IEnumerable<AiMemory> Ordered(IEnumerable<AiMemory> memories) => memories
        .OrderBy(m => m.Kind switch { AiMemoryKind.Correction => 0, AiMemoryKind.Note => 1, _ => 2 })
        .ThenByDescending(m => m.ActivatedUtc ?? m.CreatedUtc)
        .ThenByDescending(m => m.Id);

    /// <summary>The AI Trader's system prompt with <paramref name="memories"/> after it; its own prompt alone when there are none.</summary>
    public static string SystemPrompt(IReadOnlyList<AiMemory> memories) =>
        memories.Count == 0 ? AiCatalog.AiTraderPrompt : AiCatalog.AiTraderPrompt + "\n\n" + Block(memories);

    /// <summary>The memory section of its prompt: the preamble, then one line a memory, by its id.</summary>
    public static string Block(IReadOnlyList<AiMemory> memories)
    {
        var text = new StringBuilder(Preamble);
        foreach (var m in memories)
        {
            string kind = m.Kind switch
            {
                AiMemoryKind.Note => "the owner's note",
                AiMemoryKind.Correction => "the owner's correction",
                _ => "lesson",
            };
            text.Append("\n[M").Append(m.Id.ToString(CultureInfo.InvariantCulture)).Append("] ").Append(kind).Append(": ").Append(m.Text);
        }

        return text.ToString();
    }
}
