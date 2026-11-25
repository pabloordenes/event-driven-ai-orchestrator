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

    public async Task<ResolutionTask?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.ResolutionTasks
            .Include(t => t.ActionExecutions)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
    }

    public async Task<ResolutionTask?> GetByKeyAsync(
        Guid tenantId,
        Guid resourceId,
        string anomalyType,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.ResolutionTasks
            .Include(t => t.ActionExecutions)
            .Where(t => t.TenantId == tenantId &&
                        t.Anomaly.ResourceId == resourceId &&
                        t.Anomaly.AnomalyType == anomalyType)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<ResolutionTask?> FindActiveOrWithinWindowAsync(
        Guid tenantId,
        Guid resourceId,
        string anomalyType,
        DateTime? windowStartUtc,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.ResolutionTasks
            .Include(t => t.ActionExecutions)
            .Where(t => t.TenantId == tenantId &&
                        t.Anomaly.ResourceId == resourceId &&
                        t.Anomaly.AnomalyType == anomalyType);

        if (windowStartUtc.HasValue)
        {
            var cutoff = windowStartUtc.Value;
            query = query.Where(t =>
                (t.Status != ResolutionStatus.Resolved && t.Status != ResolutionStatus.Failed) ||
                t.CreatedAt >= cutoff);
        }
        else
        {
            query = query.Where(t =>
                t.Status != ResolutionStatus.Resolved && t.Status != ResolutionStatus.Failed);
        }

        return await query
            .OrderByDescending(t => t.Status != ResolutionStatus.Resolved && t.Status != ResolutionStatus.Failed)
            .ThenByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<ResolutionTask?> GetLatestByTenantAndResourceAsync(
        Guid tenantId,
        Guid resourceId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.ResolutionTasks
            .Include(t => t.ActionExecutions)
            .Where(t => t.TenantId == tenantId && t.Anomaly.ResourceId == resourceId)
            .OrderByDescending(t =>
                t.Status == ResolutionStatus.WaitingForExternalData ||
                t.Status == ResolutionStatus.ActionProposed ||
                t.Status == ResolutionStatus.NeedsHumanReview)
            .ThenByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
