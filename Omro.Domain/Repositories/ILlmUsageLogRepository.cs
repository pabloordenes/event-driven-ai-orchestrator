using Omro.Domain.Cognitive;

namespace Omro.Domain.Repositories;

public interface ILlmUsageLogRepository
{
    void Add(LlmUsageLog entry);

    Task<int> GetTenantTokensUsedSinceAsync(
        Guid tenantId,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken = default);
}
