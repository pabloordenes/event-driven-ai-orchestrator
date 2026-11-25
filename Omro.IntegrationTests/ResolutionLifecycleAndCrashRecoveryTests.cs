using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Omro.Application.Contracts;
using Omro.Application.Interfaces;
using Omro.Application.Policies;
using Omro.Application.ResolutionTasks.Commands.IngestAnomaly;
using Omro.Application.ResolutionTasks.Services;
using Omro.Contracts.Events;
using Omro.Domain.Repositories;
using Omro.Domain.ResolutionTasks;
using Omro.Domain.Tenants;
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

public class ResolutionLifecycleAndCrashRecoveryTests
{
    [Fact]
    public async Task AutonomousFlow_WhenImmediateWebhookSucceeds_ShouldPersistActionExecutionAndResolveTask()
    {
        var handler = new DelegateHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") });

        var orchestrator = new StubCognitiveOrchestrator((anomaly, _) => new CognitivePlan(
            Title: "Overbook",
            Reasoning: "High risk",
            ConfidenceScore: 0.95m,
            ProposedActions:
            [
                new ProposedAction("GENERATE_OVERBOOK", "https://tenant.test/api/overbook", new Dictionary<string, object>
                {
                    ["resourceId"] = anomaly.ResourceId
                })
            ],
            ExpectsExternalApproval: false));

        await using var ctx = await LifecycleTestHost.CreateAsync(handler, orchestrator);
        var tenantId = Guid.NewGuid();
        await ctx.SeedTenantAsync(tenantId);
        var resourceId = Guid.NewGuid();

        await ctx.Harness.Bus.Publish(new AnomalyDetected(
            TenantId: tenantId,
            CorrelationId: Guid.NewGuid(),
            AnomalyType: "AppointmentNoShowRisk",
            ResourceId: resourceId,
            RiskProbability: 0.93m,
            Attributes: new Dictionary<string, string>()));

        var task = await ctx.WaitForTaskAsync(
            t => t.Anomaly.ResourceId == resourceId && t.Status == ResolutionStatus.Resolved);

        task.Should().NotBeNull();
        task!.Status.Should().Be(ResolutionStatus.Resolved);
        task.ResolvedAt.Should().NotBeNull();
        task.ActionExecutions.Should().ContainSingle();

        var exec = task.ActionExecutions[0];
        exec.Phase.Should().Be(ActionExecutionPhase.Immediate);
        exec.Status.Should().Be(ActionExecutionStatus.Succeeded);
        exec.HttpStatus.Should().Be(200);
        exec.Attempts.Should().Be(1);
        exec.ExecutedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Permanent4xxFailure_ShouldRecordFailedPermanentAndCloseTaskAsFailedWithoutRetries()
    {
        int wireHits = 0;
        var handler = new DelegateHttpMessageHandler((_, _) =>
        {
            Interlocked.Increment(ref wireHits);
            return new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = new StringContent("invalid domain state")
            };
        });

        var orchestrator = new StubCognitiveOrchestrator((anomaly, _) => new CognitivePlan(
            Title: "Overbook",
            Reasoning: "High risk",
            ConfidenceScore: 0.95m,
            ProposedActions:
            [
                new ProposedAction("GENERATE_OVERBOOK", "https://tenant.test/api/overbook", new Dictionary<string, object>
                {
                    ["resourceId"] = anomaly.ResourceId
                })
            ]));

        await using var ctx = await LifecycleTestHost.CreateAsync(handler, orchestrator, retryLimit: 3);
        var tenantId = Guid.NewGuid();
        await ctx.SeedTenantAsync(tenantId);
        var resourceId = Guid.NewGuid();

        await ctx.Harness.Bus.Publish(new AnomalyDetected(
            TenantId: tenantId,
            CorrelationId: Guid.NewGuid(),
            AnomalyType: "AppointmentNoShowRisk",
            ResourceId: resourceId,
            RiskProbability: 0.93m,
            Attributes: new Dictionary<string, string>()));

        var task = await ctx.WaitForTaskAsync(
            t => t.Anomaly.ResourceId == resourceId && t.Status == ResolutionStatus.Failed);

        task.Should().NotBeNull();
        task!.Status.Should().Be(ResolutionStatus.Failed);
        task.FailureReason.Should().Contain("422");
        task.ActionExecutions.Should().ContainSingle();
        task.ActionExecutions[0].Status.Should().Be(ActionExecutionStatus.FailedPermanent);
        task.ActionExecutions[0].Attempts.Should().Be(1);
        wireHits.Should().Be(1);
    }

    [Fact]
    public async Task CrashRecovery_WhenWorkerDiedAfterAction0_ResumesWithoutCallingGroqOrRepeatingAction0()
    {
        var dispatchedPaths = new ConcurrentBag<(string Path, string? IdempotencyKey)>();
        var handler = new DelegateHttpMessageHandler((req, _) =>
        {
            req.Headers.TryGetValues("Idempotency-Key", out var keys);
            dispatchedPaths.Add((req.RequestUri!.AbsolutePath, keys?.FirstOrDefault()));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        int groqCalls = 0;
        var orchestrator = new StubCognitiveOrchestrator((_, _) =>
        {
            Interlocked.Increment(ref groqCalls);
            throw new InvalidOperationException("Groq no debe volver a llamarse durante crash recovery.");
        });

        await using var ctx = await LifecycleTestHost.CreateAsync(handler, orchestrator);

        var tenantId = Guid.NewGuid();
        await ctx.SeedTenantAsync(tenantId);
        var resourceId = Guid.NewGuid();
        Guid taskId;
        string action1Key;

        // Simulamos un crash previo: la tarea ya existe en ActionProposed, Acción 0 Succeeded, Acción 1 quedó InProgress
        using (var scope = ctx.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var seeded = new ResolutionTask(tenantId, new AnomalyEvent(0.94m, resourceId, "AppointmentNoShowRisk", Guid.NewGuid()));
            seeded.StartAnalysis();
            seeded.ProposePlan(new CognitivePlan(
                Title: "Dual step",
                Reasoning: "Test crash recovery",
                ConfidenceScore: 0.99m,
                ProposedActions:
                [
                    new ProposedAction("STEP_0", "https://tenant.test/api/step0", new Dictionary<string, object> { ["step"] = 0 }),
                    new ProposedAction("STEP_1", "https://tenant.test/api/step1", new Dictionary<string, object> { ["step"] = 1 })
                ]), 10, 20);

            seeded.ActionExecutions[0].MarkInProgress();
            seeded.ActionExecutions[0].MarkSucceeded(200, 1, DateTime.UtcNow.AddMinutes(-1));
            seeded.ActionExecutions[1].MarkInProgress(); // interrumpido antes de recibir respuesta

            taskId = seeded.Id;
            action1Key = seeded.ActionExecutions[1].IdempotencyKey;

            db.ResolutionTasks.Add(seeded);
            await db.SaveChangesAsync();
        }

        // RabbitMQ re-entrega el mensaje original tras reinicio del worker
        await ctx.Harness.Bus.Publish(new AnomalyDetected(
            TenantId: tenantId,
            CorrelationId: Guid.NewGuid(),
            AnomalyType: "AppointmentNoShowRisk",
            ResourceId: resourceId,
            RiskProbability: 0.94m,
            Attributes: new Dictionary<string, string>()));

        var recovered = await ctx.WaitForTaskAsync(
            t => t.Id == taskId && t.Status == ResolutionStatus.Resolved);

        recovered.Should().NotBeNull();
        recovered!.Status.Should().Be(ResolutionStatus.Resolved);
        groqCalls.Should().Be(0);
        dispatchedPaths.Should().ContainSingle();
        dispatchedPaths.Single().Path.Should().Be("/api/step1");
        dispatchedPaths.Single().IdempotencyKey.Should().Be(action1Key);
        recovered.ActionExecutions.Should().HaveCount(2);
        recovered.ActionExecutions.Should().OnlyContain(e => e.Status == ActionExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task HitlFullLifecycle_ShouldEnterWaitingForExternalData_AndResolveOnApprovedEvent()
    {
        var hitPaths = new ConcurrentBag<string>();
        var handler = new DelegateHttpMessageHandler((req, _) =>
        {
            hitPaths.Add(req.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var orchestrator = new StubCognitiveOrchestrator((anomaly, _) => new CognitivePlan(
            Title: "HITL Proposal",
            Reasoning: "Moderate risk requires approval",
            ConfidenceScore: 0.80m,
            ProposedActions:
            [
                new ProposedAction("PROPOSE_RESOLUTION", "https://tenant.test/api/resolutions/propose", new Dictionary<string, object>
                {
                    ["resourceId"] = anomaly.ResourceId
                })
            ],
            ExpectsExternalApproval: true));

        await using var ctx = await LifecycleTestHost.CreateAsync(handler, orchestrator);

        var tenantId = Guid.NewGuid();
        await ctx.SeedTenantAsync(tenantId);
        var resourceId = Guid.NewGuid();

        await ctx.Harness.Bus.Publish(new AnomalyDetected(
            TenantId: tenantId,
            CorrelationId: Guid.NewGuid(),
            AnomalyType: "AppointmentNoShowRisk",
            ResourceId: resourceId,
            RiskProbability: 0.68m,
            Attributes: new Dictionary<string, string>()));

        var waitingTask = await ctx.WaitForTaskAsync(
            t => t.Anomaly.ResourceId == resourceId && t.Status == ResolutionStatus.WaitingForExternalData);

        waitingTask.Should().NotBeNull();
        waitingTask!.Status.Should().Be(ResolutionStatus.WaitingForExternalData);
        waitingTask.ResolvedAt.Should().BeNull();

        // MedCareOS publica ResolutionApproved con su propio TaskId interno distinto al Id de OMRO
        var externalTaskId = Guid.NewGuid();
        await ctx.Harness.Bus.Publish(new ResolutionApproved(
            TenantId: tenantId,
            CorrelationId: Guid.NewGuid(),
            ResolutionTaskId: externalTaskId,
            ResourceId: resourceId,
            AiPlanJson: """
                        {
                          "DeferredActions": [
                            {
                              "Endpoint": "https://tenant.test/api/appointments/monitor",
                              "Payload": { "requiresAction": true }
                            }
                          ]
                        }
                        """));

        var resolvedTask = await ctx.WaitForTaskAsync(
            t => t.Id == waitingTask.Id && t.Status == ResolutionStatus.Resolved);

        resolvedTask.Should().NotBeNull();
        resolvedTask!.Status.Should().Be(ResolutionStatus.Resolved);
        resolvedTask.ResolvedAt.Should().NotBeNull();
        resolvedTask.ActionExecutions.Should().HaveCount(2);
        resolvedTask.ActionExecutions.Should().Contain(e =>
            e.Phase == ActionExecutionPhase.Immediate && e.Status == ActionExecutionStatus.Succeeded);

        var expectedDeferredKey = new ActionIdempotencyKeyGenerator().Generate(
            waitingTask.Id,
            ActionExecutionPhase.Deferred,
            0,
            "MONITOR_RESOURCE",
            "POST",
            "https://tenant.test/api/appointments/monitor");
        expectedDeferredKey.Length.Should().BeLessThanOrEqualTo(100);

        resolvedTask.ActionExecutions.Should().Contain(e =>
            e.Phase == ActionExecutionPhase.Deferred &&
            e.Status == ActionExecutionStatus.Succeeded &&
            e.IdempotencyKey == expectedDeferredKey);

        hitPaths.Should().Contain("/api/resolutions/propose");
        hitPaths.Should().Contain("/api/appointments/monitor");
    }

    [Fact]
    public async Task Transient503Failure_WhenMassTransitRetries_ShouldAccumulateAttemptsAndResolveTask()
    {
        int wireHits = 0;
        var handler = new DelegateHttpMessageHandler((_, _) =>
        {
            var attempt = Interlocked.Increment(ref wireHits);
            // Polly MaxRetryAttempts=1 ejecuta 2 wire hits por intento de consumer:
            // intento consumer 1 (hits 1,2 -> 503), intento consumer 2 (hit 3 -> 200 OK)
            return attempt < 3
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK);
        });

        var orchestrator = new StubCognitiveOrchestrator((anomaly, _) => new CognitivePlan(
            Title: "Overbook",
            Reasoning: "High risk",
            ConfidenceScore: 0.91m,
            ProposedActions:
            [
                new ProposedAction("GENERATE_OVERBOOK", "https://tenant.test/api/overbook", new Dictionary<string, object>
                {
                    ["resourceId"] = anomaly.ResourceId
                })
            ]));

        await using var ctx = await LifecycleTestHost.CreateAsync(handler, orchestrator, retryLimit: 2, pollyRetries: 1);
        var tenantId = Guid.NewGuid();
        await ctx.SeedTenantAsync(tenantId);
        var resourceId = Guid.NewGuid();

        await ctx.Harness.Bus.Publish(new AnomalyDetected(
            TenantId: tenantId,
            CorrelationId: Guid.NewGuid(),
            AnomalyType: "AppointmentNoShowRisk",
            ResourceId: resourceId,
            RiskProbability: 0.91m,
            Attributes: new Dictionary<string, string>()));

        var resolved = await ctx.WaitForTaskAsync(
            t => t.Anomaly.ResourceId == resourceId && t.Status == ResolutionStatus.Resolved);

        resolved.Should().NotBeNull();
        resolved!.Status.Should().Be(ResolutionStatus.Resolved);
        wireHits.Should().Be(3);
        resolved.ActionExecutions.Should().ContainSingle();
        resolved.ActionExecutions[0].Status.Should().Be(ActionExecutionStatus.Succeeded);
        resolved.ActionExecutions[0].Attempts.Should().Be(3); // 2 fallidos + 1 exitoso
    }

    private sealed class StubCognitiveOrchestrator : ICognitiveOrchestrator
    {
        private readonly Func<AnomalyEvent, string, CognitivePlan> _fn;

        public StubCognitiveOrchestrator(Func<AnomalyEvent, string, CognitivePlan> fn) => _fn = fn;

        public Task<(CognitivePlan Plan, int PromptTokens, int CompletionTokens)> ProposeResolutionAsync(
            AnomalyEvent anomaly,
            string tenantPromptContext,
            CancellationToken cancellationToken = default)
            => Task.FromResult((_fn(anomaly, tenantPromptContext), 10, 20));
    }

    private sealed class DelegateHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _fn;

        public DelegateHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> fn)
            => _fn = fn;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_fn(request, cancellationToken));
    }

    private sealed class LifecycleTestHost : IAsyncDisposable
    {
        public ServiceProvider Provider { get; }
        public ITestHarness Harness { get; }

        private LifecycleTestHost(ServiceProvider provider, ITestHarness harness)
        {
            Provider = provider;
            Harness = harness;
        }

        public static async Task<LifecycleTestHost> CreateAsync(
            HttpMessageHandler primaryHandler,
            ICognitiveOrchestrator orchestrator,
            int retryLimit = 2,
            int pollyRetries = 1)
        {
            var services = new ServiceCollection();
            var dbName = $"OmroLifecycleDb_{Guid.NewGuid()}";
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
            services.AddSingleton<IPolicyEngine, JsonDecisionTablePolicyEngine>();
            services.AddSingleton<IDeterministicPlanBuilder, DeterministicPlanBuilder>();
            services.AddSingleton<IPlanValidator, CatalogPlanValidator>();
            services.AddSingleton<IAiPlanContractParser, AiPlanContractParser>();
            services.AddSingleton<IActionIdempotencyKeyGenerator, ActionIdempotencyKeyGenerator>();
            services.AddSingleton(orchestrator);

            services.AddResilientWebhookHttpClient(
                opt =>
                {
                    opt.MaxRetryAttempts = pollyRetries;
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
                o.RetryLimit = retryLimit;
                o.RetryMinIntervalMs = 15;
                o.RetryMaxIntervalMs = 40;
                o.RetryIntervalDeltaMs = 10;
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

            return new LifecycleTestHost(provider, harness);
        }

        public async Task SeedTenantAsync(Guid tenantId)
        {
            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if (await db.TenantConfigs.AnyAsync(t => t.TenantId == tenantId))
                return;

            var tenant = TenantConfig.Create(
                tenantId,
                $"tenant-{tenantId:N}",
                "Test Tenant",
                "Test prompt context",
                "tenant-api",
                autonomousExecutionEnabled: true,
                allowedEndpointHostsJson: "[\"https://tenant.test\"]");

            tenant.AddAction(TenantAction.Create(
                tenantId,
                "GENERATE_OVERBOOK",
                "Autonomous overbook",
                "https://tenant.test/api/overbook",
                defaultPayloadTemplateJson: "{\"resourceId\":\"{{resourceId}}\"}"));

            tenant.AddAction(TenantAction.Create(
                tenantId,
                "PROPOSE_RESOLUTION",
                "HITL proposal",
                "https://tenant.test/api/resolutions/propose",
                defaultPayloadTemplateJson: "{\"resourceId\":\"{{resourceId}}\"}",
                requiresApproval: true,
                allowedPhases: TenantActionPhase.Immediate));

            tenant.AddAction(TenantAction.Create(
                tenantId,
                "MONITOR_RESOURCE",
                "Deferred monitor",
                "https://tenant.test/api/appointments/monitor",
                defaultPayloadTemplateJson: "{\"requiresAction\":true}"));

            tenant.AddPolicy(TenantPolicy.Create(
                tenantId,
                "AppointmentNoShowRisk",
                """
                {
                  "hitPolicy": "FIRST_MATCH",
                  "rules": [
                    {
                      "ruleId": "high-risk-auto",
                      "priority": 10,
                      "when": { "field": "riskProbability", "op": "gte", "value": 0.85 },
                      "then": {
                        "outcome": "ExecuteActions",
                        "expectsExternalApproval": false,
                        "invokeLlmEnrichment": true,
                        "actions": [{ "actionType": "GENERATE_OVERBOOK" }]
                      }
                    },
                    {
                      "ruleId": "moderate-risk-hitl",
                      "priority": 20,
                      "when": { "field": "riskProbability", "op": "range_half_open", "minInclusive": 0.50, "maxExclusive": 0.85 },
                      "then": {
                        "outcome": "ProposeForApproval",
                        "expectsExternalApproval": true,
                        "invokeLlmEnrichment": true,
                        "actions": [{ "actionType": "PROPOSE_RESOLUTION" }]
                      }
                    }
                  ],
                  "defaultDecision": { "outcome": "NoAction", "invokeLlmEnrichment": false, "actions": [] }
                }
                """));

            db.TenantConfigs.Add(tenant);
            await db.SaveChangesAsync();
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
