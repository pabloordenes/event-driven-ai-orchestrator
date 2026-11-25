using Microsoft.EntityFrameworkCore;
using Omro.Domain.Repositories;
using Omro.Domain.ResolutionTasks;

namespace Omro.Infrastructure.Persistence.Repositories;

public class ShadowDecisionRepository : IShadowDecisionRepository
{
    private readonly ApplicationDbContext _dbContext;

    public ShadowDecisionRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public void Add(ShadowDecision shadowDecision)
    {
        _dbContext.ShadowDecisions.Add(shadowDecision);
    }

    public async Task<bool> ExistsAsync(
        Guid resolutionTaskId,
        string matchedRuleId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.ShadowDecisions
            .AsNoTracking()
            .AnyAsync(
                x => x.ResolutionTaskId == resolutionTaskId && x.MatchedRuleId == matchedRuleId,
                cancellationToken);
    }
}
