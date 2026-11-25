using MassTransit;
using Omro.Contracts.Plans;

namespace Omro.Contracts.Events;

[MessageUrn("omro:resolution-approved")]
public record ResolutionApproved(
    Guid TenantId,
    Guid CorrelationId,
    Guid ResolutionTaskId,
    Guid ResourceId,
    string? AiPlanJson = null,
    string SchemaVersion = PlanContractVersions.V1Legacy,
    StructuredAiPlan? AiPlan = null
);
