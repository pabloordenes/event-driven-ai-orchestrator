using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Omro.Application.Contracts;
using Omro.Application.Interfaces;
using Omro.Application.Policies;
using Omro.Application.ResolutionTasks.Commands.IngestAnomaly;
using Omro.Application.ResolutionTasks.Services;
using Omro.Contracts.Events;
using Omro.Contracts.Plans;
using Omro.Domain.Exceptions;
using Omro.Domain.Repositories;
using Omro.Domain.ResolutionTasks;
using Omro.Domain.Tenants;
using Omro.Infrastructure.Configuration;
using Omro.Infrastructure.Persistence;
using Omro.Infrastructure.Persistence.Repositories;
using Omro.Infrastructure.Tenants;
using Omro.Infrastructure.Validation;
using Omro.Worker.Consumers;
using MassTransit;

namespace Omro.IntegrationTests;

public class WindowedIngestIdempotencyTests
{
    private static readonly ActionIdempotencyKeyGenerator KeyGenerator = new();

    [Fact]
    public async Task SlidingWindow_ShouldDeduplicateInsideWindow_AndAllowNewTaskAfterWindowExpires()
    {
        var startUtc = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(startUtc);
        var tenantId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();

        await using var ctx = await WindowedTestHost.CreateAsync(timeProvider);
        await ctx.SeedTenantAsync(tenantId, tenantDedupMinutes: 60);

        var cmd1 = new IngestAnomalyCommand(tenantId, 0.92m, resourceId, "DelayRisk", Guid.NewGuid());
        var task1Id = await ctx.ExecuteIngestAsync(cmd1);

        // T0 + 30 min (dentro de ventana de 60 min sobre tarea Resolved): debe deduplicar
        timeProvider.Advance(TimeSpan.FromMinutes(30));
        var dupTaskId = await ctx.ExecuteIngestAsync(
            new IngestAnomalyCommand(tenantId, 0.94m, resourceId, "DelayRisk", Guid.NewGuid()));

        dupTaskId.Should().Be(task1Id);
        ctx.DispatchedWebhooks.Should().HaveCount(1);

        // T0 + 61 min (fuera de ventana de 60 min): debe crear una nueva tarea con nuevo Idempotency-Key compacto
        timeProvider.Advance(TimeSpan.FromMinutes(31));
        var task2Id = await ctx.ExecuteIngestAsync(
            new IngestAnomalyCommand(tenantId, 0.95m, resourceId, "DelayRisk", Guid.NewGuid()));

        task2Id.Should().NotBe(task1Id);
        ctx.DispatchedWebhooks.Should().HaveCount(2);

        var key1 = ctx.DispatchedWebhooks[0].IdempotencyKey!;
        var key2 = ctx.DispatchedWebhooks[1].IdempotencyKey!;
        key1.Should().NotBe(key2);
        key1.Length.Should().BeLessThanOrEqualTo(100);
        key2.Length.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public async Task ActiveTask_ShouldNeverBeDuplicatedEvenAfterWindowExpires()
    {
        var startUtc = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(startUtc);
        var tenantId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();

        await using var ctx = await WindowedTestHost.CreateAsync(timeProvider);
        await ctx.SeedTenantAsync(tenantId, tenantDedupMinutes: 60);

        // Score 0.72 -> ProposeForApproval -> WaitingForExternalData (activo)
        var task1Id = await ctx.ExecuteIngestAsync(
            new IngestAnomalyCommand(tenantId, 0.72m, resourceId, "DelayRisk", Guid.NewGuid()));

        using (var verifyScope = ctx.Provider.CreateScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var saved = await db.ResolutionTasks.AsNoTracking().FirstAsync(t => t.Id == task1Id);
            saved.Status.Should().Be(ResolutionStatus.WaitingForExternalData);
        }

        // T0 + 180 min (> ventana 60 min): mientras siga activa en WaitingForExternalData no se duplica
        timeProvider.Advance(TimeSpan.FromMinutes(180));
        var secondResult = await ctx.ExecuteIngestAsync(
            new IngestAnomalyCommand(tenantId, 0.95m, resourceId, "DelayRisk", Guid.NewGuid()));

        secondResult.Should().Be(task1Id);
        ctx.DispatchedWebhooks.Should().ContainSingle();
    }

    [Fact]
    public async Task PolicyLevelDedupWindowOverride_ShouldOverrideTenantDefault_AndDifferentAnomalyTypeShouldBeIndependent()
    {
        var startUtc = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(startUtc);
        var tenantId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();

        await using var ctx = await WindowedTestHost.CreateAsync(timeProvider);
        // Tenant default = 60m, pero política DelayRisk = 15m
        await ctx.SeedTenantAsync(tenantId, tenantDedupMinutes: 60, policyDedupMinutes: 15);

        var delayTask1Id = await ctx.ExecuteIngestAsync(
            new IngestAnomalyCommand(tenantId, 0.91m, resourceId, "DelayRisk", Guid.NewGuid()));

        // Otro AnomalyType sobre el mismo (TenantId, ResourceId) dentro de ventana -> tarea independiente
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        var capacityTaskId = await ctx.ExecuteIngestAsync(
            new IngestAnomalyCommand(tenantId, 0.91m, resourceId, "CapacityRisk", Guid.NewGuid()));
        capacityTaskId.Should().NotBe(delayTask1Id);

        // T0 + 10 min (< 15 min): DelayRisk se deduplica
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        var delayDupId = await ctx.ExecuteIngestAsync(
            new IngestAnomalyCommand(tenantId, 0.92m, resourceId, "DelayRisk", Guid.NewGuid()));
        delayDupId.Should().Be(delayTask1Id);

        // T0 + 16 min (> 15 min override, aún < 60 min default): DelayRisk crea nueva tarea
        timeProvider.Advance(TimeSpan.FromMinutes(6));
        var delayTask2Id = await ctx.ExecuteIngestAsync(
            new IngestAnomalyCommand(tenantId, 0.93m, resourceId, "DelayRisk", Guid.NewGuid()));
        delayTask2Id.Should().NotBe(delayTask1Id);

        ctx.DispatchedWebhooks.Should().HaveCount(3);
    }

    [Fact]
    public async Task DuplicateActiveTaskExceptionRace_ShouldRecoverWinnerTaskAndDispatchZeroWebhooks()
    {
        var startUtc = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(startUtc);
        var tenantId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();

        await using var ctx = await WindowedTestHost.CreateAsync(timeProvider);
        await ctx.SeedTenantAsync(tenantId, tenantDedupMinutes: 60);

        Guid winnerTaskId = Guid.Empty;
        var strictWebhook = new Mock<IWebhookDispatcher>(MockBehavior.Strict);

        using var scope = ctx.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var uow = new RacingDuplicateUnitOfWork(db, async ct =>
        {
            await using var racingDb = new ApplicationDbContext(ctx.DbOptions);
            var winner = new ResolutionTask(
                tenantId,
                new AnomalyEvent(0.92m, resourceId, "DelayRisk", Guid.NewGuid()),
                startUtc.UtcDateTime);
            winner.StartAnalysis();
            winner.ProposePlan(new CognitivePlan("Winner", "Concurrent win", 0.95m, []), 0, 0);
            winner.Resolve("Won race");
            racingDb.ResolutionTasks.Add(winner);
            await racingDb.SaveChangesAsync(ct);
            winnerTaskId = winner.Id;
        });

        var coordinator = new ActionExecutionCoordinator(
            strictWebhook.Object,
            uow,
            NullLogger<ActionExecutionCoordinator>.Instance);

        var handler = new IngestAnomalyCommandHandler(
            scope.ServiceProvider.GetRequiredService<IResolutionTaskRepository>(),
            uow,
            scope.ServiceProvider.GetRequiredService<ICognitiveOrchestrator>(),
            coordinator,
            scope.ServiceProvider.GetRequiredService<ITenantConfigProvider>(),
            scope.ServiceProvider.GetRequiredService<IPolicyEngine>(),
            scope.ServiceProvider.GetRequiredService<IDeterministicPlanBuilder>(),
            scope.ServiceProvider.GetRequiredService<IPlanValidator>(),
            scope.ServiceProvider.GetRequiredService<IShadowDecisionRepository>(),
            NullLogger<IngestAnomalyCommandHandler>.Instance,
            KeyGenerator,
            timeProvider);

        var returnedId = await handler.Handle(
            new IngestAnomalyCommand(tenantId, 0.92m, resourceId, "DelayRisk", Guid.NewGuid()),
            CancellationToken.None);

        returnedId.Should().Be(winnerTaskId);
        strictWebhook.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ImmediateAndDeferredActions_ShouldPersistCatalogHttpMethodAndCompactKeys()
    {
        var startUtc = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(startUtc);
        var tenantId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();

        await using var ctx = await WindowedTestHost.CreateAsync(timeProvider);
        await ctx.SeedTenantAsync(
            tenantId,
            tenantDedupMinutes: 60,
            immediateMethod: "PATCH",
            deferredMethod: "PUT");

        var taskId = await ctx.ExecuteIngestAsync(
            new IngestAnomalyCommand(tenantId, 0.72m, resourceId, "DelayRisk", Guid.NewGuid()));

        var approvedPlan = new StructuredAiPlan(
            SchemaVersion: PlanContractVersions.V2Structured,
            Title: "Deferred Execution",
            Reasoning: "Approved by operator",
            ConfidenceScore: 0.85m,
            OmroTaskId: taskId,
            MatchedRuleId: "hitl-delay",
            PolicyVersion: 1,
            ProposalExpiresAt: startUtc.AddHours(3),
            IsShadowModeFallback: false,
            DeferredActions:
            [
                new DeferredActionContract(
                    "EXECUTE_MITIGATION",
                    "https://tenant.test/api/mitigate",
                    "PUT",
                    System.Text.Json.JsonSerializer.SerializeToElement(new { resourceId = resourceId.ToString() }))
            ]);

        await ctx.ExecuteApprovedAsync(new ResolutionApproved(
            TenantId: tenantId,
            CorrelationId: Guid.NewGuid(),
            ResolutionTaskId: Guid.NewGuid(),
            ResourceId: resourceId,
            AiPlanJson: null,
            SchemaVersion: PlanContractVersions.V2Structured,
            AiPlan: approvedPlan));

        using var verifyScope = ctx.Provider.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await db.ResolutionTasks
            .Include(t => t.ActionExecutions)
            .AsNoTracking()
            .FirstAsync(t => t.Id == taskId);

        saved.Status.Should().Be(ResolutionStatus.Resolved);
        var immediate = saved.ActionExecutions.Should().ContainSingle(a => a.Phase == ActionExecutionPhase.Immediate).Subject;
        immediate.HttpMethod.Should().Be("PATCH");
        immediate.IdempotencyKey.Should().Be(KeyGenerator.Generate(
            taskId, ActionExecutionPhase.Immediate, 0, "PROPOSE_ACTION", "PATCH", "https://tenant.test/api/propose"));
        immediate.IdempotencyKey.Length.Should().BeLessThanOrEqualTo(100);

        var deferred = saved.ActionExecutions.Should().ContainSingle(a => a.Phase == ActionExecutionPhase.Deferred).Subject;
        deferred.HttpMethod.Should().Be("PUT");
        deferred.IdempotencyKey.Should().Be(KeyGenerator.Generate(
            taskId, ActionExecutionPhase.Deferred, 0, "EXECUTE_MITIGATION", "PUT", "https://tenant.test/api/mitigate"));
        deferred.IdempotencyKey.Length.Should().BeLessThanOrEqualTo(100);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public FakeTimeProvider(DateTimeOffset initialUtc) => _utcNow = initialUtc;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private sealed class RacingDuplicateUnitOfWork : IUnitOfWork
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly Func<CancellationToken, Task> _beforeThrow;

        public RacingDuplicateUnitOfWork(ApplicationDbContext dbContext, Func<CancellationToken, Task> beforeThrow)
        {
            _dbContext = dbContext;
            _beforeThrow = beforeThrow;
        }

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            await _beforeThrow(cancellationToken);
            _dbContext.ChangeTracker.Clear();
            throw new DuplicateActiveTaskException("Simulated 23505 IX_RESOLUTION_TASK_active_dedup violation.");
        }
    }

    private sealed class StubOrchestrator : ICognitiveOrchestrator
    {
        public Task<(CognitivePlan Plan, int PromptTokens, int CompletionTokens)> ProposeResolutionAsync(
            AnomalyEvent anomaly,
            string tenantPromptContext,
            CancellationToken cancellationToken = default)
            => Task.FromResult((new CognitivePlan("Plan", "Reason", 0.90m, [], IsFallback: false), 5, 5));
    }

    private sealed class WindowedTestHost : IAsyncDisposable
    {
        public ServiceProvider Provider { get; }
        public DbContextOptions<ApplicationDbContext> DbOptions { get; }
        public List<(string Endpoint, string? IdempotencyKey, Guid? TaskId)> DispatchedWebhooks { get; } = new();

        private WindowedTestHost(ServiceProvider provider, DbContextOptions<ApplicationDbContext> dbOptions)
        {
            Provider = provider;
            DbOptions = dbOptions;
        }

        public static Task<WindowedTestHost> CreateAsync(TimeProvider timeProvider)
        {
            var services = new ServiceCollection();
            var dbName = $"OmroWindowedDedup_{Guid.NewGuid()}";
            var inMemoryProvider = new ServiceCollection().AddEntityFrameworkInMemoryDatabase().BuildServiceProvider();
            var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(dbName)
                .UseInternalServiceProvider(inMemoryProvider)
                .Options;

            var host = new WindowedTestHost(null!, dbOptions);

            services.AddDbContext<ApplicationDbContext>(o =>
                o.UseInMemoryDatabase(dbName).UseInternalServiceProvider(inMemoryProvider));
            services.AddMemoryCache();
            services.Configure<TenantCacheOptions>(_ => { });
            services.Configure<PlanValidationOptions>(_ => { });
            services.Configure<WebhookExecutionOptions>(_ => { });
            services.AddSingleton(timeProvider);
            services.AddSingleton<IActionIdempotencyKeyGenerator, ActionIdempotencyKeyGenerator>();
            services.AddSingleton<IAiPlanContractParser, AiPlanContractParser>();
            services.AddSingleton<ITenantConfigProvider, CachedTenantConfigProvider>();
            services.AddSingleton<IPolicyEngine, JsonDecisionTablePolicyEngine>();
            services.AddSingleton<IDeterministicPlanBuilder, DeterministicPlanBuilder>();
            services.AddSingleton<IPlanValidator, CatalogPlanValidator>();
            services.AddSingleton<ICognitiveOrchestrator, StubOrchestrator>();
            services.AddScoped<IResolutionTaskRepository, ResolutionTaskRepository>();
            services.AddScoped<IActionExecutionRepository, ActionExecutionRepository>();
            services.AddScoped<ITenantConfigRepository, TenantConfigRepository>();
            services.AddScoped<IShadowDecisionRepository, ShadowDecisionRepository>();
            services.AddScoped<IActionExecutionCoordinator, ActionExecutionCoordinator>();
            services.AddScoped<IUnitOfWork, UnitOfWork>();
            services.AddTransient<ResolutionApprovedConsumer>();

            var webhookMock = new Mock<IWebhookDispatcher>();
            webhookMock
                .Setup(w => w.DispatchAsync(
                    It.IsAny<string>(),
                    It.IsAny<Dictionary<string, object>>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<string?>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<WebhookDispatchOptions?>()))
                .Callback<string, Dictionary<string, object>, CancellationToken, string?, Guid?, WebhookDispatchOptions?>((ep, _, _, key, tId, _) =>
                    host.DispatchedWebhooks.Add((ep, key, tId)))
                .ReturnsAsync(WebhookDispatchResult.Succeeded(200, 1));
            services.AddSingleton(webhookMock.Object);

            services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<IngestAnomalyCommandHandler>());
            services.AddLogging();

            var provider = services.BuildServiceProvider();
            var initialized = new WindowedTestHost(provider, dbOptions);

            webhookMock.Reset();
            webhookMock
                .Setup(w => w.DispatchAsync(
                    It.IsAny<string>(),
                    It.IsAny<Dictionary<string, object>>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<string?>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<WebhookDispatchOptions?>()))
                .Callback<string, Dictionary<string, object>, CancellationToken, string?, Guid?, WebhookDispatchOptions?>((ep, _, _, key, tId, _) =>
                    initialized.DispatchedWebhooks.Add((ep, key, tId)))
                .ReturnsAsync(WebhookDispatchResult.Succeeded(200, 1));

            return Task.FromResult(initialized);
        }

        public async Task<Guid> ExecuteIngestAsync(IngestAnomalyCommand command)
        {
            using var scope = Provider.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
            return await handler.Send(command);
        }

        public async Task ExecuteApprovedAsync(ResolutionApproved approved)
        {
            using var scope = Provider.CreateScope();
            var consumer = scope.ServiceProvider.GetRequiredService<ResolutionApprovedConsumer>();
            var ctxMock = new Mock<ConsumeContext<ResolutionApproved>>();
            ctxMock.SetupGet(c => c.Message).Returns(approved);
            ctxMock.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
            await consumer.Consume(ctxMock.Object);
        }

        public async Task SeedTenantAsync(
            Guid tenantId,
            int tenantDedupMinutes,
            int? policyDedupMinutes = null,
            string immediateMethod = "POST",
            string deferredMethod = "POST")
        {
            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var tenant = TenantConfig.Create(
                tenantId,
                $"tenant-{tenantId:N}",
                "Generic Logistics Tenant",
                "Generic operations prompt",
                "logistics-api",
                autonomousExecutionEnabled: true,
                dedupWindowMinutes: tenantDedupMinutes,
                allowedEndpointHostsJson: "[\"https://tenant.test\"]");

            tenant.AddAction(TenantAction.Create(
                tenantId,
                "EXECUTE_MITIGATION",
                "Execute mitigation",
                "https://tenant.test/api/mitigate",
                httpMethod: deferredMethod,
                defaultPayloadTemplateJson: "{\"resourceId\":\"{{resourceId}}\"}",
                requiresApproval: false,
                allowedPhases: TenantActionPhase.Both));

            tenant.AddAction(TenantAction.Create(
                tenantId,
                "PROPOSE_ACTION",
                "Propose HITL",
                "https://tenant.test/api/propose",
                httpMethod: immediateMethod,
                defaultPayloadTemplateJson: "{\"resourceId\":\"{{resourceId}}\"}",
                requiresApproval: true,
                allowedPhases: TenantActionPhase.Immediate));

            const string decisionTable = """
            {
              "hitPolicy": "FIRST_MATCH",
              "rules": [
                {
                  "ruleId": "auto-high",
                  "priority": 10,
                  "when": { "field": "riskProbability", "op": "gte", "value": 0.85 },
                  "then": {
                    "outcome": "ExecuteActions",
                    "expectsExternalApproval": false,
                    "invokeLlmEnrichment": true,
                    "actions": [{ "actionType": "EXECUTE_MITIGATION" }]
                  }
                },
                {
                  "ruleId": "hitl-delay",
                  "priority": 20,
                  "when": { "field": "riskProbability", "op": "range_half_open", "minInclusive": 0.50, "maxExclusive": 0.85 },
                  "then": {
                    "outcome": "ProposeForApproval",
                    "expectsExternalApproval": true,
                    "invokeLlmEnrichment": false,
                    "actions": [{
                      "actionType": "PROPOSE_ACTION",
                      "deferredActions": [{ "actionType": "EXECUTE_MITIGATION" }]
                    }]
                  }
                }
              ],
              "defaultDecision": { "outcome": "NoAction", "invokeLlmEnrichment": false, "actions": [] }
            }
            """;

            tenant.AddPolicy(TenantPolicy.Create(
                tenantId,
                "DelayRisk",
                decisionTable,
                dedupWindowMinutes: policyDedupMinutes));

            tenant.AddPolicy(TenantPolicy.Create(
                tenantId,
                "CapacityRisk",
                decisionTable));

            db.TenantConfigs.Add(tenant);
            await db.SaveChangesAsync();
            Provider.GetRequiredService<ITenantConfigProvider>().Invalidate(tenantId);
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
        }
    }
}
