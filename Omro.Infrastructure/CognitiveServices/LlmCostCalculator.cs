using Microsoft.Extensions.Options;
using Omro.Application.Interfaces;
using Omro.Infrastructure.Configuration;

namespace Omro.Infrastructure.CognitiveServices;

public sealed class LlmCostCalculator : ILlmCostCalculator
{
    private static readonly IReadOnlyDictionary<string, (decimal Input, decimal Output)> BuiltInRates =
        new Dictionary<string, (decimal Input, decimal Output)>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai/gpt-oss-120b"] = (0.15m, 0.60m),
            ["llama-3.3-70b-versatile"] = (0.59m, 0.79m),
            ["gemini-2.5-flash"] = (0.15m, 0.60m),
            ["gemini-2.5-pro"] = (1.25m, 10.00m)
        };

    private static readonly (decimal Input, decimal Output) DefaultFallbackRate = (0.50m, 1.50m);

    private readonly CognitiveOrchestratorOptions _options;

    public LlmCostCalculator(IOptions<CognitiveOrchestratorOptions>? options = null)
    {
        _options = options?.Value ?? new CognitiveOrchestratorOptions();
    }

    public decimal CalculateEstimatedCostUsd(
        string providerName,
        string modelName,
        int promptTokens,
        int completionTokens)
    {
        var promptCount = Math.Max(0, promptTokens);
        var completionCount = Math.Max(0, completionTokens);

        var (inputPerMillion, outputPerMillion) = ResolveRate(providerName, modelName);

        var inputCost = (promptCount / 1_000_000m) * Math.Max(0m, inputPerMillion);
        var outputCost = (completionCount / 1_000_000m) * Math.Max(0m, outputPerMillion);

        return Math.Round(inputCost + outputCost, 8, MidpointRounding.AwayFromZero);
    }

    private (decimal Input, decimal Output) ResolveRate(string providerName, string modelName)
    {
        var trimmedModel = modelName?.Trim() ?? string.Empty;
        var trimmedProvider = providerName?.Trim() ?? string.Empty;
        var pricing = _options.PricingPerMillionTokens;

        if (pricing is { Count: > 0 })
        {
            if (!string.IsNullOrEmpty(trimmedModel) && pricing.TryGetValue(trimmedModel, out var byModel))
                return (byModel.InputUsdPerMillion, byModel.OutputUsdPerMillion);

            var compositeKey = $"{trimmedProvider}:{trimmedModel}";
            if (pricing.TryGetValue(compositeKey, out var byComposite))
                return (byComposite.InputUsdPerMillion, byComposite.OutputUsdPerMillion);
        }

        if (!string.IsNullOrEmpty(trimmedModel) && BuiltInRates.TryGetValue(trimmedModel, out var builtIn))
            return builtIn;

        return DefaultFallbackRate;
    }
}
