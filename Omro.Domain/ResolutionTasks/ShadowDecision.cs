using System.Text.Json;
using Omro.Domain.Tenants;

namespace Omro.Domain.ResolutionTasks;

public class ShadowDecision
{
    public const int MaxAnomalyTypeLength = 100;
    public const int MaxMatchedRuleIdLength = 100;
    public const int MaxActionTypeLength = 100;
    public const int MaxSuppressionReasonLength = 100;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ResolutionTaskId { get; private set; }
    public Guid ResourceId { get; private set; }
    public string AnomalyType { get; private set; } = string.Empty;
    public Guid PolicyId { get; private set; }
    public int PolicyVersion { get; private set; }
    public string MatchedRuleId { get; private set; } = string.Empty;
    public decimal RiskProbability { get; private set; }
    public string IntendedActionType { get; private set; } = string.Empty;
    public string ActualActionType { get; private set; } = string.Empty;
    public string SuppressionReason { get; private set; } = string.Empty;
    public string EvaluationSnapshotJson { get; private set; } = "{}";
    public Guid CorrelationId { get; private set; }
    public DateTimeOffset EvaluatedAt { get; private set; }

    private ShadowDecision() { } // ef core

    public static ShadowDecision Create(
        Guid tenantId,
        Guid resolutionTaskId,
        Guid resourceId,
        string anomalyType,
        Guid policyId,
        int policyVersion,
        string matchedRuleId,
        decimal riskProbability,
        string intendedActionType,
        string actualActionType,
        string suppressionReason,
        string evaluationSnapshotJson,
        Guid correlationId,
        DateTimeOffset? evaluatedAtUtc = null)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("TenantId no puede estar vacío.", nameof(tenantId));
        if (resolutionTaskId == Guid.Empty)
            throw new ArgumentException("ResolutionTaskId no puede estar vacío.", nameof(resolutionTaskId));
        if (resourceId == Guid.Empty)
            throw new ArgumentException("ResourceId no puede estar vacío.", nameof(resourceId));
        if (policyId == Guid.Empty)
            throw new ArgumentException("PolicyId no puede estar vacío.", nameof(policyId));
        if (correlationId == Guid.Empty)
            throw new ArgumentException("CorrelationId no puede estar vacío.", nameof(correlationId));
        if (policyVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(policyVersion), "PolicyVersion debe ser mayor o igual a 1.");

        var normalizedAnomaly = RequireTrimmed(anomalyType, MaxAnomalyTypeLength, nameof(anomalyType));
        var normalizedRule = RequireTrimmed(matchedRuleId, MaxMatchedRuleIdLength, nameof(matchedRuleId));
        var normalizedIntended = RequireTrimmed(intendedActionType, MaxActionTypeLength, nameof(intendedActionType));
        var normalizedActual = RequireTrimmed(actualActionType, MaxActionTypeLength, nameof(actualActionType));
        var normalizedReason = RequireTrimmed(suppressionReason, MaxSuppressionReasonLength, nameof(suppressionReason));
        var validSnapshot = TenantJsonValidator.EnsureValidJson(evaluationSnapshotJson, nameof(evaluationSnapshotJson), JsonValueKind.Object);

        return new ShadowDecision
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ResolutionTaskId = resolutionTaskId,
            ResourceId = resourceId,
            AnomalyType = normalizedAnomaly,
            PolicyId = policyId,
            PolicyVersion = policyVersion,
            MatchedRuleId = normalizedRule,
            RiskProbability = Math.Round(riskProbability, 4, MidpointRounding.AwayFromZero),
            IntendedActionType = normalizedIntended,
            ActualActionType = normalizedActual,
            SuppressionReason = normalizedReason,
            EvaluationSnapshotJson = validSnapshot,
            CorrelationId = correlationId,
            EvaluatedAt = evaluatedAtUtc ?? DateTimeOffset.UtcNow
        };
    }

    private static string RequireTrimmed(string value, int maxLength, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"El campo '{paramName}' es obligatorio.", paramName);

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
