using Omro.Domain.ResolutionTasks;

namespace Omro.Domain.Repositories;

public interface IShadowDecisionRepository
{
    void Add(ShadowDecision shadowDecision);
    Task<bool> ExistsAsync(Guid resolutionTaskId, string matchedRuleId, CancellationToken cancellationToken = default);
}
