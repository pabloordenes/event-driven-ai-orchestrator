using Microsoft.EntityFrameworkCore;
using Omro.Domain.Repositories;
using Omro.Domain.ResolutionTasks;

namespace Omro.Infrastructure.Persistence.Repositories;

public class ActionExecutionRepository : IActionExecutionRepository
{
    private readonly ApplicationDbContext _dbContext;

    public ActionExecutionRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public void AddRange(IEnumerable<ActionExecution> executions)
    {
        _dbContext.ActionExecutions.AddRange(executions);
    }

    public async Task<List<ActionExecution>> GetByTaskAndPhaseAsync(
        Guid resolutionTaskId,
        ActionExecutionPhase phase,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.ActionExecutions
            .Where(a => a.ResolutionTaskId == resolutionTaskId && a.Phase == phase)
            .OrderBy(a => a.ActionIndex)
            .ToListAsync(cancellationToken);
    }
}
