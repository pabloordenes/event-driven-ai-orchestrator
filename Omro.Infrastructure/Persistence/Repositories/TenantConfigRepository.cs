using Microsoft.EntityFrameworkCore;
using Omro.Domain.Repositories;
using Omro.Domain.Tenants;

namespace Omro.Infrastructure.Persistence.Repositories;

public class TenantConfigRepository : ITenantConfigRepository
{
    private readonly ApplicationDbContext _dbContext;

    public TenantConfigRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<TenantConfig?> GetWithCatalogAndActivePoliciesAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.TenantConfigs
            .AsNoTracking()
            .Include(t => t.Actions)
            .Include(t => t.Policies)
            .FirstOrDefaultAsync(t => t.TenantId == tenantId, cancellationToken);
    }

    public async Task<bool> ExistsAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.TenantConfigs
            .AsNoTracking()
            .AnyAsync(t => t.TenantId == tenantId, cancellationToken);
    }

    public void Add(TenantConfig tenantConfig)
    {
        _dbContext.TenantConfigs.Add(tenantConfig);
    }
}
