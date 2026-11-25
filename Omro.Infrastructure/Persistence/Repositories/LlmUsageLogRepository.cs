using Microsoft.EntityFrameworkCore;
using Omro.Domain.Cognitive;
using Omro.Domain.Repositories;

namespace Omro.Infrastructure.Persistence.Repositories;

public class LlmUsageLogRepository : ILlmUsageLogRepository
{
    private readonly ApplicationDbContext _dbContext;

    public LlmUsageLogRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public void Add(LlmUsageLog entry)
    {
        _dbContext.LlmUsageLogs.Add(entry);
    }

    public async Task<int> GetTenantTokensUsedSinceAsync(
        Guid tenantId,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.LlmUsageLogs
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.RecordedAt >= sinceUtc)
            .SumAsync(x => (int?)x.TotalTokens, cancellationToken) ?? 0;
    }
}
