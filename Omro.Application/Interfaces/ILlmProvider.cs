using Omro.Domain.ResolutionTasks;

namespace Omro.Application.Interfaces;

public sealed record LlmProviderRequest(
    AnomalyEvent Anomaly,
    string TenantPromptContext,
    IReadOnlyDictionary<string, string>? Attributes,
    Guid? TenantId = null,
    Guid? ResolutionTaskId = null);

public sealed record LlmProviderResponse(
    CognitivePlan Plan,
    string ProviderName,
    string ModelName,
    int PromptTokens,
    int CompletionTokens,
    int LatencyMs);

public interface ILlmProvider
{
    string ProviderName { get; }
    string ModelName { get; }
    bool IsConfigured { get; }

    Task<LlmProviderResponse> ProposeResolutionAsync(
        LlmProviderRequest request,
        CancellationToken cancellationToken = default);
}
