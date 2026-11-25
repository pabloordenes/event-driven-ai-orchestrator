namespace Omro.Domain.Cognitive;

public class LlmUsageLog
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid? ResolutionTaskId { get; private set; }
    public Guid ResourceId { get; private set; }
    public string AnomalyType { get; private set; } = string.Empty;
    public string ProviderName { get; private set; } = string.Empty;
    public string ModelName { get; private set; } = string.Empty;
    public bool Succeeded { get; private set; }
    public bool UsedFallbackProvider { get; private set; }
    public int PromptTokens { get; private set; }
    public int CompletionTokens { get; private set; }
    public int TotalTokens { get; private set; }
    public decimal EstimatedCostUsd { get; private set; }
    public int LatencyMs { get; private set; }
    public string? ErrorReason { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }

    private LlmUsageLog() { } // ef core

    public static LlmUsageLog Create(
        Guid tenantId,
        Guid? resolutionTaskId,
        Guid resourceId,
        string anomalyType,
        string providerName,
        string modelName,
        bool succeeded,
        bool usedFallbackProvider,
        int promptTokens,
        int completionTokens,
        decimal estimatedCostUsd,
        int latencyMs,
        string? errorReason = null,
        DateTimeOffset? recordedAtUtc = null,
        Guid? id = null)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("TenantId es obligatorio para LlmUsageLog.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(providerName))
            throw new ArgumentException("ProviderName es obligatorio.", nameof(providerName));
        if (string.IsNullOrWhiteSpace(modelName))
            throw new ArgumentException("ModelName es obligatorio.", nameof(modelName));

        var clampedPrompt = Math.Max(0, promptTokens);
        var clampedCompletion = Math.Max(0, completionTokens);
        var clampedCost = Math.Max(0m, Math.Round(estimatedCostUsd, 8, MidpointRounding.AwayFromZero));
        var clampedLatency = Math.Max(0, latencyMs);

        string? normalizedError = null;
        if (!string.IsNullOrWhiteSpace(errorReason))
        {
            var trimmed = errorReason.Trim();
            normalizedError = trimmed.Length <= 500 ? trimmed : trimmed[..500];
        }

        return new LlmUsageLog
        {
            Id = id.HasValue && id.Value != Guid.Empty ? id.Value : Guid.NewGuid(),
            TenantId = tenantId,
            ResolutionTaskId = resolutionTaskId.HasValue && resolutionTaskId.Value != Guid.Empty ? resolutionTaskId : null,
            ResourceId = resourceId,
            AnomalyType = string.IsNullOrWhiteSpace(anomalyType) ? "GenericAnomaly" : Truncate(anomalyType.Trim(), 100),
            ProviderName = Truncate(providerName.Trim(), 64),
            ModelName = Truncate(modelName.Trim(), 128),
            Succeeded = succeeded,
            UsedFallbackProvider = usedFallbackProvider,
            PromptTokens = clampedPrompt,
            CompletionTokens = clampedCompletion,
            TotalTokens = clampedPrompt + clampedCompletion,
            EstimatedCostUsd = clampedCost,
            LatencyMs = clampedLatency,
            ErrorReason = normalizedError,
            RecordedAt = recordedAtUtc ?? DateTimeOffset.UtcNow
        };
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
