using Omro.Application.Policies;
using Omro.Application.Tenants;
using Omro.Domain.ResolutionTasks;

namespace Omro.Application.Interfaces;

public sealed record DeterministicPlanBuildResult(
    bool IsSuccess,
    CognitivePlan? Plan,
    string? ErrorReason);

public interface IDeterministicPlanBuilder
{
    DeterministicPlanBuildResult BuildBasePlan(
        TenantConfiguration tenant,
        AnomalyEvent anomaly,
        IReadOnlyDictionary<string, string>? attributes,
        PolicyDecision decision,
        Guid omroTaskId);

    CognitivePlan MergeLlmEnrichment(
        TenantConfiguration tenant,
        PolicyDecision decision,
        CognitivePlan basePlan,
        CognitivePlan llmPlan);
}
