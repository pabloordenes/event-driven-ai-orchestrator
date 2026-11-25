using System.Text.Json;

namespace Omro.Domain.Tenants;

public class TenantPolicy
{
    public const int MaxAnomalyTypeLength = 100;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string AnomalyType { get; private set; } = string.Empty;
    public int PolicyVersion { get; private set; } = 1;
    public bool IsActive { get; private set; } = true;
    public int? DedupWindowMinutes { get; private set; }
    public string DecisionTableJson { get; private set; } = "{}";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private TenantPolicy() { } // ef core

    public static TenantPolicy Create(
        Guid tenantId,
        string anomalyType,
        string decisionTableJson,
        int policyVersion = 1,
        bool isActive = true,
        int? dedupWindowMinutes = null,
        Guid? id = null,
        DateTimeOffset? utcNow = null)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("TenantId no puede estar vacío.", nameof(tenantId));

        if (string.IsNullOrWhiteSpace(anomalyType) || anomalyType.Trim().Length > MaxAnomalyTypeLength)
            throw new ArgumentException($"AnomalyType es obligatorio y no puede exceder {MaxAnomalyTypeLength} caracteres.", nameof(anomalyType));

        if (policyVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(policyVersion), "PolicyVersion debe ser mayor o igual a 1.");

        if (dedupWindowMinutes is < 0)
            throw new ArgumentOutOfRangeException(nameof(dedupWindowMinutes), "DedupWindowMinutes no puede ser negativo.");

        var validDecisionTable = TenantJsonValidator.EnsureValidJson(decisionTableJson, nameof(decisionTableJson), JsonValueKind.Object);
        var now = utcNow ?? DateTimeOffset.UtcNow;

        return new TenantPolicy
        {
            Id = id.HasValue && id.Value != Guid.Empty ? id.Value : Guid.NewGuid(),
            TenantId = tenantId,
            AnomalyType = anomalyType.Trim(),
            PolicyVersion = policyVersion,
            IsActive = isActive,
            DedupWindowMinutes = dedupWindowMinutes,
            DecisionTableJson = validDecisionTable,
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
