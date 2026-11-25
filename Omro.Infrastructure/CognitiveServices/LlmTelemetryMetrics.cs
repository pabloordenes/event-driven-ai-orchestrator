using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Omro.Infrastructure.CognitiveServices;

public sealed class LlmTelemetryMetrics : IDisposable
{
    public const string MeterName = "Omro.Cognitive";

    private readonly Meter _meter;
    private readonly Counter<long> _requestsCounter;
    private readonly Counter<long> _promptTokensCounter;
    private readonly Counter<long> _completionTokensCounter;
    private readonly Counter<double> _costUsdCounter;
    private readonly Histogram<double> _latencyMsHistogram;

    public LlmTelemetryMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");
        _requestsCounter = _meter.CreateCounter<long>("omro.llm.requests", unit: "{request}");
        _promptTokensCounter = _meter.CreateCounter<long>("omro.llm.tokens.prompt", unit: "{token}");
        _completionTokensCounter = _meter.CreateCounter<long>("omro.llm.tokens.completion", unit: "{token}");
        _costUsdCounter = _meter.CreateCounter<double>("omro.llm.cost.usd", unit: "USD");
        _latencyMsHistogram = _meter.CreateHistogram<double>("omro.llm.latency.ms", unit: "ms");
    }

    public void RecordAttempt(
        Guid tenantId,
        string provider,
        string model,
        bool succeeded,
        bool usedFallbackProvider,
        int promptTokens,
        int completionTokens,
        decimal estimatedCostUsd,
        int latencyMs)
    {
        var tags = new TagList
        {
            { "tenant_id", tenantId.ToString() },
            { "provider", string.IsNullOrWhiteSpace(provider) ? "unknown" : provider.Trim() },
            { "model", string.IsNullOrWhiteSpace(model) ? "unknown" : model.Trim() },
            { "status", succeeded ? "success" : "failure" },
            { "fallback", usedFallbackProvider ? "true" : "false" }
        };

        _requestsCounter.Add(1, tags);
        if (promptTokens > 0)
            _promptTokensCounter.Add(promptTokens, tags);
        if (completionTokens > 0)
            _completionTokensCounter.Add(completionTokens, tags);
        if (estimatedCostUsd > 0m)
            _costUsdCounter.Add((double)estimatedCostUsd, tags);

        _latencyMsHistogram.Record(Math.Max(0, latencyMs), tags);
    }

    public void Dispose() => _meter.Dispose();
}
