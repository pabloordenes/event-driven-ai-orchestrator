using System.Text.Json;
using Omro.Application.Tenants;
using Omro.Domain.ResolutionTasks;

namespace Omro.Application.Policies;

public enum PolicyOutcome
{
    ExecuteActions,
    ProposeForApproval,
    NoAction,
    EscalateToHumanReview
}

public sealed record PolicyEvaluationInput(
    TenantConfiguration Tenant,
    AnomalyEvent Anomaly,
    IReadOnlyDictionary<string, string>? Attributes,
    DateTimeOffset EvaluatedAtUtc);

public sealed record PolicyPlannedAction(
    string ActionType,
    IReadOnlyDictionary<string, JsonElement> PayloadOverrides,
    IReadOnlyList<PolicyPlannedAction> DeferredActions);

public sealed record PolicyShadowIntent(
    string IntendedActionType,
    string ActualActionType,
    string SuppressionReason);

public sealed record PolicyDecision(
    PolicyOutcome Outcome,
    Guid? PolicyId,
    int PolicyVersion,
    string MatchedRuleId,
    string Reason,
    bool ExpectsExternalApproval,
    bool InvokeLlmEnrichment,
    DateTimeOffset? ProposalExpiresAt,
    IReadOnlyList<PolicyPlannedAction> Actions,
    PolicyShadowIntent? ShadowIntent,
    string EvaluationSnapshotJson);
