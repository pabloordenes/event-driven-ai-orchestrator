using Omro.Domain.ResolutionTasks;

namespace Omro.Domain.Repositories;

public interface IActionExecutionRepository
{
    void AddRange(IEnumerable<ActionExecution> executions);
    Task<List<ActionExecution>> GetByTaskAndPhaseAsync(
        Guid resolutionTaskId,
        ActionExecutionPhase phase,
        CancellationToken cancellationToken = default);
}
