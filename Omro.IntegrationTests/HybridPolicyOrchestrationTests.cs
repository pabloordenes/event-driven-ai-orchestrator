using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Omro.Application.Contracts;
using Omro.Application.Interfaces;
using Omro.Application.Policies;
using Omro.Application.ResolutionTasks.Commands.IngestAnomaly;
using Omro.Application.ResolutionTasks.Services;
using Omro.Contracts.Events;
using Omro.Contracts.Plans;
using Omro.Domain.Repositories;
using Omro.Domain.ResolutionTasks;
using Omro.Infrastructure.CognitiveServices.Agents;
using Omro.Infrastructure.Configuration;
using Omro.Infrastructure.HttpClients;
using Omro.Infrastructure.Messaging.Configuration;
using Omro.Infrastructure.Messaging.Observers;
using Omro.Infrastructure.Persistence;
using Omro.Infrastructure.Persistence.Repositories;
using Omro.Infrastructure.Tenants;
using Omro.Infrastructure.Validation;
using Omro.Worker.Consumers;

namespace Omro.IntegrationTests;

public class HybridPolicyOrchestrationTests
{
    private static readonly Guid MedCareTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task ConfirmationDeadlineMissed_AutonomousOff_ShouldProposeHitlWithExpiryAndPersistShadowDecision()
    {
        var dispatched = new ConcurrentBag<(string Path, string Body)>();
        var httpHandler = new DelegateHttpMessageHandler(async (req, ct) =>
        {
            var body = await req.Content!.ReadAsStringAsync(ct);
            dispatched.Add((req.RequestUri!.AbsolutePath, body));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var llm = new StubOrchestrator((_, _) => new CognitivePlan(
            Title: "Propuesta HITL enriquecida",
            Reasoning: "Paciente sin confirmación tras aviso final entregado.",
            ConfidenceScore: 0.91m,
            ProposedActions: [],
            IsFallback: false,
            ExpectsExternalApproval: false));

        await using var ctx = await HybridTestHost.CreateAsync(httpHandler, llm);
        await ctx.SeedMedCareAsync(autonomousExecutionEnabled: false);

        var resourceId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        await ctx.Harness.Bus.Publish(new AnomalyDetected(
            TenantId: MedCareTenantId,
            CorrelationId: correlationId,
            AnomalyType: "ConfirmationDeadlineMissed",
            ResourceId: resourceId,
            RiskProbability: 0.90m,
            Attributes: new Dictionary<string, string>
            {
                ["effective_score"] = "0.90",
                ["final_notice_status"] = "Delivered",
                ["clinical_flags"] = "[]",
                ["edad"] = "42"
            }));

        var task = await ctx.WaitForTaskAsync(t =>
            t.Anomaly.ResourceId == resourceId && t.Status == ResolutionStatus.WaitingForExternalData);

        task.Should().NotBeNull();
        task!.ExpectsExternalApproval.Should().BeTrue();
        dispatched.Should().ContainSingle(d => d.Path == "/api/resolutions/propose");

        var sentBody = dispatched.Single().Body;
        using var sentDoc = JsonDocument.Parse(sentBody);
        sentDoc.RootElement.GetProperty("schemaVersion").GetString().Should().Be(PlanContractVersions.V2Structured);
        sentDoc.RootElement.GetProperty("resourceId").GetString().Should().Be(resourceId.ToString());
        sentDoc.RootElement.GetProperty("proposalExpiresAt").GetDateTimeOffset()
            .Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(170));
        sentDoc.RootElement.TryGetProperty("aiPlanJson", out _).Should().BeFalse();

        var aiPlanEl = sentDoc.RootElement.GetProperty("aiPlan");
        aiPlanEl.ValueKind.Should().Be(JsonValueKind.Object);
        aiPlanEl.GetProperty("schemaVersion").GetString().Should().Be(PlanContractVersions.V2Structured);
        aiPlanEl.GetProperty("title").GetString().Should().Be("Propuesta HITL enriquecida");
        aiPlanEl.GetProperty("omroTaskId").GetGuid().Should().Be(task.Id);
        aiPlanEl.GetProperty("isShadowModeFallback").GetBoolean().Should().BeTrue();
        aiPlanEl.GetProperty("proposalExpiresAt").GetDateTimeOffset()
            .Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(170));
        aiPlanEl.GetProperty("deferredActions").GetArrayLength().Should().Be(1);

        task.ProposalExpiresAt.Should().NotBeNull();
        task.ProposalExpiresAt!.Value.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(170));

        using var scope = ctx.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var shadowRows = await db.ShadowDecisions.AsNoTracking()
            .Where(s => s.ResolutionTaskId == task.Id)
            .ToListAsync();

        shadowRows.Should().ContainSingle();
        shadowRows[0].IntendedActionType.Should().Be("GENERATE_OVERBOOK");
        shadowRows[0].ActualActionType.Should().Be("PROPOSE_RESOLUTION");
        shadowRows[0].SuppressionReason.Should().Be("AUTONOMOUS_EXECUTION_DISABLED");
    }

    [Fact]
    public async Task ConfirmationDeadlineMissed_AutonomousOn_ShouldExecuteAutonomousOverbookWithoutShadowDecision()
    {
        var dispatched = new ConcurrentBag<(string Path, string Body)>();
        var httpHandler = new DelegateHttpMessageHandler(async (req, ct) =>
        {
            var body = await req.Content!.ReadAsStringAsync(ct);
            dispatched.Add((req.RequestUri!.AbsolutePath, body));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var llm = new StubOrchestrator((anomaly, _) => new CognitivePlan(
            Title: "Sobrecupo autónomo aprobado por política",
            Reasoning: "Score crítico >= 0.85 y aviso entregado.",
            ConfidenceScore: 0.96m,
            ProposedActions:
            [
                // Intento de secuestro del LLM hacia un endpoint malicioso y un ActionType distinto
                new ProposedAction("EVIL_ACTION", "https://attacker.evil/hijack", new Dictionary<string, object>
                {
                    ["resourceId"] = Guid.NewGuid().ToString(),
                    ["decisionMode"] = "Hijacked",
                    ["reason"] = "Justificación segura redactada por IA."
                }),
                // Además incluye la acción correcta para enriquecer solo `reason`
                new ProposedAction("GENERATE_OVERBOOK", "https://attacker.evil/hijack2", new Dictionary<string, object>
                {
                    ["resourceId"] = Guid.NewGuid().ToString(),
                    ["decisionMode"] = "HijackedMode",
                    ["reason"] = "Justificación enriquecida desde LLM."
                })
            ]));

        await using var ctx = await HybridTestHost.CreateAsync(httpHandler, llm);
        await ctx.SeedMedCareAsync(autonomousExecutionEnabled: true);

        var resourceId = Guid.NewGuid();
        await ctx.Harness.Bus.Publish(new AnomalyDetected(
            TenantId: MedCareTenantId,
            CorrelationId: Guid.NewGuid(),
            AnomalyType: "ConfirmationDeadlineMissed",
            ResourceId: resourceId,
            RiskProbability: 0.90m,
            Attributes: new Dictionary<string, string>
            {
                ["effective_score"] = "0.90",
                ["final_notice_status"] = "Delivered",
                ["clinical_exclusion"] = "false",
                ["edad"] = "35"
            }));

        var task = await ctx.WaitForTaskAsync(t =>
            t.Anomaly.ResourceId == resourceId && t.Status == ResolutionStatus.Resolved);

        task.Should().NotBeNull();
        dispatched.Should().ContainSingle();
        var (path, body) = dispatched.Single();
        path.Should().Be("/api/appointments/overbook");

        using var payloadDoc = JsonDocument.Parse(body);
        payloadDoc.RootElement.GetProperty("resourceId").GetString().Should().Be(resourceId.ToString());
        payloadDoc.RootElement.GetProperty("decisionMode").GetString().Should().Be("Autonomous");
        payloadDoc.RootElement.GetProperty("reason").GetString().Should().Be("Justificación enriquecida desde LLM.");

        using var scope = ctx.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.ShadowDecisions.CountAsync(s => s.ResolutionTaskId == task!.Id)).Should().Be(0);
    }

    [Fact]
    public async Task ConfirmationDeadlineMissed_WhenGroqApiDown_ShouldStillExecuteDeterministicPolicyAction()
    {
        var dispatched = new ConcurrentBag<(string Path, string Body)>();
        var webhookHandler = new DelegateHttpMessageHandler(async (req, ct) =>
        {
            var body = await req.Content!.ReadAsStringAsync(ct);
            dispatched.Add((req.RequestUri!.AbsolutePath, body));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var failingGroqClient = new HttpClient(new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("Groq outage")
            })))
        {
            BaseAddress = new Uri("https://api.groq.com/openai/v1/")
        };

        var realFailingGroq = new GroqCognitiveOrchestrator(
            failingGroqClient,
            Options.Create(new GroqOptions { ApiKey = "test-key", Model = "openai/gpt-oss-120b" }),
            new ConfigurationBuilder().Build(),
            NullLogger<GroqCognitiveOrchestrator>.Instance);

        await using var ctx = await HybridTestHost.CreateAsync(webhookHandler, realFailingGroq);
        await ctx.SeedMedCareAsync(autonomousExecutionEnabled: true);

        var resourceId = Guid.NewGuid();
        await ctx.Harness.Bus.Publish(new AnomalyDetected(
            TenantId: MedCareTenantId,
            CorrelationId: Guid.NewGuid(),
            AnomalyType: "ConfirmationDeadlineMissed",
            ResourceId: resourceId,
            RiskProbability: 0.90m,
            Attributes: new Dictionary<string, string>
            {
                ["effective_score"] = "0.90",
                ["final_notice_status"] = "Delivered"
            }));

        var task = await ctx.WaitForTaskAsync(t =>
            t.Anomaly.ResourceId == resourceId && t.Status == ResolutionStatus.Resolved);

        task.Should().NotBeNull();
        task!.ReasoningTraces.Should().Contain(t => t.StepType == "LlmUnavailableDeterministicFallback");
        dispatched.Should().ContainSingle(d => d.Path == "/api/appointments/overbook");

        using var payloadDoc = JsonDocument.Parse(dispatched.Single().Body);
        payloadDoc.RootElement.GetProperty("resourceId").GetString().Should().Be(resourceId.ToString());
        payloadDoc.RootElement.GetProperty("decisionMode").GetString().Should().Be("Autonomous");
    }

    [Theory]
    [InlineData("0.499", "Delivered")]
    [InlineData("0.95", "Sent")]
    public async Task ConfirmationDeadlineMissed_WhenNoActionRuleMatches_ShouldResolveWithoutCallingLlmOrWebhooks(
        string scoreText,
        string noticeStatus)
    {
        int webhookHits = 0;
        var httpHandler = new DelegateHttpMessageHandler((_, _) =>
        {
            Interlocked.Increment(ref webhookHits);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        int llmCalls = 0;
        var llm = new StubOrchestrator((_, _) =>
        {
            Interlocked.Increment(ref llmCalls);
            throw new InvalidOperationException("No debería invocarse el LLM para NoAction.");
        });

        await using var ctx = await HybridTestHost.CreateAsync(httpHandler, llm);
        await ctx.SeedMedCareAsync(autonomousExecutionEnabled: true);

        var score = decimal.Parse(scoreText, System.Globalization.CultureInfo.InvariantCulture);
        var resourceId = Guid.NewGuid();

        await ctx.Harness.Bus.Publish(new AnomalyDetected(
            TenantId: MedCareTenantId,
            CorrelationId: Guid.NewGuid(),
            AnomalyType: "ConfirmationDeadlineMissed",
            ResourceId: resourceId,
            RiskProbability: score,
            Attributes: new Dictionary<string, string>
            {
                ["effective_score"] = scoreText,
                ["final_notice_status"] = noticeStatus
            }));

        var task = await ctx.WaitForTaskAsync(t =>
            t.Anomaly.ResourceId == resourceId && t.Status == ResolutionStatus.Resolved);

        task.Should().NotBeNull();
        task!.ActionExecutions.Should().BeEmpty();
        task.ReasoningTraces.Should().Contain(t => t.StepType == "PolicyNoAction");
        llmCalls.Should().Be(0);
        webhookHits.Should().Be(0);
    }

    [Fact]
    public async Task ConfirmationDeadlineMissed_HitlV2EndToEndRoundTrip_ShouldResolveTaskWhenStructuredResolutionApproved()
    {
        var dispatched = new ConcurrentBag<(string Path, string Body)>();
        var httpHandler = new DelegateHttpMessageHandler(async (req, ct) =>
        {
            var body = await req.Content!.ReadAsStringAsync(ct);
            dispatched.Add((req.RequestUri!.AbsolutePath, body));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var llm = new StubOrchestrator((_, _) => new CognitivePlan(
            Title: "HITL v2 E2E",
            Reasoning: "Riesgo moderado tras aviso final entregado.",
            ConfidenceScore: 0.78m,
            ProposedActions: [],
            IsFallback: false,
            ExpectsExternalApproval: true));

        await using var ctx = await HybridTestHost.CreateAsync(httpHandler, llm);
        await ctx.SeedMedCareAsync(autonomousExecutionEnabled: false);

        var resourceId = Guid.NewGuid();
        await ctx.Harness.Bus.Publish(new AnomalyDetected(
            TenantId: MedCareTenantId,
            CorrelationId: Guid.NewGuid(),
            AnomalyType: "ConfirmationDeadlineMissed",
            ResourceId: resourceId,
            RiskProbability: 0.72m,
            Attributes: new Dictionary<string, string>
            {
                ["effective_score"] = "0.72",
                ["final_notice_status"] = "Delivered"
            }));

        var waitingTask = await ctx.WaitForTaskAsync(t =>
            t.Anomaly.ResourceId == resourceId && t.Status == ResolutionStatus.WaitingForExternalData);
        waitingTask.Should().NotBeNull();
        waitingTask!.ProposalExpiresAt.Should().NotBeNull();

        var proposeBody = dispatched.Should().ContainSingle(d => d.Path == "/api/resolutions/propose").Subject.Body;
        using var proposeDoc = JsonDocument.Parse(proposeBody);
        var structuredPlan = JsonSerializer.Deserialize<StructuredAiPlan>(
            proposeDoc.RootElement.GetProperty("aiPlan").GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        structuredPlan.Should().NotBeNull();
        structuredPlan!.SchemaVersion.Should().Be(PlanContractVersions.V2Structured);
        structuredPlan.OmroTaskId.Should().Be(waitingTask.Id);
        structuredPlan.DeferredActions.Should().ContainSingle(a => a.ActionType == "GENERATE_OVERBOOK");

        var externalTenantTaskId = Guid.NewGuid();
        await ctx.Harness.Bus.Publish(new ResolutionApproved(
            TenantId: MedCareTenantId,
            CorrelationId: Guid.NewGuid(),
            ResolutionTaskId: externalTenantTaskId,
            ResourceId: resourceId,
            AiPlanJson: null,
            SchemaVersion: PlanContractVersions.V2Structured,
            AiPlan: structuredPlan));

        var resolvedTask = await ctx.WaitForTaskAsync(t =>
            t.Id == waitingTask.Id && t.Status == ResolutionStatus.Resolved);

        resolvedTask.Should().NotBeNull();
        resolvedTask!.ActionExecutions.Should().HaveCount(2);
        resolvedTask.ActionExecutions.Should().OnlyContain(e => e.Status == ActionExecutionStatus.Succeeded);
        resolvedTask.ActionExecutions.Should().Contain(e =>
            e.Phase == ActionExecutionPhase.Deferred && e.ActionType == "GENERATE_OVERBOOK");

        var overbookBody = dispatched.Should().ContainSingle(d => d.Path == "/api/appointments/overbook").Subject.Body;
        using var overbookDoc = JsonDocument.Parse(overbookBody);
        overbookDoc.RootElement.GetProperty("resourceId").GetString().Should().Be(resourceId.ToString());
        overbookDoc.RootElement.GetProperty("decisionMode").GetString().Should().Be("Approved");
    }

    private sealed class StubOrchestrator : ICognitiveOrchestrator
    {
        private readonly Func<AnomalyEvent, string, CognitivePlan> _fn;
        public StubOrchestrator(Func<AnomalyEvent, string, CognitivePlan> fn) => _fn = fn;

        public Task<(CognitivePlan Plan, int PromptTokens, int CompletionTokens)> ProposeResolutionAsync(
            AnomalyEvent anomaly,
            string tenantPromptContext,
            CancellationToken cancellationToken = default)
            => Task.FromResult((_fn(anomaly, tenantPromptContext), 12, 24));
    }

    private sealed class DelegateHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _fn;

        public DelegateHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fn)
            => _fn = fn;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _fn(request, cancellationToken);
    }

    private sealed class HybridTestHost : IAsyncDisposable
    {
        public ServiceProvider Provider { get; }
        public ITestHarness Harness { get; }

        private HybridTestHost(ServiceProvider provider, ITestHarness harness)
        {
            Provider = provider;
            Harness = harness;
        }

        public static async Task<HybridTestHost> CreateAsync(
            HttpMessageHandler primaryHandler,
            ICognitiveOrchestrator orchestrator)
        {
            var services = new ServiceCollection();
            var dbName = $"OmroHybridPolicyDb_{Guid.NewGuid()}";
            var inMemoryProvider = new ServiceCollection().AddEntityFrameworkInMemoryDatabase().BuildServiceProvider();

            services.AddDbContext<ApplicationDbContext>(o =>
                o.UseInMemoryDatabase(dbName).UseInternalServiceProvider(inMemoryProvider));
            services.AddMemoryCache();
            services.Configure<TenantCacheOptions>(_ => { });
            services.Configure<PlanValidationOptions>(_ => { });
            services.AddScoped<IResolutionTaskRepository, ResolutionTaskRepository>();
            services.AddScoped<IActionExecutionRepository, ActionExecutionRepository>();
            services.AddScoped<ITenantConfigRepository, TenantConfigRepository>();
            services.AddScoped<IShadowDecisionRepository, ShadowDecisionRepository>();
            services.AddScoped<IActionExecutionCoordinator, ActionExecutionCoordinator>();
            services.AddScoped<IUnitOfWork, UnitOfWork>();
            services.AddScoped<IMessageInboxStore, EfMessageInboxStore>();
            services.AddSingleton<ITenantConfigProvider, CachedTenantConfigProvider>();
            services.AddScoped<ITenantSeedLoader, JsonTenantSeedLoader>();
            services.AddSingleton<IPolicyEngine, JsonDecisionTablePolicyEngine>();
            services.AddSingleton<IDeterministicPlanBuilder, DeterministicPlanBuilder>();
            services.AddSingleton<IPlanValidator, CatalogPlanValidator>();
            services.AddSingleton<IAiPlanContractParser, AiPlanContractParser>();
            services.AddSingleton<IActionIdempotencyKeyGenerator, ActionIdempotencyKeyGenerator>();
            services.AddSingleton(orchestrator);

            services.AddResilientWebhookHttpClient(
                opt =>
                {
                    opt.MaxRetryAttempts = 1;
                    opt.RetryDelayMs = 10;
                    opt.UseJitter = false;
                    opt.AttemptTimeoutSeconds = 2;
                    opt.SamplingDurationSeconds = 10;
                    opt.BreakDurationSeconds = 5;
                    opt.MinimumThroughput = 10;
                },
                builder => builder.ConfigurePrimaryHttpMessageHandler(() => primaryHandler));

            services.Configure<RabbitMqMessagingOptions>(o =>
            {
                o.RedeliveryMode = "None";
                o.RetryLimit = 1;
                o.RetryMinIntervalMs = 10;
                o.RetryMaxIntervalMs = 20;
                o.RetryIntervalDeltaMs = 5;
            });

            services.AddSingleton<IOperationalAlertSink, LoggingOperationalAlertSink>();
            services.AddSingleton<DeadLetterAlertObserver>();

            services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<IngestAnomalyCommandHandler>());
            services.AddMassTransitTestHarness(x =>
            {
                x.SetTestTimeouts(testTimeout: TimeSpan.FromSeconds(15), testInactivityTimeout: TimeSpan.FromSeconds(2));
                x.SetKebabCaseEndpointNameFormatter();
                x.AddConsumer<AnomalyEventConsumer, AnomalyEventConsumerDefinition>();
                x.AddConsumer<ResolutionApprovedConsumer, ResolutionApprovedConsumerDefinition>();
                x.UsingInMemory((context, cfg) =>
                {
                    cfg.ConnectReceiveObserver(context.GetRequiredService<DeadLetterAlertObserver>());
                    cfg.ConfigureEndpoints(context);
                });
            });

            services.AddLogging();

            var provider = services.BuildServiceProvider();
            var harness = provider.GetRequiredService<ITestHarness>();
            await harness.Start();
            return new HybridTestHost(provider, harness);
        }

        public async Task SeedMedCareAsync(bool autonomousExecutionEnabled)
        {
            var envVar = "OMRO_SEED_MEDCARE_BASE_URL";
            var prev = Environment.GetEnvironmentVariable(envVar);
            Environment.SetEnvironmentVariable(envVar, "https://medcare.test");

            try
            {
                var seedPath = Path.Combine(AppContext.BaseDirectory, "Seeds", "medcare.tenant.json");
                var json = await File.ReadAllTextAsync(seedPath);

                using var scope = Provider.CreateScope();
                var loader = scope.ServiceProvider.GetRequiredService<ITenantSeedLoader>();
                await loader.SeedFromJsonAsync(json);

                if (autonomousExecutionEnabled)
                {
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var entity = await db.TenantConfigs.FirstAsync(t => t.TenantId == MedCareTenantId);
                    entity.SetAutonomousExecution(true);
                    await db.SaveChangesAsync();
                    Provider.GetRequiredService<ITenantConfigProvider>().Invalidate(MedCareTenantId);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(envVar, prev);
            }
        }

        public async Task<ResolutionTask?> WaitForTaskAsync(Func<ResolutionTask, bool> predicate, int timeoutSeconds = 10)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                using var scope = Provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var tasks = await db.ResolutionTasks
                    .Include(t => t.ActionExecutions)
                    .Include(t => t.ReasoningTraces)
                    .AsNoTracking()
                    .ToListAsync();

                var match = tasks.FirstOrDefault(predicate);
                if (match is not null)
                    return match;

                await Task.Delay(30);
            }

            return null;
        }

        public async ValueTask DisposeAsync()
        {
            await Harness.Stop();
            await Provider.DisposeAsync();
        }
    }
}
