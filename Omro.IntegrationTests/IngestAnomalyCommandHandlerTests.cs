using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Omro.Application.Interfaces;
using Omro.Application.ResolutionTasks.Commands.IngestAnomaly;
using Omro.Contracts.Events;

// Importa tus dominios reales (Ajusta los namespaces según tu proyecto)
using Omro.Domain.ResolutionTasks;
using Omro.Domain.Repositories; // <-- Añadido
using Omro.Infrastructure.Persistence;
using Omro.Infrastructure.Persistence.Repositories; // <-- Añadido
using Omro.Worker.Consumers;

namespace Omro.IntegrationTests;

public class IngestAnomalyCommandHandlerTests : IAsyncLifetime
{
    private ServiceProvider _provider;
    private ITestHarness _harness;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        var sharedDbName = Guid.NewGuid().ToString();

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase(sharedDbName));

        services.AddScoped<IResolutionTaskRepository, ResolutionTaskRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        // Mock del Webhook para que el test no intente hacer llamadas HTTP reales
        services.AddScoped<IWebhookDispatcher>(sp => new Moq.Mock<IWebhookDispatcher>().Object);
        
        services.AddScoped<Application.Interfaces.ICognitiveOrchestrator, FakeCognitiveOrchestrator>();

        // Inyectamos el repositorio y el Unit of Work simulando el entorno de producción
        services.AddScoped<IResolutionTaskRepository, ResolutionTaskRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
    
        // 2. Inyectamos MediatR
        services.AddMediatR(cfg => 
            cfg.RegisterServicesFromAssemblyContaining<IngestAnomalyCommandHandler>());

        // 3. Inyectamos el Arnés de MassTransit
        services.AddMassTransitTestHarness(x =>
        {
            x.AddConsumer<AnomalyEventConsumer>();
        });

        // 4. Inyectamos ILogger
        services.AddLogging();

        // Construimos el motor
        _provider = services.BuildServiceProvider();
        _harness = _provider.GetRequiredService<ITestHarness>();

        // Encendemos el bus virtual
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

        // 2. ACT: Publicamos en RabbitMQ virtual
        await _harness.Bus.Publish(anomalyEvent);

        // 3. ASSERT
        var isConsumedByBus = await _harness.Consumed.Any<AnomalyDetected>();
        isConsumedByBus.Should().BeTrue("El mensaje AnomalyDetected debió ser enrutado.");

        var consumerHarness = _harness.GetConsumerHarness<AnomalyEventConsumer>();
        var isConsumedByHandler = await consumerHarness.Consumed.Any<AnomalyDetected>();
        isConsumedByHandler.Should().BeTrue("El AnomalyEventConsumer debió procesar el mensaje sin excepciones.");

        // Aserción de Persistencia
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var tasksInDb = await dbContext.ResolutionTasks.ToListAsync();
        tasksInDb.Should().NotBeEmpty("El handler de MediatR debió persistir en PostgreSQL (InMemory).");
    }
    
    public class FakeCognitiveOrchestrator : Application.Interfaces.ICognitiveOrchestrator
    {
        public Task<(CognitivePlan Plan, int PromptTokens, int CompletionTokens)> ProposeResolutionAsync(
            AnomalyEvent anomaly, 
            string tenantPromptContext, 
            CancellationToken cancellationToken = default)
        {
            // Simulamos lo que Groq habría respondido en menos de 1 milisegundo
            var dummyPlan = new CognitivePlan(
                Title: "Acción Simulada",
                Reasoning: "Porque es un test automatizado",
                ConfidenceScore: 0.99m,
                ProposedActions: new List<ProposedAction>()
            );

            return Task.FromResult((dummyPlan, 15, 25)); // Simulamos gasto de tokens
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

        var cmdHandler = new IngestAnomalyCommandHandler(repo, uow, orchestrator, handlerMock.Object);
        var taskId = await cmdHandler.Handle(new IngestAnomalyCommand(Guid.NewGuid(), 0.91m, Guid.NewGuid()), CancellationToken.None);

        failingHttpClient.DefaultRequestHeaders.Authorization.Should().BeNull();
        var saved = await db.ResolutionTasks.FirstOrDefaultAsync(t => t.Id == taskId);
        saved.Should().NotBeNull();
        saved!.Status.Should().Be(ResolutionStatus.NeedsHumanReview);
        handlerMock.VerifyNoOtherCalls();
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