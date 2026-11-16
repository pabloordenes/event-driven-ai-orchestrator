using MassTransit;

namespace Omro.Contracts.Events;

[MessageUrn("omro:resolution-approved")]
public record ResolutionApproved(
    Guid TenantId,
    Guid CorrelationId,
    Guid ResolutionTaskId,
    Guid ResourceId,
    string AiPlanJson
);
