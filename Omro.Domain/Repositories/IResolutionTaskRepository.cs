using Omro.Domain.ResolutionTasks;

namespace Omro.Domain.Repositories;

public interface IResolutionTaskRepository
{
    void Add(ResolutionTask task);
    Task<ResolutionTask?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ResolutionTask?> GetByKeyAsync(Guid tenantId, Guid resourceId, string anomalyType, CancellationToken cancellationToken = default);
}
