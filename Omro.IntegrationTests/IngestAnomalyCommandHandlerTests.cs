using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Omro.Application.Interfaces;
using Omro.Application.ResolutionTasks.Commands.IngestAnomaly;
using Omro.Application.ResolutionTasks.Services;
using Omro.Contracts.Events;
using Omro.Domain.Repositories;
using Omro.Domain.ResolutionTasks;
using Omro.Infrastructure.Messaging.Configuration;
using Omro.Infrastructure.Messaging.Observers;
using Omro.Infrastructure.Persistence;
using Omro.Infrastructure.Persistence.Repositories;
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

        services.AddScoped<IResolutionTaskRepository, ResolutionTaskRepository>();
        services.AddScoped<IActionExecutionRepository, ActionExecutionRepository>();
        services.AddScoped<IActionExecutionCoordinator, ActionExecutionCoordinator>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IMessageInboxStore, EfMessageInboxStore>();

        _webhookMock = new Moq.Mock<IWebhookDispatcher>();
        _webhookMock
            .Setup(w => w.DispatchAsync(
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<Dictionary<string, object>>(),
                Moq.It.IsAny<CancellationToken>(),
                Moq.It.IsAny<string?>(),
                Moq.It.IsAny<Guid?>()))
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
        var anomalyEvent = new AnomalyDetected(
            TenantId: Guid.NewGuid(),
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
        _webhookMock.Verify(w => w.DispatchAsync(
            "https://medcare.test/api/appointments/overbook",
            Moq.It.IsAny<Dictionary<string, object>>(),
            Moq.It.IsAny<CancellationToken>(),
            $"{tasks[0].Id}:0",
            Moq.It.IsAny<Guid?>()), Moq.Times.Once);
    }

    [Fact]
    public async Task Should_Consume_ResolutionApproved_And_DispatchDeferredActionsWithIdempotencyKey()
    {
        var taskId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var approvedEvent = new ResolutionApproved(
            TenantId: Guid.NewGuid(),
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

        _webhookMock.Verify(w => w.DispatchAsync(
            "https://medcare.test/api/appointments/monitor",
            Moq.It.IsAny<Dictionary<string, object>>(),
            Moq.It.IsAny<CancellationToken>(),
            $"{taskId}:deferred:0",
            Moq.It.IsAny<Guid?>()), Moq.Times.Once);
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
                        "https://medcare.test/api/appointments/overbook",
                        new Dictionary<string, object> { ["resourceId"] = anomaly.ResourceId })
                }
                : new List<ProposedAction>();

            var dummyPlan = new CognitivePlan(
                Title: "Acción Simulada",
                Reasoning: "Porque es un test automatizado",
                ConfidenceScore: 0.99m,
                ProposedActions: actions
            );

            return Task.FromResult((dummyPlan, 15, 25));
        }
    }

    [Fact]
    public async Task GroqFallback_WhenApiFails_ShouldPersistNeedsHumanReviewAndDispatchNoWebhooks()
    {
        var handlerMock = new Moq.Mock<IWebhookDispatcher>(Moq.MockBehavior.Strict);
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(dbOptions);
        var repo = new ResolutionTaskRepository(db);
        var uow = new UnitOfWork(db);
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
            NullLogger<IngestAnomalyCommandHandler>.Instance);
        var taskId = await cmdHandler.Handle(new IngestAnomalyCommand(Guid.NewGuid(), 0.91m, Guid.NewGuid()), CancellationToken.None);

        failingHttpClient.DefaultRequestHeaders.Authorization.Should().BeNull();
        var saved = await db.ResolutionTasks.FirstOrDefaultAsync(t => t.Id == taskId);
        saved.Should().NotBeNull();
        saved!.Status.Should().Be(ResolutionStatus.NeedsHumanReview);
        handlerMock.VerifyNoOtherCalls();
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
