using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Omro.Application.IntegrationEvents;
using Omro.Application.ResolutionTasks.Commands.IngestAnomaly;

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
        var anomalyEvent = new AnomalyIntegrationEvent(
            TenantId: Guid.NewGuid(),
            RiskProbability: 0.85m,
            ResourceId: Guid.NewGuid()
        );

        // 2. ACT: Publicamos en RabbitMQ virtual
        await _harness.Bus.Publish(anomalyEvent);

        // 3. ASSERT
        var isConsumedByBus = await _harness.Consumed.Any<AnomalyIntegrationEvent>();
        isConsumedByBus.Should().BeTrue("El mensaje AnomalyEvent debió ser enrutado.");

        var consumerHarness = _harness.GetConsumerHarness<AnomalyEventConsumer>();
        var isConsumedByHandler = await consumerHarness.Consumed.Any<AnomalyIntegrationEvent>();
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
            // Simulamos lo que Gemini habría respondido en menos de 1 milisegundo
            var dummyPlan = new CognitivePlan(
                Title: "Acción Simulada",
                Reasoning: "Porque es un test automatizado",
                ConfidenceScore: 0.99m,
                ProposedActions: new List<ProposedAction>()
            );

            return Task.FromResult((dummyPlan, 15, 25)); // Simulamos gasto de tokens
        }
    }
}