using Omro.Domain.Tenants;

namespace Omro.Domain.Repositories;

public interface ITenantConfigRepository
{
    Task<TenantConfig?> GetWithCatalogAndActivePoliciesAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(Guid tenantId, CancellationToken cancellationToken = default);
    void Add(TenantConfig tenantConfig);
}
