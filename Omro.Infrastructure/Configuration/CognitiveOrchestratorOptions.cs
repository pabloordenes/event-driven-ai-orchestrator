namespace Omro.Infrastructure.Configuration;

public class CognitiveOrchestratorOptions
{
    public const string SectionName = "CognitiveOrchestrator";

    public List<string> ProviderOrder { get; set; } = ["Groq", "Gemini"];
    public int AttemptTimeoutMs { get; set; } = 12000;
    public int MaxRetryAttempts { get; set; } = 1;
    public int RetryDelayMs { get; set; } = 250;
    public int CircuitBreakerMinimumThroughput { get; set; } = 4;
    public double CircuitBreakerFailureRatio { get; set; } = 0.5;
    public int CircuitBreakerSamplingDurationSeconds { get; set; } = 30;
    public int CircuitBreakerBreakDurationSeconds { get; set; } = 30;

    public Dictionary<string, ModelPricingOptions> PricingPerMillionTokens { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public class ModelPricingOptions
{
    public decimal InputUsdPerMillion { get; set; }
    public decimal OutputUsdPerMillion { get; set; }
}
