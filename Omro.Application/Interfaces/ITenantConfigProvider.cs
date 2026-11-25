using Omro.Application.Tenants;

namespace Omro.Application.Interfaces;

public interface ITenantConfigProvider
{
    Task<TenantConfiguration?> GetTenantConfigurationAsync(Guid tenantId, CancellationToken cancellationToken = default);
    void Invalidate(Guid tenantId);
}
