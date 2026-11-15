using Omro.Domain.ResolutionTasks;

namespace Omro.Application.Interfaces;

public interface ICognitiveOrchestrator
{
    Task<(CognitivePlan Plan, int PromptTokens, int CompletionTokens)> ProposeResolutionAsync(
        AnomalyEvent anomaly,
        string tenantPromptContext,
        CancellationToken cancellationToken = default
        );
}