using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Omro.Application.Interfaces;
using Omro.Application.Policies;
using Omro.Application.ResolutionTasks.Commands.IngestAnomaly;
using Omro.Application.ResolutionTasks.Services;
using Omro.Application.Tenants;
using Omro.Domain.Cognitive;
using Omro.Domain.Repositories;
using Omro.Domain.ResolutionTasks;
using Omro.Domain.Tenants;
using Omro.Infrastructure.CognitiveServices;
using Omro.Infrastructure.CognitiveServices.Agents;
using Omro.Infrastructure.CognitiveServices.Providers;
using Omro.Infrastructure.Configuration;
using Omro.Infrastructure.Persistence;
using Omro.Infrastructure.Persistence.Repositories;
using Omro.Infrastructure.Tenants;
using Omro.Infrastructure.Validation;

namespace Omro.IntegrationTests;

public class MultiProviderLlmOrchestratorTests
{
    [Fact]
    public async Task PollyFallback_WhenGroqReturns503_ShouldFailoverToGeminiPersistBothLogsAndEmitMetrics()
    {
        int groqHits = 0;
        int geminiHits = 0;

        var groqHandler = new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref groqHits);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Groq outage") });
        });

        var geminiHandler = new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref geminiHits);
            var json = """
            {
              "candidates": [{ "content": { "parts": [{ "text": "{\"Title\":\"Fallback Gemini Plan\",\"Reasoning\":\"Mitigación enriquecida por Gemini tras fallo de Groq.\",\"ConfidenceScore\":0.93,\"ExpectsExternalApproval\":false,\"ProposedActions\":[]}" }] } }],
              "usageMetadata": { "promptTokenCount": 300, "candidatesTokenCount": 100 }
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        });

        await using var ctx = CreateOrchestratorHost(groqHandler, geminiHandler, maxRetryAttempts: 1, breakerMinThroughput: 4);
        var tenantId = await ctx.SeedTenantAsync(preferredProvider: null, dailyTokenBudget: null);

        var recordedMetrics = new List<(string Instrument, string TenantTag, string ProviderTag, string StatusTag, string FallbackTag)>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == LlmTelemetryMetrics.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            CaptureTag(recordedMetrics, instrument.Name, tags, tenantId));
        meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            CaptureTag(recordedMetrics, instrument.Name, tags, tenantId));
        meterListener.Start();

        var orchestrator = ctx.Provider.GetRequiredService<ITelemetryCognitiveOrchestrator>();
        var result = await orchestrator.ProposeResolutionWithTelemetryAsync(
            new AnomalyEvent(0.92m, Guid.NewGuid(), "DelayRisk", Guid.NewGuid()),
            "Tenant context",
            null,
            new CognitiveOrchestrationContext(tenantId, "test-tenant", Guid.NewGuid()));

        groqHits.Should().Be(2); // 1 intento inicial + 1 retry Polly
        geminiHits.Should().Be(1);
        result.Plan.IsFallback.Should().BeFalse();
        result.Plan.Title.Should().Be("Fallback Gemini Plan");
        result.ProviderName.Should().Be("Gemini");
        result.ModelName.Should().Be("gemini-2.5-flash");
        result.UsedFallbackProvider.Should().BeTrue();
        result.PromptTokens.Should().Be(300);
        result.CompletionTokens.Should().Be(100);
        result.EstimatedCostUsd.Should().Be(0.00010500m); // (300*0.15 + 100*0.60) / 1M

        using var scope = ctx.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var logs = await db.LlmUsageLogs.AsNoTracking()
            .Where(l => l.TenantId == tenantId)
            .OrderBy(l => l.RecordedAt)
            .ToListAsync();

        logs.Should().HaveCount(2);
        logs[0].ProviderName.Should().Be("Groq");
        logs[0].Succeeded.Should().BeFalse();
        logs[0].UsedFallbackProvider.Should().BeFalse();

        logs[1].ProviderName.Should().Be("Gemini");
        logs[1].Succeeded.Should().BeTrue();
        logs[1].UsedFallbackProvider.Should().BeTrue();
        logs[1].TotalTokens.Should().Be(400);
        logs[1].EstimatedCostUsd.Should().Be(0.00010500m);

        recordedMetrics.Should().Contain(m =>
            m.Instrument == "omro.llm.requests" && m.ProviderTag == "Groq" && m.StatusTag == "failure");
        recordedMetrics.Should().Contain(m =>
            m.Instrument == "omro.llm.requests" && m.ProviderTag == "Gemini" && m.StatusTag == "success" && m.FallbackTag == "true");
        recordedMetrics.Should().Contain(m =>
            m.Instrument == "omro.llm.cost.usd" && m.ProviderTag == "Gemini");
    }

    [Fact]
    public async Task PerProviderCircuitBreaker_WhenGroqOpensBreaker_ShouldBypassGroqWithZeroHitsAndRouteToGemini()
    {
        int groqHits = 0;
        int geminiHits = 0;

        var groqHandler = new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref groqHits);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });

        var geminiHandler = new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref geminiHits);
            var json = """
            {
              "candidates": [{ "content": { "parts": [{ "text": "{\"Title\":\"Gemini Direct CB\",\"Reasoning\":\"CB Groq abierto\",\"ConfidenceScore\":0.90,\"ExpectsExternalApproval\":false,\"ProposedActions\":[]}" }] } }],
              "usageMetadata": { "promptTokenCount": 50, "candidatesTokenCount": 25 }
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        });

        await using var ctx = CreateOrchestratorHost(groqHandler, geminiHandler, maxRetryAttempts: 1, breakerMinThroughput: 2);
        var tenantId = await ctx.SeedTenantAsync();
        var orchestrator = ctx.Provider.GetRequiredService<ITelemetryCognitiveOrchestrator>();

        // Llamada 1: 1 intento + 1 retry = 2 fallos -> alcanza CircuitBreakerMinimumThroughput=2 y abre circuito Groq
        var first = await orchestrator.ProposeResolutionWithTelemetryAsync(
            new AnomalyEvent(0.92m, Guid.NewGuid(), "DelayRisk", Guid.NewGuid()),
            "Context",
            null,
            new CognitiveOrchestrationContext(tenantId, "cb-tenant", Guid.NewGuid()));

        first.ProviderName.Should().Be("Gemini");
        groqHits.Should().Be(2);

        // Llamada 2: circuito de Groq está abierto -> 0 llamadas HTTP hacia Groq, pasa directo a Gemini
        var second = await orchestrator.ProposeResolutionWithTelemetryAsync(
            new AnomalyEvent(0.93m, Guid.NewGuid(), "DelayRisk", Guid.NewGuid()),
            "Context",
            null,
            new CognitiveOrchestrationContext(tenantId, "cb-tenant", Guid.NewGuid()));

        second.ProviderName.Should().Be("Gemini");
        second.UsedFallbackProvider.Should().BeTrue();
        groqHits.Should().Be(2);
        geminiHits.Should().Be(2);
    }

    [Fact]
    public async Task IngestHandler_WhenAllProvidersFail_ShouldFallbackToDeterministicPolicyAndDispatchWebhook()
    {
        var groqHandler = new DelegateHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var geminiHandler = new DelegateHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));

        await using var ctx = CreateOrchestratorHost(groqHandler, geminiHandler, maxRetryAttempts: 0, breakerMinThroughput: 10);
        var tenantId = await ctx.SeedTenantAsync();

        var taskId = await ctx.ExecuteIngestAsync(
            new IngestAnomalyCommand(tenantId, 0.92m, Guid.NewGuid(), "DelayRisk", Guid.NewGuid()));

        using var scope = ctx.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await db.ResolutionTasks
            .Include(t => t.ReasoningTraces)
            .Include(t => t.ActionExecutions)
            .FirstAsync(t => t.Id == taskId);

        saved.Status.Should().Be(ResolutionStatus.Resolved);
        saved.ReasoningTraces.Should().Contain(t => t.StepType == "LlmUnavailableDeterministicFallback");
        saved.ActionExecutions.Should().ContainSingle(a => a.Status == ActionExecutionStatus.Succeeded);
        ctx.DispatchedWebhookUrls.Should().ContainSingle("https://tenant.test/api/mitigate");
    }

    [Fact]
    public async Task PreferredProviderGemini_AndDailyTokenBudgetExceeded_AndE2EFallbackTrace_ShouldWorkAsConfigured()
    {
        int groqHits = 0;
        int geminiHits = 0;

        var groqHandler = new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref groqHits);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });

        var geminiHandler = new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref geminiHits);
            var json = """
            {
              "candidates": [{ "content": { "parts": [{ "text": "{\"Title\":\"Gemini E2E\",\"Reasoning\":\"Explicación Gemini\",\"ConfidenceScore\":0.95,\"ExpectsExternalApproval\":false,\"ProposedActions\":[]}" }] } }],
              "usageMetadata": { "promptTokenCount": 400, "candidatesTokenCount": 150 }
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        });

        await using var ctx = CreateOrchestratorHost(groqHandler, geminiHandler, maxRetryAttempts: 0, breakerMinThroughput: 10);

        // 1. PreferredProvider = "Gemini" -> 0 hits a Groq, UsedFallbackProvider = false
        var geminiPreferredTenant = await ctx.SeedTenantAsync(preferredProvider: "Gemini", dailyTokenBudget: 1000);
        var orchestrator = ctx.Provider.GetRequiredService<ITelemetryCognitiveOrchestrator>();
        var preferredRes = await orchestrator.ProposeResolutionWithTelemetryAsync(
            new AnomalyEvent(0.91m, Guid.NewGuid(), "DelayRisk", Guid.NewGuid()),
            "Context",
            null,
            new CognitiveOrchestrationContext(geminiPreferredTenant, "gemini-pref", null, PreferredProvider: "Gemini", DailyTokenBudget: 1000));

        preferredRes.ProviderName.Should().Be("Gemini");
        preferredRes.UsedFallbackProvider.Should().BeFalse();
        groqHits.Should().Be(0);
        geminiHits.Should().Be(1);

        // 2. DailyTokenBudget superado -> 0 hits HTTP a LLM y ejecuta plan determinista
        var budgetedTenant = await ctx.SeedTenantAsync(preferredProvider: "Gemini", dailyTokenBudget: 500);
        using (var seedScope = ctx.Provider.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.LlmUsageLogs.Add(LlmUsageLog.Create(
                budgetedTenant,
                null,
                Guid.NewGuid(),
                "DelayRisk",
                "Gemini",
                "gemini-2.5-flash",
                succeeded: true,
                usedFallbackProvider: false,
                promptTokens: 350,
                completionTokens: 200,
                estimatedCostUsd: 0.001m,
                latencyMs: 50));
            await db.SaveChangesAsync();
        }

        var budgetTaskId = await ctx.ExecuteIngestAsync(
            new IngestAnomalyCommand(budgetedTenant, 0.92m, Guid.NewGuid(), "DelayRisk", Guid.NewGuid()));
        geminiHits.Should().Be(1); // No incrementó por cuota agotada

        // 3. E2E con IngestAnomalyCommandHandler: Groq falla -> Gemini enriquece + guarda traza LlmProviderFallbackSucceeded + LlmUsageLog vinculado
        var e2eTenant = await ctx.SeedTenantAsync(preferredProvider: "Groq", dailyTokenBudget: null);
        var e2eTaskId = await ctx.ExecuteIngestAsync(
            new IngestAnomalyCommand(e2eTenant, 0.92m, Guid.NewGuid(), "DelayRisk", Guid.NewGuid()));

        groqHits.Should().Be(1);
        geminiHits.Should().Be(2);

        using var verifyScope = ctx.Provider.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var budgetTask = await verifyDb.ResolutionTasks.Include(t => t.ReasoningTraces).FirstAsync(t => t.Id == budgetTaskId);
        budgetTask.Status.Should().Be(ResolutionStatus.Resolved);
        budgetTask.ReasoningTraces.Should().Contain(t =>
            t.StepType == "LlmUnavailableDeterministicFallback" &&
            t.TraceMessage.Contains("DAILY_TOKEN_BUDGET_EXCEEDED"));

        var e2eTask = await verifyDb.ResolutionTasks.Include(t => t.ReasoningTraces).FirstAsync(t => t.Id == e2eTaskId);
        e2eTask.Status.Should().Be(ResolutionStatus.Resolved);
        e2eTask.ReasoningTraces.Should().Contain(t =>
            t.StepType == "LlmProviderFallbackSucceeded" &&
            t.TraceMessage.Contains("Gemini"));

        var e2eLogs = await verifyDb.LlmUsageLogs.AsNoTracking()
            .Where(l => l.TenantId == e2eTenant && l.ResolutionTaskId == e2eTaskId)
            .ToListAsync();
        e2eLogs.Should().HaveCount(2);
        e2eLogs.Should().Contain(l => l.ProviderName == "Groq" && !l.Succeeded);
        e2eLogs.Should().Contain(l => l.ProviderName == "Gemini" && l.Succeeded && l.UsedFallbackProvider);
    }

    private static void CaptureTag(
        List<(string Instrument, string TenantTag, string ProviderTag, string StatusTag, string FallbackTag)> bucket,
        string instrumentName,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        Guid expectedTenantId)
    {
        string tenant = "";
        string provider = "";
        string status = "";
        string fallback = "";
        foreach (var kvp in tags)
        {
            switch (kvp.Key)
            {
                case "tenant_id": tenant = kvp.Value?.ToString() ?? ""; break;
                case "provider": provider = kvp.Value?.ToString() ?? ""; break;
                case "status": status = kvp.Value?.ToString() ?? ""; break;
                case "fallback": fallback = kvp.Value?.ToString() ?? ""; break;
            }
        }

        if (string.Equals(tenant, expectedTenantId.ToString(), StringComparison.Ordinal))
        {
            lock (bucket)
            {
                bucket.Add((instrumentName, tenant, provider, status, fallback));
            }
        }
    }

    private static OrchestratorTestContext CreateOrchestratorHost(
        HttpMessageHandler groqHandler,
        HttpMessageHandler geminiHandler,
        int maxRetryAttempts,
        int breakerMinThroughput)
    {
        var services = new ServiceCollection();
        var dbName = $"OmroMultiLlm_{Guid.NewGuid()}";
        var inMemoryProvider = new ServiceCollection().AddEntityFrameworkInMemoryDatabase().BuildServiceProvider();

        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddDbContext<ApplicationDbContext>(o =>
            o.UseInMemoryDatabase(dbName).UseInternalServiceProvider(inMemoryProvider));
        services.AddMemoryCache();
        services.Configure<TenantCacheOptions>(_ => { });
        services.Configure<PlanValidationOptions>(_ => { });
        services.Configure<WebhookExecutionOptions>(_ => { });
        services.Configure<GroqOptions>(o =>
        {
            o.ApiKey = "test-groq-key";
            o.Model = "openai/gpt-oss-120b";
            o.BaseUrl = "https://api.groq.com/openai/v1/";
        });
        services.Configure<GeminiOptions>(o =>
        {
            o.ApiKey = "test-gemini-key";
            o.Model = "gemini-2.5-flash";
            o.BaseUrl = "https://generativelanguage.googleapis.com/v1beta/";
        });
        services.Configure<CognitiveOrchestratorOptions>(o =>
        {
            o.ProviderOrder = ["Groq", "Gemini"];
            o.AttemptTimeoutMs = 5000;
            o.MaxRetryAttempts = maxRetryAttempts;
            o.RetryDelayMs = 10;
            o.CircuitBreakerMinimumThroughput = breakerMinThroughput;
            o.CircuitBreakerFailureRatio = 0.5;
            o.CircuitBreakerSamplingDurationSeconds = 30;
            o.CircuitBreakerBreakDurationSeconds = 60;
        });

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ILlmUsageLogRepository, LlmUsageLogRepository>();
        services.AddScoped<IResolutionTaskRepository, ResolutionTaskRepository>();
        services.AddScoped<IActionExecutionRepository, ActionExecutionRepository>();
        services.AddScoped<ITenantConfigRepository, TenantConfigRepository>();
        services.AddScoped<IShadowDecisionRepository, ShadowDecisionRepository>();
        services.AddScoped<IActionExecutionCoordinator, ActionExecutionCoordinator>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddSingleton<ITenantConfigProvider, CachedTenantConfigProvider>();
        services.AddSingleton<IPolicyEngine, JsonDecisionTablePolicyEngine>();
        services.AddSingleton<IDeterministicPlanBuilder, DeterministicPlanBuilder>();
        services.AddSingleton<IPlanValidator, CatalogPlanValidator>();
        services.AddSingleton<IActionIdempotencyKeyGenerator, ActionIdempotencyKeyGenerator>();

        services.AddSingleton<ILlmCostCalculator, LlmCostCalculator>();
        services.AddSingleton<LlmTelemetryMetrics>();
        services.AddSingleton<ILlmUsageRecorder, EfLlmUsageRecorder>();

        services.AddHttpClient(GroqLlmProvider.HttpClientName, c =>
        {
            c.BaseAddress = new Uri("https://api.groq.com/openai/v1/");
        }).ConfigurePrimaryHttpMessageHandler(() => groqHandler);

        services.AddHttpClient(GeminiLlmProvider.HttpClientName, c =>
        {
            c.BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/");
        }).ConfigurePrimaryHttpMessageHandler(() => geminiHandler);

        services.AddSingleton<ILlmProvider, GroqLlmProvider>();
        services.AddSingleton<ILlmProvider, GeminiLlmProvider>();
        services.AddSingleton<ResilientCognitiveOrchestrator>();
        services.AddSingleton<ICognitiveOrchestrator>(sp => sp.GetRequiredService<ResilientCognitiveOrchestrator>());
        services.AddSingleton<ITelemetryCognitiveOrchestrator>(sp => sp.GetRequiredService<ResilientCognitiveOrchestrator>());

        var testContext = new OrchestratorTestContext(null!);
        var webhookMock = new Mock<IWebhookDispatcher>();
        webhookMock
            .Setup(w => w.DispatchAsync(
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>(),
                It.IsAny<Guid?>(),
                It.IsAny<WebhookDispatchOptions?>()))
            .Callback<string, Dictionary<string, object>, CancellationToken, string?, Guid?, WebhookDispatchOptions?>((ep, _, _, _, _, _) =>
                testContext.DispatchedWebhookUrls.Add(ep))
            .ReturnsAsync(WebhookDispatchResult.Succeeded(200, 1));
        services.AddSingleton(webhookMock.Object);

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<IngestAnomalyCommandHandler>());
        services.AddLogging();

        var provider = services.BuildServiceProvider();
        var finalContext = new OrchestratorTestContext(provider);

        webhookMock.Reset();
        webhookMock
            .Setup(w => w.DispatchAsync(
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>(),
                It.IsAny<Guid?>(),
                It.IsAny<WebhookDispatchOptions?>()))
            .Callback<string, Dictionary<string, object>, CancellationToken, string?, Guid?, WebhookDispatchOptions?>((ep, _, _, _, _, _) =>
                finalContext.DispatchedWebhookUrls.Add(ep))
            .ReturnsAsync(WebhookDispatchResult.Succeeded(200, 1));

        return finalContext;
    }

    private sealed class OrchestratorTestContext : IAsyncDisposable
    {
        public ServiceProvider Provider { get; }
        public List<string> DispatchedWebhookUrls { get; } = new();

        public OrchestratorTestContext(ServiceProvider provider) => Provider = provider;

        public async Task<Guid> SeedTenantAsync(string? preferredProvider = null, int? dailyTokenBudget = null)
        {
            var tenantId = Guid.NewGuid();
            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var llmConfigJson = JsonSerializer.Serialize(new
            {
                enrichmentEnabled = true,
                preferredProvider,
                dailyTokenBudget,
                timeoutMs = 4000
            });

            var tenant = TenantConfig.Create(
                tenantId: tenantId,
                tenantSlug: $"llm-{tenantId:N}"[..24],
                displayName: "LLM Tenant",
                basePromptContext: "Prompt base de prueba",
                authAudience: "tenant-api",
                autonomousExecutionEnabled: true,
                allowedEndpointHostsJson: "[\"https://tenant.test\"]",
                llmConfigJson: llmConfigJson);

            tenant.AddAction(TenantAction.Create(
                tenantId,
                "EXECUTE_MITIGATION",
                "Mitigate",
                "https://tenant.test/api/mitigate",
                defaultPayloadTemplateJson: "{\"resourceId\":\"{{resourceId}}\"}"));

            tenant.AddPolicy(TenantPolicy.Create(
                tenantId,
                "DelayRisk",
                """
                {
                  "hitPolicy": "FIRST_MATCH",
                  "rules": [
                    {
                      "ruleId": "auto",
                      "priority": 10,
                      "when": { "field": "riskProbability", "op": "gte", "value": 0.85 },
                      "then": {
                        "outcome": "ExecuteActions",
                        "expectsExternalApproval": false,
                        "invokeLlmEnrichment": true,
                        "actions": [{ "actionType": "EXECUTE_MITIGATION" }]
                      }
                    }
                  ],
                  "defaultDecision": { "outcome": "NoAction", "invokeLlmEnrichment": false, "actions": [] }
                }
                """));

            db.TenantConfigs.Add(tenant);
            await db.SaveChangesAsync();
            Provider.GetRequiredService<ITenantConfigProvider>().Invalidate(tenantId);
            return tenantId;
        }

        public async Task<Guid> ExecuteIngestAsync(IngestAnomalyCommand cmd)
        {
            using var scope = Provider.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
            return await sender.Send(cmd);
        }

        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }

    private sealed class DelegateHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _fn;
        public DelegateHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _fn(request, cancellationToken);
    }
}
