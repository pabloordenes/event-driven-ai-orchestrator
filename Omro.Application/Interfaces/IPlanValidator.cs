using Omro.Application.Policies;
using Omro.Application.Tenants;
using Omro.Application.Validation;
using Omro.Domain.ResolutionTasks;

namespace Omro.Application.Interfaces;

public interface IPlanValidator
{
    PlanValidationResult ValidateImmediatePlan(
        TenantConfiguration tenant,
        PolicyDecision decision,
        CognitivePlan plan);

    PlanValidationResult ValidateDeferredActions(
        TenantConfiguration tenant,
        IReadOnlyList<DeferredActionCandidate> actions);
}
