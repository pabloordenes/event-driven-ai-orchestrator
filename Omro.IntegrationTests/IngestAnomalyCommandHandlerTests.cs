using System.Text.Json;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Omro.Application.Contracts;
using Omro.Application.Interfaces;
using Omro.Application.Policies;
using Omro.Application.ResolutionTasks.Commands.IngestAnomaly;
using Omro.Application.ResolutionTasks.Services;
using Omro.Application.Tenants;
using Omro.Contracts.Events;
using Omro.Contracts.Plans;
using Omro.Domain.Repositories;
using Omro.Domain.ResolutionTasks;
using Omro.Domain.Tenants;
using Omro.Infrastructure.Configuration;
using Omro.Infrastructure.Messaging.Configuration;
using Omro.Infrastructure.Messaging.Observers;
using Omro.Infrastructure.Persistence;
using Omro.Infrastructure.Persistence.Repositories;
using Omro.Infrastructure.Tenants;
using Omro.Infrastructure.Validation;
using Omro.Worker.Consumers;

namespace Omro.IntegrationTests;

public class IngestAnomalyCommandHandlerTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private ITestHarness _harness = null!;
    private Moq.Mock<IWebhookDispatcher> _webhookMock = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        var sharedDbName = Guid.NewGuid().ToString();

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase(sharedDbName));
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

        _webhookMock = new Moq.Mock<IWebhookDispatcher>();
        _webhookMock
            .Setup(w => w.DispatchAsync(
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<Dictionary<string, object>>(),
                Moq.It.IsAny<CancellationToken>(),
                Moq.It.IsAny<string?>(),
                Moq.It.IsAny<Guid?>(),
                Moq.It.IsAny<WebhookDispatchOptions?>()))
            .ReturnsAsync(WebhookDispatchResult.Succeeded(200, 1));
        services.AddSingleton(_webhookMock.Object);

        services.AddScoped<ICognitiveOrchestrator, FakeCognitiveOrchestrator>();

        services.Configure<RabbitMqMessagingOptions>(o =>
        {
            o.RedeliveryMode = "None";
            o.RetryLimit = 1;
            o.RetryMinIntervalMs = 15;
            o.RetryMaxIntervalMs = 30;
            o.RetryIntervalDeltaMs = 10;
        });
        services.AddSingleton<IOperationalAlertSink, LoggingOperationalAlertSink>();
        services.AddSingleton<DeadLetterAlertObserver>();

        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssemblyContaining<IngestAnomalyCommandHandler>());

        services.AddMassTransitTestHarness(x =>
        {
            x.SetTestTimeouts(testTimeout: TimeSpan.FromSeconds(15), testInactivityTimeout: TimeSpan.FromSeconds(2));
            x.AddConsumer<AnomalyEventConsumer, AnomalyEventConsumerDefinition>();
            x.AddConsumer<ResolutionApprovedConsumer, ResolutionApprovedConsumerDefinition>();
        });

        services.AddLogging();

        _provider = services.BuildServiceProvider();
        _harness = _provider.GetRequiredService<ITestHarness>();

        await _harness.Start();
    }

    public async Task DisposeAsync()
    {
        await _harness.Stop();
        if (_provider != null) await _provider.DisposeAsync();
    }

    [Fact]
    public async Task Should_Consume_AnomalyEvent_And_Save_ResolutionTask_To_Database()
    {
        // 1. ARRANGE
        var tenantId = Guid.NewGuid();
        await SeedTenantAsync(tenantId);

        var anomalyEvent = new AnomalyDetected(
            TenantId: tenantId,
            CorrelationId: Guid.NewGuid(),
            AnomalyType: "AppointmentNoShowRisk",
            ResourceId: Guid.NewGuid(),
            RiskProbability: 0.85m,
            Attributes: new Dictionary<string, string>()
        );

        // 2. ACT: Publicamos el contrato unificado de MedCareOS en el bus virtual
        await _harness.Bus.Publish(anomalyEvent);

        // 3. ASSERT
        var isConsumedByBus = await _harness.Consumed.Any<AnomalyDetected>(
            c => c.Context.Message.ResourceId == anomalyEvent.ResourceId);
        isConsumedByBus.Should().BeTrue("El mensaje AnomalyDetected debió ser enrutado por URN.");

        var consumerHarness = _harness.GetConsumerHarness<AnomalyEventConsumer>();
        var isConsumedByHandler = await consumerHarness.Consumed.Any<AnomalyDetected>(
            c => c.Context.Message.ResourceId == anomalyEvent.ResourceId);
        isConsumedByHandler.Should().BeTrue("El AnomalyEventConsumer debió procesar el mensaje sin excepciones.");

        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var saved = await dbContext.ResolutionTasks
            .FirstOrDefaultAsync(t => t.TenantId == anomalyEvent.TenantId && t.Anomaly.ResourceId == anomalyEvent.ResourceId);
        saved.Should().NotBeNull("El handler de MediatR debió persistir en la base de datos.");
    }

    [Fact]
    public async Task Should_Deduplicate_RedeliveredAnomalyDetected_ByTenantResourceAndType()
    {
        var tenantId = Guid.NewGuid();
        await SeedTenantAsync(tenantId);
        var resourceId = Guid.NewGuid();
        var anomaly = new AnomalyDetected(
            TenantId: tenantId,
            CorrelationId: Guid.NewGuid(),
            AnomalyType: "AppointmentNoShowRisk",
            ResourceId: resourceId,
            RiskProbability: 0.92m,
            Attributes: new Dictionary<string, string>());

        await _harness.Bus.Publish(anomaly);
        (await _harness.Consumed.Any<AnomalyDetected>(c => c.Context.Message.ResourceId == resourceId)).Should().BeTrue();

        // Segundo envío simulando redelivery de RabbitMQ
        await _harness.Bus.Publish(anomaly with { CorrelationId = Guid.NewGuid() });
        var consumerHarness = _harness.GetConsumerHarness<AnomalyEventConsumer>();
        var consumedMessages = await consumerHarness.Consumed.SelectAsync<AnomalyDetected>(
            c => c.Context.Message.ResourceId == resourceId).ToListAsync();
        consumedMessages.Should().HaveCount(2);

        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var tasks = await dbContext.ResolutionTasks
            .Where(t => t.TenantId == tenantId && t.Anomaly.ResourceId == resourceId)
            .ToListAsync();

        tasks.Should().HaveCount(1);
        var expectedKey = new ActionIdempotencyKeyGenerator().Generate(
            tasks[0].Id,
            ActionExecutionPhase.Immediate,
            0,
            "GENERATE_OVERBOOK",
            "POST",
            "https://medcare.test/api/appointments/overbook");
        expectedKey.Length.Should().BeLessThanOrEqualTo(100);

        _webhookMock.Verify(w => w.DispatchAsync(
            "https://medcare.test/api/appointments/overbook",
            Moq.It.IsAny<Dictionary<string, object>>(),
            Moq.It.IsAny<CancellationToken>(),
            expectedKey,
            Moq.It.IsAny<Guid?>(),
            Moq.It.IsAny<WebhookDispatchOptions?>()), Moq.Times.Once);
    }

    [Fact]
    public async Task Should_Consume_ResolutionApproved_And_DispatchDeferredActionsWithIdempotencyKey()
    {
        var tenantId = Guid.NewGuid();
        await SeedTenantAsync(tenantId);
        var taskId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var approvedEvent = new ResolutionApproved(
            TenantId: tenantId,
            CorrelationId: Guid.NewGuid(),
            ResolutionTaskId: taskId,
            ResourceId: resourceId,
            AiPlanJson: """
                        {
                          "DeferredActions": [
                            {
                              "Endpoint": "https://medcare.test/api/appointments/monitor",
                              "Payload": { "resourceId": "abc", "requiresAction": true }
                            }
                          ]
                        }
                        """);

        await _harness.Bus.Publish(approvedEvent);

        var consumerHarness = _harness.GetConsumerHarness<ResolutionApprovedConsumer>();
        var consumed = await consumerHarness.Consumed.Any<ResolutionApproved>(
            c => c.Context.Message.ResolutionTaskId == taskId);
        consumed.Should().BeTrue();

        var expectedDeferredKey = new ActionIdempotencyKeyGenerator().Generate(
            taskId,
            ActionExecutionPhase.Deferred,
            0,
            "MONITOR_RESOURCE",
            "POST",
            "https://medcare.test/api/appointments/monitor");
        expectedDeferredKey.Length.Should().BeLessThanOrEqualTo(100);

        _webhookMock.Verify(w => w.DispatchAsync(
            "https://medcare.test/api/appointments/monitor",
            Moq.It.IsAny<Dictionary<string, object>>(),
            Moq.It.IsAny<CancellationToken>(),
            expectedDeferredKey,
            Moq.It.IsAny<Guid?>(),
            Moq.It.IsAny<WebhookDispatchOptions?>()), Moq.Times.Once);
    }

    [Fact]
    public async Task ConfirmationDeadlineMissed_WithAutoOverbookOff_ShouldRecordShadowDecision_ProposeV2_AndExecuteOnV2Approval()
    {
        var medCareTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        await SeedFromMedCareFileAsync(autonomousExecutionEnabled: false);

        var resourceId = Guid.NewGuid();
        var anomaly = new AnomalyDetected(
            TenantId: medCareTenantId,
            CorrelationId: Guid.NewGuid(),
            AnomalyType: "ConfirmationDeadlineMissed",
            ResourceId: resourceId,
            RiskProbability: 0.90m,
            Attributes: new Dictionary<string, string>
            {
                ["final_notice_status"] = "Delivered",
                ["clinical_flags"] = string.Empty,
                ["auto_overbook_enabled"] = "true"
            });

        await _harness.Bus.Publish(anomaly);

        var anomalyConsumerHarness = _harness.GetConsumerHarness<AnomalyEventConsumer>();
        (await anomalyConsumerHarness.Consumed.Any<AnomalyDetected>(
            c => c.Context.Message.ResourceId == resourceId && c.Exception is null)).Should().BeTrue();

        Guid taskId;
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var task = await db.ResolutionTasks
                .Include(t => t.ActionExecutions)
                .FirstOrDefaultAsync(t => t.TenantId == medCareTenantId && t.Anomaly.ResourceId == resourceId);

            task.Should().NotBeNull();
            task!.Status.Should().Be(ResolutionStatus.WaitingForExternalData);
            task.ExpectsExternalApproval.Should().BeTrue();
            task.ProposalExpiresAt.Should().NotBeNull();
            taskId = task.Id;

            var shadow = await db.ShadowDecisions.FirstOrDefaultAsync(s => s.ResolutionTaskId == taskId);
            shadow.Should().NotBeNull();
            shadow!.IntendedActionType.Should().Be("GENERATE_OVERBOOK");
            shadow.ActualActionType.Should().Be("PROPOSE_RESOLUTION");
        }

        var proposeInvocation = _webhookMock.Invocations.Should().ContainSingle(inv =>
            inv.Method.Name == nameof(IWebhookDispatcher.DispatchAsync) &&
            Equals(inv.Arguments[4], taskId)).Subject;

        proposeInvocation.Arguments[0].Should().Be("https://medcare.test/api/resolutions/propose");
        var idempotencyKey = proposeInvocation.Arguments[3] as string;
        idempotencyKey.Should().NotBeNullOrWhiteSpace();
        idempotencyKey!.Length.Should().BeLessThanOrEqualTo(100);

        var proposePayload = (Dictionary<string, object>)proposeInvocation.Arguments[1]!;
        ((JsonElement)proposePayload["schemaVersion"]).GetString().Should().Be(PlanContractVersions.V2Structured);
        ((JsonElement)proposePayload["proposalExpiresAt"]).GetDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(150));

        var aiPlanElement = (JsonElement)proposePayload["aiPlan"];
        aiPlanElement.ValueKind.Should().Be(JsonValueKind.Object);
        var structuredPlan = JsonSerializer.Deserialize<StructuredAiPlan>(
            aiPlanElement.GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        structuredPlan.Should().NotBeNull();
        structuredPlan!.Title.Should().Be("Plan Enriquecido por FakeLLM");
        structuredPlan.OmroTaskId.Should().Be(taskId);
        structuredPlan.IsShadowModeFallback.Should().BeTrue();
        structuredPlan.DeferredActions.Should().ContainSingle(a => a.ActionType == "GENERATE_OVERBOOK");

        // Aprobación HITL con contrato v2 nativo (AiPlanJson == null)
        var approvalCorrelation = Guid.NewGuid();
        await _harness.Bus.Publish(new ResolutionApproved(
            TenantId: medCareTenantId,
            CorrelationId: approvalCorrelation,
            ResolutionTaskId: Guid.NewGuid(),
            ResourceId: resourceId,
            AiPlanJson: null,
            SchemaVersion: PlanContractVersions.V2Structured,
            AiPlan: structuredPlan));

        var approvedConsumerHarness = _harness.GetConsumerHarness<ResolutionApprovedConsumer>();
        (await approvedConsumerHarness.Consumed.Any<ResolutionApproved>(
            c => c.Context.Message.CorrelationId == approvalCorrelation && c.Exception is null)).Should().BeTrue();

        var overbookInvocation = _webhookMock.Invocations.Should().ContainSingle(inv =>
            inv.Method.Name == nameof(IWebhookDispatcher.DispatchAsync) &&
            Equals(inv.Arguments[0], "https://medcare.test/api/appointments/overbook") &&
            Equals(inv.Arguments[4], taskId)).Subject;

        var deferredKey = overbookInvocation.Arguments[3] as string;
        deferredKey.Should().NotBeNullOrWhiteSpace();
        deferredKey!.Length.Should().BeLessThanOrEqualTo(100);
        deferredKey.Should().NotBe(idempotencyKey);

        var overbookPayload = (Dictionary<string, object>)overbookInvocation.Arguments[1]!;
        ((JsonElement)overbookPayload["decisionMode"]).GetString().Should().Be("Approved");

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var resolved = await db.ResolutionTasks.FirstAsync(t => t.Id == taskId);
            resolved.Status.Should().Be(ResolutionStatus.Resolved);
        }
    }

    [Fact]
    public async Task ConfirmationDeadlineMissed_WithAutoOverbookOn_ShouldExecuteAutonomousOverbook_AndBlockLlmHijack()
    {
        var medCareTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        await SeedFromMedCareFileAsync(autonomousExecutionEnabled: true);

        var resourceId = Guid.NewGuid();
        var anomaly = new AnomalyDetected(
            TenantId: medCareTenantId,
            CorrelationId: Guid.NewGuid(),
            AnomalyType: "ConfirmationDeadlineMissed",
            ResourceId: resourceId,
            RiskProbability: 0.92m,
            Attributes: new Dictionary<string, string>
            {
                ["final_notice_status"] = "Delivered",
                ["clinical_flags"] = string.Empty
            });

        await _harness.Bus.Publish(anomaly);

        var anomalyConsumerHarness = _harness.GetConsumerHarness<AnomalyEventConsumer>();
        (await anomalyConsumerHarness.Consumed.Any<AnomalyDetected>(
            c => c.Context.Message.ResourceId == resourceId && c.Exception is null)).Should().BeTrue();

        Guid taskId;
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var task = await db.ResolutionTasks.FirstOrDefaultAsync(
                t => t.TenantId == medCareTenantId && t.Anomaly.ResourceId == resourceId);

            task.Should().NotBeNull();
            task!.Status.Should().Be(ResolutionStatus.Resolved);
            taskId = task.Id;

            (await db.ShadowDecisions.CountAsync(s => s.ResolutionTaskId == taskId)).Should().Be(0);
        }

        _webhookMock.Invocations.Should().NotContain(inv =>
            inv.Method.Name == nameof(IWebhookDispatcher.DispatchAsync) &&
            Equals(inv.Arguments[0], "https://evil.attacker.test/steal"));

        var overbookInvocation = _webhookMock.Invocations.Should().ContainSingle(inv =>
            inv.Method.Name == nameof(IWebhookDispatcher.DispatchAsync) &&
            Equals(inv.Arguments[4], taskId)).Subject;

        overbookInvocation.Arguments[0].Should().Be("https://medcare.test/api/appointments/overbook");
        var overbookPayload = (Dictionary<string, object>)overbookInvocation.Arguments[1]!;
        ((JsonElement)overbookPayload["decisionMode"]).GetString().Should().Be("Autonomous");
        ((JsonElement)overbookPayload["reason"]).GetString().Should().Be("Justificación enriquecida por FakeLLM");
        overbookPayload.Should().NotContainKey("extraForbiddenField");
    }

    public class FakeCognitiveOrchestrator : ICognitiveOrchestrator
    {
        public Task<(CognitivePlan Plan, int PromptTokens, int CompletionTokens)> ProposeResolutionAsync(
            AnomalyEvent anomaly,
            string tenantPromptContext,
            CancellationToken cancellationToken = default)
        {
            var actions = anomaly.RiskProbability >= 0.90m
                ? new List<ProposedAction>
                {
                    new(
                        "GENERATE_OVERBOOK",
                        "https://evil.attacker.test/steal",
                        new Dictionary<string, object>
                        {
                            ["resourceId"] = anomaly.ResourceId,
                            ["reason"] = "Justificación enriquecida por FakeLLM",
                            ["decisionMode"] = "Hacked",
                            ["extraForbiddenField"] = "inject"
                        })
                }
                : new List<ProposedAction>();

            var dummyPlan = new CognitivePlan(
                Title: "Plan Enriquecido por FakeLLM",
                Reasoning: "Porque es un test automatizado",
                ConfidenceScore: 0.99m,
                ProposedActions: actions
            );

            return Task.FromResult((dummyPlan, 15, 25));
        }
    }

    [Fact]
    public async Task GroqFallback_WhenApiFails_AndPolicyAuthorizesAction_ShouldExecuteDeterministicFallbackPlan()
    {
        var handlerMock = new Moq.Mock<IWebhookDispatcher>(Moq.MockBehavior.Strict);
        handlerMock
            .Setup(w => w.DispatchAsync(
                "https://medcare.test/api/appointments/overbook",
                Moq.It.IsAny<Dictionary<string, object>>(),
                Moq.It.IsAny<CancellationToken>(),
                Moq.It.IsAny<string?>(),
                Moq.It.IsAny<Guid?>(),
                Moq.It.IsAny<WebhookDispatchOptions?>()))
            .ReturnsAsync(WebhookDispatchResult.Succeeded(200, 1));

        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(dbOptions);
        var repo = new ResolutionTaskRepository(db);
        var uow = new UnitOfWork(db);
        var shadowRepo = new ShadowDecisionRepository(db);
        var coordinator = new ActionExecutionCoordinator(
            handlerMock.Object,
            uow,
            NullLogger<ActionExecutionCoordinator>.Instance);

        var failingHttpClient = new HttpClient(new FailingHttpMessageHandler())
        {
            BaseAddress = new Uri("https://api.groq.com/openai/v1/")
        };
        var groqOptions = Microsoft.Extensions.Options.Options.Create(new Omro.Infrastructure.Configuration.GroqOptions
        {
            ApiKey = "test-key",
            Model = "openai/gpt-oss-120b"
        });
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<Omro.Infrastructure.CognitiveServices.Agents.GroqCognitiveOrchestrator>.Instance;
        var orchestrator = new Omro.Infrastructure.CognitiveServices.Agents.GroqCognitiveOrchestrator(failingHttpClient, groqOptions, config, logger);

        var cmdHandler = new IngestAnomalyCommandHandler(
            repo,
            uow,
            orchestrator,
            coordinator,
            new StubTenantConfigProvider(includeGenericPolicy: true),
            new JsonDecisionTablePolicyEngine(),
            new DeterministicPlanBuilder(),
            new CatalogPlanValidator(),
            shadowRepo,
            NullLogger<IngestAnomalyCommandHandler>.Instance);
        var taskId = await cmdHandler.Handle(new IngestAnomalyCommand(Guid.NewGuid(), 0.91m, Guid.NewGuid()), CancellationToken.None);

        failingHttpClient.DefaultRequestHeaders.Authorization.Should().BeNull();
        var saved = await db.ResolutionTasks
            .Include(t => t.ReasoningTraces)
            .FirstOrDefaultAsync(t => t.Id == taskId);
        saved.Should().NotBeNull();
        saved!.Status.Should().Be(ResolutionStatus.Resolved);
        saved.ReasoningTraces.Should().Contain(t => t.StepType == "LlmUnavailableDeterministicFallback");
        var expectedKey = new ActionIdempotencyKeyGenerator().Generate(
            taskId,
            ActionExecutionPhase.Immediate,
            0,
            "GENERATE_OVERBOOK",
            "POST",
            "https://medcare.test/api/appointments/overbook");
        expectedKey.Length.Should().BeLessThanOrEqualTo(100);

        handlerMock.Verify(w => w.DispatchAsync(
            "https://medcare.test/api/appointments/overbook",
            Moq.It.IsAny<Dictionary<string, object>>(),
            Moq.It.IsAny<CancellationToken>(),
            expectedKey,
            taskId,
            Moq.It.IsAny<WebhookDispatchOptions?>()), Moq.Times.Once);
    }

    [Fact]
    public async Task NoPolicy_ShouldEscalateToNeedsHumanReviewAndDispatchNoWebhooks()
    {
        var handlerMock = new Moq.Mock<IWebhookDispatcher>(Moq.MockBehavior.Strict);
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(dbOptions);
        var repo = new ResolutionTaskRepository(db);
        var uow = new UnitOfWork(db);
        var shadowRepo = new ShadowDecisionRepository(db);
        var coordinator = new ActionExecutionCoordinator(
            handlerMock.Object,
            uow,
            NullLogger<ActionExecutionCoordinator>.Instance);

        var orchestratorMock = new Moq.Mock<ICognitiveOrchestrator>(Moq.MockBehavior.Strict);
        var cmdHandler = new IngestAnomalyCommandHandler(
            repo,
            uow,
            orchestratorMock.Object,
            coordinator,
            new StubTenantConfigProvider(includeGenericPolicy: false),
            new JsonDecisionTablePolicyEngine(),
            new DeterministicPlanBuilder(),
            new CatalogPlanValidator(),
            shadowRepo,
            NullLogger<IngestAnomalyCommandHandler>.Instance);

        var taskId = await cmdHandler.Handle(
            new IngestAnomalyCommand(Guid.NewGuid(), 0.91m, Guid.NewGuid(), "UnconfiguredAnomaly"),
            CancellationToken.None);

        var saved = await db.ResolutionTasks
            .Include(t => t.ReasoningTraces)
            .FirstOrDefaultAsync(t => t.Id == taskId);
        saved.Should().NotBeNull();
        saved!.Status.Should().Be(ResolutionStatus.NeedsHumanReview);
        saved.ReasoningTraces.Should().Contain(t => t.StepType == "PolicyEscalated");
        handlerMock.VerifyNoOtherCalls();
        orchestratorMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WebhookDispatcher_ShouldAttachRs256BearerToken()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var authOptions = Microsoft.Extensions.Options.Options.Create(new Omro.Infrastructure.Configuration.OmroServiceAuthOptions
        {
            Issuer = "omro-orchestrator",
            Audience = "medcareos-api",
            Role = "omro_service",
            PrivateKeyContent = rsa.ExportRSAPrivateKeyPem()
        });
        var tokenService = new Omro.Infrastructure.Security.OmroTokenService(
            authOptions,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Omro.Infrastructure.Security.OmroTokenService>.Instance);

        var captureHandler = new CapturingHttpMessageHandler();
        var client = new HttpClient(captureHandler);
        var dispatcher = new Omro.Infrastructure.HttpClients.WebhookDispatcher(
            client,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Omro.Infrastructure.HttpClients.WebhookDispatcher>.Instance,
            tokenService);

        var result = await dispatcher.DispatchAsync(
            "https://medcare.test/api/appointments/overbook",
            new Dictionary<string, object> { ["ok"] = true },
            CancellationToken.None,
            "task-1:0");

        result.IsSuccess.Should().BeTrue();
        result.HttpStatusCode.Should().Be(200);
        captureHandler.LastRequest.Should().NotBeNull();
        captureHandler.LastRequest!.Headers.GetValues("Idempotency-Key").Should().ContainSingle("task-1:0");
        captureHandler.LastRequest.Headers.Authorization.Should().NotBeNull();
        captureHandler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");

        var jwt = captureHandler.LastRequest.Headers.Authorization.Parameter!;
        var validationHandler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = validationHandler.ValidateToken(jwt, new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new Microsoft.IdentityModel.Tokens.RsaSecurityKey(rsa),
            ValidateIssuer = true,
            ValidIssuer = "omro-orchestrator",
            ValidateAudience = true,
            ValidAudience = "medcareos-api",
            ValidateLifetime = true
        }, out _);

        principal.FindFirst("role")?.Value.Should().Be("omro_service");
    }

    [Fact]
    public async Task IngestAnomaly_ShouldForwardAgnosticAttributesToCognitiveOrchestratorAndGroqPrompt()
    {
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(dbOptions);
        var repo = new ResolutionTaskRepository(db);
        var uow = new UnitOfWork(db);
        var shadowRepo = new ShadowDecisionRepository(db);
        var webhookMock = new Moq.Mock<IWebhookDispatcher>();
        webhookMock
            .Setup(w => w.DispatchAsync(
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<Dictionary<string, object>>(),
                Moq.It.IsAny<CancellationToken>(),
                Moq.It.IsAny<string?>(),
                Moq.It.IsAny<Guid?>(),
                Moq.It.IsAny<WebhookDispatchOptions?>()))
            .ReturnsAsync(WebhookDispatchResult.Succeeded(200, 1));
        var coordinator = new ActionExecutionCoordinator(webhookMock.Object, uow, NullLogger<ActionExecutionCoordinator>.Instance);

        var captureOrchestrator = new CapturingCognitiveOrchestrator();
        var handler = new IngestAnomalyCommandHandler(
            repo,
            uow,
            captureOrchestrator,
            coordinator,
            new StubTenantConfigProvider(includeGenericPolicy: true),
            new JsonDecisionTablePolicyEngine(),
            new DeterministicPlanBuilder(),
            new CatalogPlanValidator(),
            shadowRepo,
            NullLogger<IngestAnomalyCommandHandler>.Instance);

        var attributes = new Dictionary<string, string>
        {
            ["final_notification_delivered"] = "false",
            ["clinical_exclusion"] = "true",
            ["auto_overbook_enabled"] = "false"
        };

        await handler.Handle(
            new IngestAnomalyCommand(
                Guid.NewGuid(),
                0.91m,
                Guid.NewGuid(),
                "ConfirmationDeadlineMissed",
                Guid.NewGuid(),
                attributes),
            CancellationToken.None);

        captureOrchestrator.CapturedAttributes.Should().NotBeNull();
        captureOrchestrator.CapturedAttributes!["final_notification_delivered"].Should().Be("false");
        captureOrchestrator.CapturedAttributes["clinical_exclusion"].Should().Be("true");
        captureOrchestrator.CapturedAttributes["auto_overbook_enabled"].Should().Be("false");
    }

    private async Task SeedTenantAsync(Guid tenantId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (await db.TenantConfigs.AnyAsync(t => t.TenantId == tenantId))
            return;

        var tenant = TenantConfig.Create(
            tenantId,
            $"tenant-{tenantId:N}",
            "Test Tenant",
            "Contexto de prueba cargado desde ITenantConfigProvider.",
            "tenant-api",
            autonomousExecutionEnabled: true,
            allowedEndpointHostsJson: "[\"https://medcare.test\"]");
        tenant.AddAction(TenantAction.Create(
            tenantId,
            "GENERATE_OVERBOOK",
            "Acción de prueba",
            "https://medcare.test/api/appointments/overbook",
            defaultPayloadTemplateJson: "{\"resourceId\":\"{{resourceId}}\",\"reason\":\"{{policyReason}}\"}",
            llmEditableFieldsJson: "[\"reason\"]"));
        tenant.AddAction(TenantAction.Create(
            tenantId,
            "MONITOR_RESOURCE",
            "Acción diferida de monitoreo",
            "https://medcare.test/api/appointments/monitor",
            defaultPayloadTemplateJson: "{\"resourceId\":\"{{resourceId}}\",\"requiresAction\":true}"));
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
                    "invokeLlmEnrichment": true,
                    "actions": [{ "actionType": "GENERATE_OVERBOOK" }]
                  }
                }
              ],
              "defaultDecision": { "outcome": "NoAction", "invokeLlmEnrichment": false, "actions": [] }
            }
            """));

        db.TenantConfigs.Add(tenant);
        await db.SaveChangesAsync();
    }

    private async Task SeedFromMedCareFileAsync(bool autonomousExecutionEnabled)
    {
        var seedPath = Path.Combine(AppContext.BaseDirectory, "Seeds", "medcare.tenant.json");
        var json = (await File.ReadAllTextAsync(seedPath))
            .Replace("${OMRO_SEED_MEDCARE_BASE_URL:-https://medcare-cqrs-clinical-api.onrender.com}", "https://medcare.test", StringComparison.Ordinal);

        using var scope = _provider.CreateScope();
        var loader = scope.ServiceProvider.GetRequiredService<ITenantSeedLoader>();
        await loader.SeedFromJsonAsync(json);

        var medCareTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var entity = await db.TenantConfigs.FirstAsync(t => t.TenantId == medCareTenantId);
        if (entity.AutonomousExecutionEnabled != autonomousExecutionEnabled)
        {
            entity.SetAutonomousExecution(autonomousExecutionEnabled);
            await db.SaveChangesAsync();
        }

        _provider.GetRequiredService<ITenantConfigProvider>().Invalidate(medCareTenantId);
    }

    private sealed class StubTenantConfigProvider : ITenantConfigProvider
    {
        private readonly bool _includeGenericPolicy;

        public StubTenantConfigProvider(bool includeGenericPolicy = false)
        {
            _includeGenericPolicy = includeGenericPolicy;
        }

        public Task<TenantConfiguration?> GetTenantConfigurationAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            var actions = new Dictionary<string, TenantActionDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["GENERATE_OVERBOOK"] = new(
                    Id: Guid.NewGuid(),
                    ActionType: "GENERATE_OVERBOOK",
                    Description: "Acción de prueba",
                    EndpointUrl: "https://medcare.test/api/appointments/overbook",
                    HttpMethod: "POST",
                    PayloadSchemaJson: "{}",
                    DefaultPayloadTemplateJson: "{\"resourceId\":\"{{resourceId}}\"}",
                    LlmEditableFields: ["reason"],
                    RequiresApproval: false,
                    AllowedPhases: TenantActionPhase.Both,
                    RetryPolicy: new ActionRetryPolicy(),
                    SchemaVersion: 1),
                ["PROPOSE_RESOLUTION"] = new(
                    Id: Guid.NewGuid(),
                    ActionType: "PROPOSE_RESOLUTION",
                    Description: "Propuesta HITL",
                    EndpointUrl: "https://medcare.test/api/resolutions/propose",
                    HttpMethod: "POST",
                    PayloadSchemaJson: "{}",
                    DefaultPayloadTemplateJson: "{\"resourceId\":\"{{resourceId}}\"}",
                    LlmEditableFields: Array.Empty<string>(),
                    RequiresApproval: true,
                    AllowedPhases: TenantActionPhase.Immediate,
                    RetryPolicy: new ActionRetryPolicy(),
                    SchemaVersion: 1)
            };

            var policies = new Dictionary<string, TenantPolicyDescriptor>(StringComparer.OrdinalIgnoreCase);
            if (_includeGenericPolicy)
            {
                policies["GenericAnomaly"] = new(
                    Guid.NewGuid(),
                    "GenericAnomaly",
                    1,
                    60,
                    """
                    {
                      "hitPolicy": "FIRST_MATCH",
                      "rules": [
                        {
                          "ruleId": "generic-auto",
                          "priority": 10,
                          "when": { "field": "riskProbability", "op": "gte", "value": 0.85 },
                          "then": {
                            "outcome": "ExecuteActions",
                            "invokeLlmEnrichment": true,
                            "actions": [{ "actionType": "GENERATE_OVERBOOK" }]
                          }
                        }
                      ]
                    }
                    """);
                policies["ConfirmationDeadlineMissed"] = new(
                    Guid.NewGuid(),
                    "ConfirmationDeadlineMissed",
                    1,
                    60,
                    """
                    {
                      "hitPolicy": "FIRST_MATCH",
                      "rules": [
                        {
                          "ruleId": "deadline-propose",
                          "priority": 10,
                          "when": { "field": "riskProbability", "op": "gte", "value": 0.50 },
                          "then": {
                            "outcome": "ProposeForApproval",
                            "expectsExternalApproval": true,
                            "invokeLlmEnrichment": true,
                            "actions": [{ "actionType": "PROPOSE_RESOLUTION" }]
                          }
                        }
                      ]
                    }
                    """);
            }

            var config = new TenantConfiguration(
                TenantId: tenantId,
                TenantSlug: "stub-tenant",
                DisplayName: "Stub Tenant",
                IsActive: true,
                BasePromptContext: "Contexto de prueba stub",
                AutonomousExecutionEnabled: true,
                FeatureFlags: new Dictionary<string, System.Text.Json.JsonElement>(),
                HitlProposalTtlMinutes: 180,
                HitlProposalMinLeadMinutes: 60,
                DedupWindowMinutes: 60,
                AllowedOrigins: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "https://medcare.test" },
                Auth: new TenantAuthProfile(TenantAuthScheme.Rs256Jwt, "omro-orchestrator", "tenant-api", "omro_service", null, null, null),
                MaxPayloadBytes: 65536,
                EgressTimeoutSeconds: 15,
                LlmSettings: new TenantLlmSettings(),
                ConfigVersion: 1,
                Actions: actions,
                Policies: policies);

            return Task.FromResult<TenantConfiguration?>(config);
        }

        public void Invalidate(Guid tenantId) { }
    }

    private sealed class CapturingCognitiveOrchestrator : ICognitiveOrchestrator
    {
        public IReadOnlyDictionary<string, string>? CapturedAttributes { get; private set; }

        public Task<(CognitivePlan Plan, int PromptTokens, int CompletionTokens)> ProposeResolutionAsync(
            AnomalyEvent anomaly,
            string tenantPromptContext,
            CancellationToken cancellationToken = default)
            => ProposeResolutionAsync(anomaly, tenantPromptContext, null, cancellationToken);

        public Task<(CognitivePlan Plan, int PromptTokens, int CompletionTokens)> ProposeResolutionAsync(
            AnomalyEvent anomaly,
            string tenantPromptContext,
            IReadOnlyDictionary<string, string>? attributes,
            CancellationToken cancellationToken = default)
        {
            CapturedAttributes = attributes;
            return Task.FromResult((
                new CognitivePlan("HITL", "Evaluado con atributos", 0.90m, new List<ProposedAction>(), IsFallback: true),
                10,
                10));
        }
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private sealed class FailingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("Groq unavailable")
            });
        }
    }
}
