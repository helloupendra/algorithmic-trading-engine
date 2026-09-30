using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Api.Services.AgentMemory;

/// <summary>Tells the owner that lessons are waiting for approval (the Telegram bot does, with Approve and Reject buttons).</summary>
public interface IAiLessonNotifier
{
    Task LessonsProposedAsync(IReadOnlyList<AiMemory> lessons, CancellationToken cancellationToken);
}
