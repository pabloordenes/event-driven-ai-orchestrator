using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Omro.Domain.Tenants;

namespace Omro.Application.Tenants;

public sealed record TenantAuthProfile(
    TenantAuthScheme Scheme,
    string? Issuer,
    string Audience,
    string Role,
    string? SigningKeyRef,
    string? SigningKeyKid,
    string? HmacSecretRef);

public sealed record TenantLlmSettings(
    bool EnrichmentEnabled = true,
    string? PreferredProvider = null,
    int? DailyTokenBudget = null,
    int? TimeoutMs = null);

public sealed record ActionRetryPolicy(
    int? MaxRetryAttempts = null,
    int? InitialDelayMs = null,
    int? AttemptTimeoutSeconds = null,
    int? MaxTotalAttempts = null);

public sealed record TenantActionDescriptor(
    Guid Id,
    string ActionType,
    string Description,
    string EndpointUrl,
    string HttpMethod,
    string PayloadSchemaJson,
    string DefaultPayloadTemplateJson,
    IReadOnlyList<string> LlmEditableFields,
    bool RequiresApproval,
    TenantActionPhase AllowedPhases,
    ActionRetryPolicy RetryPolicy,
    int SchemaVersion);

public sealed record TenantPolicyDescriptor(
    Guid Id,
    string AnomalyType,
    int PolicyVersion,
    int? DedupWindowMinutes,
    string DecisionTableJson);

public sealed record TenantConfiguration(
    Guid TenantId,
    string TenantSlug,
    string DisplayName,
    bool IsActive,
    string BasePromptContext,
    bool AutonomousExecutionEnabled,
    IReadOnlyDictionary<string, JsonElement> FeatureFlags,
    int HitlProposalTtlMinutes,
    int HitlProposalMinLeadMinutes,
    int DedupWindowMinutes,
    IReadOnlySet<string> AllowedOrigins,
    TenantAuthProfile Auth,
    int MaxPayloadBytes,
    int EgressTimeoutSeconds,
    TenantLlmSettings LlmSettings,
    int ConfigVersion,
    IReadOnlyDictionary<string, TenantActionDescriptor> Actions,
    IReadOnlyDictionary<string, TenantPolicyDescriptor> Policies)
{
    public bool TryGetAction(string actionType, [MaybeNullWhen(false)] out TenantActionDescriptor action)
    {
        if (string.IsNullOrWhiteSpace(actionType))
        {
            action = null;
            return false;
        }

        return Actions.TryGetValue(actionType.Trim(), out action);
    }

    public TenantPolicyDescriptor? ResolvePolicy(string anomalyType)
    {
        if (!string.IsNullOrWhiteSpace(anomalyType) &&
            Policies.TryGetValue(anomalyType.Trim(), out var exactPolicy))
        {
            return exactPolicy;
        }

        return Policies.TryGetValue("*", out var wildcardPolicy)
            ? wildcardPolicy
            : null;
    }

    public DateTimeOffset ComputeHitlProposalExpiresAt(
        DateTimeOffset utcNow,
        DateTimeOffset? resourceEventTimeUtc = null)
    {
        var defaultExpiry = utcNow.AddMinutes(HitlProposalTtlMinutes);

        if (!resourceEventTimeUtc.HasValue)
            return defaultExpiry;

        var leadCap = resourceEventTimeUtc.Value.AddMinutes(-HitlProposalMinLeadMinutes);
        if (leadCap <= utcNow)
            return utcNow;

        return leadCap < defaultExpiry ? leadCap : defaultExpiry;
    }

    public string BuildOrchestratorPromptContext(string anomalyType)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"TENANT: {DisplayName} ({TenantSlug})");
        sb.AppendLine($"AUTONOMOUS_EXECUTION_ENABLED: {(AutonomousExecutionEnabled ? "true" : "false")}");
        sb.AppendLine($"HITL_PROPOSAL_TTL_MINUTES: {HitlProposalTtlMinutes}");
        sb.AppendLine();
        sb.AppendLine("CONTEXTO DEL TENANT:");
        sb.AppendLine(BasePromptContext);
        sb.AppendLine();
        sb.AppendLine("CATÁLOGO DE ACCIONES AUTORIZADAS:");

        if (Actions.Count == 0)
        {
            sb.AppendLine("- (sin acciones activas configuradas)");
        }
        else
        {
            foreach (var action in Actions.Values.OrderBy(a => a.ActionType, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(
                    $"- ActionType: {action.ActionType} | Method: {action.HttpMethod} | Endpoint: {action.EndpointUrl} | RequiresApproval: {(action.RequiresApproval ? "true" : "false")} | Phases: {action.AllowedPhases} | Description: {action.Description}");
            }
        }

        var policy = ResolvePolicy(anomalyType);
        if (policy is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"POLÍTICA APLICABLE ({policy.AnomalyType} v{policy.PolicyVersion}):");
            sb.AppendLine(policy.DecisionTableJson);
        }

        return sb.ToString().TrimEnd();
    }
}
