using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Omro.Application.Interfaces;
using Omro.Application.ResolutionTasks.Commands.IngestAnomaly;
using Omro.Domain.Repositories;
using Omro.Infrastructure.CognitiveServices.Agents;
using Omro.Infrastructure.Configuration;
using Omro.Infrastructure.HttpClients;
using Omro.Infrastructure.Persistence;
using Omro.Infrastructure.Persistence.Repositories;
using Omro.Worker.Consumers;

var builder = Host.CreateApplicationBuilder(args);

// 1. Configuración de MediatR (CQRS)
builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssemblyContaining<IngestAnomalyCommand>());

// 2. Configuración de Entity Framework Core (Supabase PostgreSQL)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
                       ?? throw new InvalidOperationException("La cadena de conexión 'DefaultConnection' no existe.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

// 3. Inyección de Repositorios, Unit of Work y Servicios de Infraestructura
builder.Services.Configure<GroqOptions>(
    builder.Configuration.GetSection(GroqOptions.SectionName));

builder.Services.AddScoped<IResolutionTaskRepository, ResolutionTaskRepository>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddHttpClient<ICognitiveOrchestrator, GroqCognitiveOrchestrator>((sp, client) =>
{
    var groqOptions = sp.GetRequiredService<IOptions<GroqOptions>>().Value;
    client.BaseAddress = new Uri(groqOptions.BaseUrl.TrimEnd('/') + "/");
});
builder.Services.AddHttpClient<IWebhookDispatcher, WebhookDispatcher>();

// 4. Configuración de MassTransit (RabbitMQ / CloudAMQP) — único host de consumers
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<AnomalyEventConsumer>();
    x.AddConsumer<ResolutionApprovedConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        var rmqHost = builder.Configuration["RabbitMq:Host"] ?? "localhost";
        var rmqVHost = builder.Configuration["RabbitMq:VirtualHost"] ?? "/";
        var rmqUser = builder.Configuration["RabbitMq:Username"] ?? "guest";
        var rmqPass = builder.Configuration["RabbitMq:Password"] ?? "guest";

        cfg.Host(rmqHost, rmqVHost, h =>
        {
            h.Username(rmqUser);
            h.Password(rmqPass);
        });

        cfg.ConfigureEndpoints(context);
    });
});

var host = builder.Build();
host.Run();
