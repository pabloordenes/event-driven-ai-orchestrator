using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Omro.Application.Interfaces;
using Omro.Domain.Cognitive;
using Omro.Domain.ResolutionTasks;
using Omro.Infrastructure.Configuration;
using Polly;
using Polly.CircuitBreaker;
using Polly.Fallback;
using Polly.Retry;
using Polly.Timeout;

namespace Omro.Infrastructure.CognitiveServices.Agents;

public sealed class ResilientCognitiveOrchestrator : ITelemetryCognitiveOrchestrator
{
    private readonly IReadOnlyList<ILlmProvider> _providers;
    private readonly CognitiveOrchestratorOptions _options;
    private readonly ILlmCostCalculator _costCalculator;
    private readonly LlmTelemetryMetrics _metrics;
    private readonly ILogger<ResilientCognitiveOrchestrator> _logger;
    private readonly ILlmUsageRecorder? _usageRecorder;
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<string, ResiliencePipeline<LlmProviderResponse>> _providerPipelines =
        new(StringComparer.OrdinalIgnoreCase);

    public ResilientCognitiveOrchestrator(
        IEnumerable<ILlmProvider> providers,
        IOptions<CognitiveOrchestratorOptions> options,
        ILlmCostCalculator costCalculator,
        LlmTelemetryMetrics metrics,
        ILogger<ResilientCognitiveOrchestrator> logger,
        ILlmUsageRecorder? usageRecorder = null,
        TimeProvider? timeProvider = null)
    {
        _providers = providers?.ToList() ?? throw new ArgumentNullException(nameof(providers));
        _options = options?.Value ?? new CognitiveOrchestratorOptions();
        _costCalculator = costCalculator ?? throw new ArgumentNullException(nameof(costCalculator));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _usageRecorder = usageRecorder;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<(CognitivePlan Plan, int PromptTokens, int CompletionTokens)> ProposeResolutionAsync(
        AnomalyEvent anomaly,
        string tenantPromptContext,
        CancellationToken cancellationToken = default)
    {
        var result = await ProposeResolutionWithTelemetryAsync(
            anomaly,
            tenantPromptContext,
            attributes: null,
            orchestrationContext: null,
            cancellationToken);

        return (result.Plan, result.PromptTokens, result.CompletionTokens);
    }

    public async Task<(CognitivePlan Plan, int PromptTokens, int CompletionTokens)> ProposeResolutionAsync(
        AnomalyEvent anomaly,
        string tenantPromptContext,
        IReadOnlyDictionary<string, string>? attributes,
        CancellationToken cancellationToken = default)
    {
        var result = await ProposeResolutionWithTelemetryAsync(
            anomaly,
            tenantPromptContext,
            attributes,
            orchestrationContext: null,
            cancellationToken);

        return (result.Plan, result.PromptTokens, result.CompletionTokens);
    }

    public async Task<CognitiveOrchestrationResult> ProposeResolutionWithTelemetryAsync(
        AnomalyEvent anomaly,
        string tenantPromptContext,
        IReadOnlyDictionary<string, string>? attributes,
        CognitiveOrchestrationContext? orchestrationContext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(anomaly);

        var ctx = orchestrationContext ?? new CognitiveOrchestrationContext();
        var tenantId = ctx.TenantId;
        var utcNow = _timeProvider.GetUtcNow();

        if (ctx.DailyTokenBudget is > 0 &&
            tenantId.HasValue &&
            tenantId.Value != Guid.Empty &&
            _usageRecorder is not null)
        {
            var tokensUsedToday = await _usageRecorder.GetTenantDailyTokensAsync(
                tenantId.Value,
                utcNow,
                cancellationToken);

            if (tokensUsedToday >= ctx.DailyTokenBudget.Value)
            {
                _logger.LogWarning(
                    "[OMRO LLM BUDGET EXCEEDED] Tenant {TenantSlug} ({TenantId}) alcanzó cuota diaria ({Used}/{Budget} tokens). Se omite LLM.",
                    ctx.TenantSlug ?? "unknown",
                    tenantId.Value,
                    tokensUsedToday,
                    ctx.DailyTokenBudget.Value);

                return CreateUnavailableResult(
                    $"DAILY_TOKEN_BUDGET_EXCEEDED ({tokensUsedToday}/{ctx.DailyTokenBudget.Value} tokens)",
                    totalLatencyMs: 0);
            }
        }

        var orderedProviders = ResolveOrderedConfiguredProviders(ctx.PreferredProvider);
        if (orderedProviders.Count == 0)
        {
            _logger.LogWarning(
                "[OMRO LLM UNAVAILABLE] No hay proveedores LLM configurados con API key para recurso {ResourceId}.",
                anomaly.ResourceId);

            return CreateUnavailableResult("NO_CONFIGURED_LLM_PROVIDERS", totalLatencyMs: 0);
        }

        var request = new LlmProviderRequest(
            Anomaly: anomaly,
            TenantPromptContext: tenantPromptContext,
            Attributes: attributes,
            TenantId: tenantId,
            ResolutionTaskId: ctx.ResolutionTaskId);

        var swTotal = Stopwatch.StartNew();

        try
        {
            LlmProviderResponse winner = await ExecuteFallbackChainAsync(
                orderedProviders,
                request,
                ctx.TimeoutMs,
                cancellationToken);

            swTotal.Stop();
            var usedFallback = !string.Equals(
                winner.ProviderName,
                orderedProviders[0].ProviderName,
                StringComparison.OrdinalIgnoreCase);

            var costUsd = _costCalculator.CalculateEstimatedCostUsd(
                winner.ProviderName,
                winner.ModelName,
                winner.PromptTokens,
                winner.CompletionTokens);

            return new CognitiveOrchestrationResult(
                Plan: winner.Plan,
                PromptTokens: winner.PromptTokens,
                CompletionTokens: winner.CompletionTokens,
                ProviderName: winner.ProviderName,
                ModelName: winner.ModelName,
                UsedFallbackProvider: usedFallback,
                EstimatedCostUsd: costUsd,
                LatencyMs: (int)Math.Min(int.MaxValue, swTotal.ElapsedMilliseconds));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            swTotal.Stop();
            _logger.LogWarning(
                ex,
                "[OMRO LLM ALL PROVIDERS FAILED] Todos los proveedores LLM fallaron para recurso {ResourceId}. Se degrada a política determinista.",
                anomaly.ResourceId);

            return CreateUnavailableResult(
                ex.Message,
                (int)Math.Min(int.MaxValue, swTotal.ElapsedMilliseconds));
        }
    }

    private Task<LlmProviderResponse> ExecuteFallbackChainAsync(
        IReadOnlyList<ILlmProvider> orderedProviders,
        LlmProviderRequest request,
        int? tenantTimeoutMs,
        CancellationToken outerCancellationToken)
    {
        if (orderedProviders.Count == 1)
        {
            return ExecuteSingleProviderWithTelemetryAsync(
                orderedProviders[0],
                isFallbackAttempt: false,
                request,
                tenantTimeoutMs,
                outerCancellationToken);
        }

        Func<CancellationToken, Task<LlmProviderResponse>> chain = ct =>
            ExecuteSingleProviderWithTelemetryAsync(
                orderedProviders[^1],
                isFallbackAttempt: true,
                request,
                tenantTimeoutMs,
                ct);

        for (int i = orderedProviders.Count - 2; i >= 0; i--)
        {
            var currentProvider = orderedProviders[i];
            var nextProvider = orderedProviders[i + 1];
            var currentIsFallback = i > 0;
            var nextStage = chain;

            var fallbackPipeline = new ResiliencePipelineBuilder<LlmProviderResponse>()
                .AddFallback(new FallbackStrategyOptions<LlmProviderResponse>
                {
                    ShouldHandle = new PredicateBuilder<LlmProviderResponse>().Handle<Exception>(ex =>
                        ex is not OperationCanceledException || !outerCancellationToken.IsCancellationRequested),
                    OnFallback = args =>
                    {
                        _logger.LogWarning(
                            args.Outcome.Exception,
                            "[OMRO LLM POLLY FALLBACK] Proveedor {FailedProvider} falló para recurso {ResourceId}; ejecutando fallback hacia {NextProvider}.",
                            currentProvider.ProviderName,
                            request.Anomaly.ResourceId,
                            nextProvider.ProviderName);
                        return default;
                    },
                    FallbackAction = async args =>
                    {
                        var fallbackResult = await nextStage(args.Context.CancellationToken);
                        return Outcome.FromResult(fallbackResult);
                    }
                })
                .Build();

            chain = async ct => await fallbackPipeline.ExecuteAsync(
                async innerCt => await ExecuteSingleProviderWithTelemetryAsync(
                    currentProvider,
                    currentIsFallback,
                    request,
                    tenantTimeoutMs,
                    innerCt),
                ct);
        }

        return chain(outerCancellationToken);
    }

    private async Task<LlmProviderResponse> ExecuteSingleProviderWithTelemetryAsync(
        ILlmProvider provider,
        bool isFallbackAttempt,
        LlmProviderRequest request,
        int? tenantTimeoutMs,
        CancellationToken outerCancellationToken)
    {
        var pipeline = _providerPipelines.GetOrAdd(provider.ProviderName, _ => BuildProviderPipeline());
        var sw = Stopwatch.StartNew();

        try
        {
            var response = await pipeline.ExecuteAsync(async attemptCt =>
            {
                if (tenantTimeoutMs is > 0)
                {
                    using var tenantTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(attemptCt);
                    tenantTimeoutCts.CancelAfter(Math.Max(100, tenantTimeoutMs.Value));
                    return await provider.ProposeResolutionAsync(request, tenantTimeoutCts.Token);
                }

                return await provider.ProposeResolutionAsync(request, attemptCt);
            }, outerCancellationToken);

            sw.Stop();
            var latencyMs = Math.Max(response.LatencyMs, (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds));
            var costUsd = _costCalculator.CalculateEstimatedCostUsd(
                response.ProviderName,
                response.ModelName,
                response.PromptTokens,
                response.CompletionTokens);

            await RecordAttemptTelemetryAsync(
                request,
                response.ProviderName,
                response.ModelName,
                succeeded: true,
                usedFallbackProvider: isFallbackAttempt,
                promptTokens: response.PromptTokens,
                completionTokens: response.CompletionTokens,
                estimatedCostUsd: costUsd,
                latencyMs: latencyMs,
                errorReason: null,
                cancellationToken: outerCancellationToken);

            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !outerCancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            var latencyMs = (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);

            await RecordAttemptTelemetryAsync(
                request,
                provider.ProviderName,
                provider.ModelName,
                succeeded: false,
                usedFallbackProvider: isFallbackAttempt,
                promptTokens: 0,
                completionTokens: 0,
                estimatedCostUsd: 0m,
                latencyMs: latencyMs,
                errorReason: ex.Message,
                cancellationToken: outerCancellationToken);

            throw;
        }
    }

    private async Task RecordAttemptTelemetryAsync(
        LlmProviderRequest request,
        string providerName,
        string modelName,
        bool succeeded,
        bool usedFallbackProvider,
        int promptTokens,
        int completionTokens,
        decimal estimatedCostUsd,
        int latencyMs,
        string? errorReason,
        CancellationToken cancellationToken)
    {
        var effectiveTenantId = request.TenantId ?? Guid.Empty;

        _metrics.RecordAttempt(
            effectiveTenantId,
            providerName,
            modelName,
            succeeded,
            usedFallbackProvider,
            promptTokens,
            completionTokens,
            estimatedCostUsd,
            latencyMs);

        if (_usageRecorder is not null && effectiveTenantId != Guid.Empty)
        {
            var entry = LlmUsageLog.Create(
                tenantId: effectiveTenantId,
                resolutionTaskId: request.ResolutionTaskId,
                resourceId: request.Anomaly.ResourceId,
                anomalyType: request.Anomaly.AnomalyType,
                providerName: providerName,
                modelName: modelName,
                succeeded: succeeded,
                usedFallbackProvider: usedFallbackProvider,
                promptTokens: promptTokens,
                completionTokens: completionTokens,
                estimatedCostUsd: estimatedCostUsd,
                latencyMs: latencyMs,
                errorReason: errorReason,
                recordedAtUtc: _timeProvider.GetUtcNow());

            await _usageRecorder.RecordAsync(entry, cancellationToken);
        }
    }

    private ResiliencePipeline<LlmProviderResponse> BuildProviderPipeline()
    {
        var timeoutMs = Math.Max(100, _options.AttemptTimeoutMs);
        var samplingSec = Math.Max(2, _options.CircuitBreakerSamplingDurationSeconds);
        var breakSec = Math.Max(1, _options.CircuitBreakerBreakDurationSeconds);
        var minThroughput = Math.Max(2, _options.CircuitBreakerMinimumThroughput);
        var failureRatio = Math.Clamp(_options.CircuitBreakerFailureRatio, 0.01, 1.0);

        var builder = new ResiliencePipelineBuilder<LlmProviderResponse>();

        if (_options.MaxRetryAttempts > 0)
        {
            builder.AddRetry(new RetryStrategyOptions<LlmProviderResponse>
            {
                MaxRetryAttempts = _options.MaxRetryAttempts,
                Delay = TimeSpan.FromMilliseconds(Math.Max(1, _options.RetryDelayMs)),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<LlmProviderResponse>().Handle<Exception>(IsTransientException)
            });
        }

        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<LlmProviderResponse>
        {
            MinimumThroughput = minThroughput,
            FailureRatio = failureRatio,
            SamplingDuration = TimeSpan.FromSeconds(samplingSec),
            BreakDuration = TimeSpan.FromSeconds(breakSec),
            ShouldHandle = new PredicateBuilder<LlmProviderResponse>().Handle<Exception>(IsTransientException)
        });

        builder.AddTimeout(TimeSpan.FromMilliseconds(timeoutMs));

        return builder.Build();
    }

    private IReadOnlyList<ILlmProvider> ResolveOrderedConfiguredProviders(string? preferredProvider)
    {
        var configured = _providers.Where(p => p.IsConfigured).ToList();
        if (configured.Count == 0)
            return configured;

        var ordered = new List<ILlmProvider>(configured.Count);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(preferredProvider))
        {
            var match = configured.FirstOrDefault(p =>
                string.Equals(p.ProviderName, preferredProvider.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null && seenNames.Add(match.ProviderName))
            {
                ordered.Add(match);
            }
        }

        if (_options.ProviderOrder is { Count: > 0 })
        {
            foreach (var name in _options.ProviderOrder)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var match = configured.FirstOrDefault(p =>
                    string.Equals(p.ProviderName, name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match is not null && seenNames.Add(match.ProviderName))
                {
                    ordered.Add(match);
                }
            }
        }

        foreach (var remaining in configured)
        {
            if (seenNames.Add(remaining.ProviderName))
            {
                ordered.Add(remaining);
            }
        }

        return ordered;
    }

    private static bool IsTransientException(Exception ex)
    {
        if (ex is BrokenCircuitException)
            return false;

        if (ex is TimeoutRejectedException or TaskCanceledException)
            return true;

        if (ex is HttpRequestException httpEx)
        {
            if (httpEx.StatusCode is null)
                return true;

            var statusCode = (int)httpEx.StatusCode.Value;
            return statusCode >= 500 ||
                   httpEx.StatusCode == HttpStatusCode.RequestTimeout ||
                   httpEx.StatusCode == HttpStatusCode.TooManyRequests;
        }

        return false;
    }

    private static CognitiveOrchestrationResult CreateUnavailableResult(string reason, int totalLatencyMs) =>
        new(
            Plan: new CognitivePlan(
                Title: "NeedsHumanReview",
                Reasoning: $"Orquestador IA no disponible ({reason}). Se bloquea ejecución autónoma y se deriva a revisión humana.",
                ConfidenceScore: 0m,
                ProposedActions: new List<ProposedAction>(),
                IsFallback: true),
            PromptTokens: 0,
            CompletionTokens: 0,
            ProviderName: null,
            ModelName: null,
            UsedFallbackProvider: false,
            EstimatedCostUsd: 0m,
            LatencyMs: totalLatencyMs);
}
