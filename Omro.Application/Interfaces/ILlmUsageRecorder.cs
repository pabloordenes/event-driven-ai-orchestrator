using Omro.Domain.Cognitive;

namespace Omro.Application.Interfaces;

public interface ILlmUsageRecorder
{
    Task RecordAsync(LlmUsageLog entry, CancellationToken cancellationToken = default);

    Task<int> GetTenantDailyTokensAsync(
        Guid tenantId,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);
}
