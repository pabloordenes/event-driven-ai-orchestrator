using Omro.Domain.ResolutionTasks;

namespace Omro.Application.Interfaces;

public sealed record CognitiveOrchestrationContext(
    Guid? TenantId = null,
    string? TenantSlug = null,
    Guid? ResolutionTaskId = null,
    string? PreferredProvider = null,
    int? TimeoutMs = null,
    int? DailyTokenBudget = null);

public sealed record CognitiveOrchestrationResult(
    CognitivePlan Plan,
    int PromptTokens,
    int CompletionTokens,
    string? ProviderName = null,
    string? ModelName = null,
    bool UsedFallbackProvider = false,
    decimal EstimatedCostUsd = 0m,
    int LatencyMs = 0);

public interface ICognitiveOrchestrator
{
    Task<(CognitivePlan Plan, int PromptTokens, int CompletionTokens)> ProposeResolutionAsync(
        AnomalyEvent anomaly,
        string tenantPromptContext,
        CancellationToken cancellationToken = default);

    Task<(CognitivePlan Plan, int PromptTokens, int CompletionTokens)> ProposeResolutionAsync(
        AnomalyEvent anomaly,
        string tenantPromptContext,
        IReadOnlyDictionary<string, string>? attributes,
        CancellationToken cancellationToken = default)
        => ProposeResolutionAsync(anomaly, tenantPromptContext, cancellationToken);
}

public interface ITelemetryCognitiveOrchestrator : ICognitiveOrchestrator
{
    Task<CognitiveOrchestrationResult> ProposeResolutionWithTelemetryAsync(
        AnomalyEvent anomaly,
        string tenantPromptContext,
        IReadOnlyDictionary<string, string>? attributes,
        CognitiveOrchestrationContext? orchestrationContext = null,
        CancellationToken cancellationToken = default);
}
