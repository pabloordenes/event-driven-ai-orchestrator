using Microsoft.EntityFrameworkCore;
using Omro.Domain.Repositories;
using Omro.Domain.ResolutionTasks;

namespace Omro.Infrastructure.Persistence.Repositories;

public class ResolutionTaskRepository : IResolutionTaskRepository
{
    private readonly ApplicationDbContext _dbContext;

    public ResolutionTaskRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public void Add(ResolutionTask task)
    {
        _dbContext.ResolutionTasks.Add(task);
    }

    public async Task<ResolutionTask?> GetByKeyAsync(
        Guid tenantId,
        Guid resourceId,
        string anomalyType,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.ResolutionTasks
            .FirstOrDefaultAsync(
                t => t.TenantId == tenantId &&
                     t.Anomaly.ResourceId == resourceId &&
                     t.Anomaly.AnomalyType == anomalyType,
                cancellationToken);
    }
}
