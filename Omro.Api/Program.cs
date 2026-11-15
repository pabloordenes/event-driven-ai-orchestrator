using MassTransit;
using Microsoft.EntityFrameworkCore;
using Omro.Application.ResolutionTasks.Commands.IngestAnomaly;
using Omro.Domain.Repositories;
using Omro.Infrastructure.Persistence;
using Omro.Infrastructure.Persistence.Repositories;
using Omro.Api.Consumers;
using Omro.Application.Interfaces;
using Omro.Infrastructure.CognitiveServices.Agents;
using Omro.Infrastructure.HttpClients;

var builder = WebApplication.CreateBuilder(args);

// supabase
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
                       ?? throw new InvalidOperationException("Falta DefaultConnection.");
                       
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

// CQRS y core
builder.Services.AddMediatR(cfg => 
    cfg.RegisterServicesFromAssemblyContaining<IngestAnomalyCommand>());
    
builder.Services.AddScoped<IResolutionTaskRepository, ResolutionTaskRepository>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<ICognitiveOrchestrator, GeminiCognitiveOrchestrator>();
builder.Services.AddHttpClient<IWebhookDispatcher, WebhookDispatcher>();

// masstransit & rabbitmq
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<AnomalyEventConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        var rmqHost = builder.Configuration["RabbitMq:Host"];
        var rmqUser = builder.Configuration["RabbitMq:Username"];
        var rmqPass = builder.Configuration["RabbitMq:Password"];

        cfg.Host(rmqHost, rmqUser, h => {
            h.Username(rmqUser);
            h.Password(rmqPass);
        });
        
        cfg.ConfigureEndpoints(context);
    });
});

// openApi
builder.Services.AddOpenApi();
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// simulador de medcareOS
app.MapPost("/api/test-gemini", async (IPublishEndpoint publisher) =>
    {
        var simulacro = new Omro.Application.IntegrationEvents.AnomalyIntegrationEvent(
            TenantId: Guid.NewGuid(), // Simulamos un inquilino nuevo
            RiskProbability: 0.90m,   // Riesgo alto para forzar una decisión de la IA
            ResourceId: Guid.NewGuid()
        );

        await publisher.Publish(simulacro);
        return Results.Ok(new { Mensaje = "Anomalía inyectada en RabbitMQ", Datos = simulacro });
    })
    .WithName("TestGeminiOrchestrator")
    .WithOpenApi();

app.UseHttpsRedirection();

// health check
app.MapGet("/health", () => Results.Ok("OMRO Orchestrator is running"));

app.Run();