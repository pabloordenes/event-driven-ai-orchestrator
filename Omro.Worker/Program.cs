using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Omro.Application.Interfaces;
using Omro.Application.ResolutionTasks.Commands.IngestAnomaly;
using Omro.Domain.Repositories;
using Omro.Infrastructure.CognitiveServices.Agents;
using Omro.Infrastructure.Configuration;
using Omro.Infrastructure.HttpClients;
using Omro.Infrastructure.Messaging.Configuration;
using Omro.Infrastructure.Messaging.Observers;
using Omro.Infrastructure.Persistence;
using Omro.Infrastructure.Persistence.Repositories;
using Omro.Infrastructure.Security;
using Omro.Worker.Consumers;
using Quartz;

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
builder.Services.Configure<OmroServiceAuthOptions>(
    builder.Configuration.GetSection(OmroServiceAuthOptions.SectionName));
builder.Services.Configure<RabbitMqMessagingOptions>(
    builder.Configuration.GetSection(RabbitMqMessagingOptions.SectionName));

var rabbitOptions = builder.Configuration
    .GetSection(RabbitMqMessagingOptions.SectionName)
    .Get<RabbitMqMessagingOptions>() ?? new RabbitMqMessagingOptions();

builder.Services.AddSingleton<IOmroTokenService, OmroTokenService>();
builder.Services.AddSingleton<IOperationalAlertSink, LoggingOperationalAlertSink>();
builder.Services.AddSingleton<DeadLetterAlertObserver>();

builder.Services.AddScoped<IResolutionTaskRepository, ResolutionTaskRepository>();
builder.Services.AddScoped<IActionExecutionRepository, ActionExecutionRepository>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<IMessageInboxStore, EfMessageInboxStore>();

builder.Services.AddHttpClient<ICognitiveOrchestrator, GroqCognitiveOrchestrator>((sp, client) =>
{
    var groqOptions = sp.GetRequiredService<IOptions<GroqOptions>>().Value;
    client.BaseAddress = new Uri(groqOptions.BaseUrl.TrimEnd('/') + "/");
});

builder.Services.AddResilientWebhookHttpClient();

if (string.Equals(rabbitOptions.RedeliveryMode, "QuartzInMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddQuartz();
    builder.Services.AddQuartzHostedService(options =>
    {
        options.WaitForJobsToComplete = true;
    });
}

// 4. Configuración de MassTransit (RabbitMQ / CloudAMQP) — único host de consumers
builder.Services.AddMassTransit(x =>
{
    x.SetKebabCaseEndpointNameFormatter();
    x.AddConsumer<AnomalyEventConsumer, AnomalyEventConsumerDefinition>();
    x.AddConsumer<ResolutionApprovedConsumer, ResolutionApprovedConsumerDefinition>();

    if (string.Equals(rabbitOptions.RedeliveryMode, "QuartzInMemory", StringComparison.OrdinalIgnoreCase))
    {
        x.AddPublishMessageScheduler();
        x.AddQuartzConsumers();
    }
    else if (string.Equals(rabbitOptions.RedeliveryMode, "RabbitMqDelayedExchange", StringComparison.OrdinalIgnoreCase))
    {
        x.AddDelayedMessageScheduler();
    }

    x.UsingRabbitMq((context, cfg) =>
    {
        var rmqHost = string.IsNullOrWhiteSpace(rabbitOptions.Host) ? "localhost" : rabbitOptions.Host;
        var rmqUser = string.IsNullOrWhiteSpace(rabbitOptions.Username) ? "guest" : rabbitOptions.Username;
        var rmqPass = string.IsNullOrWhiteSpace(rabbitOptions.Password) ? "guest" : rabbitOptions.Password;
        var rmqVHost = rabbitOptions.VirtualHost;
        if (string.IsNullOrWhiteSpace(rmqVHost))
        {
            rmqVHost = rmqHost is "localhost" or "127.0.0.1" or "rabbitmq" ? "/" : rmqUser;
        }

        cfg.Host(rmqHost, rmqVHost, h =>
        {
            h.Username(rmqUser);
            h.Password(rmqPass);
        });

        if (string.Equals(rabbitOptions.RedeliveryMode, "QuartzInMemory", StringComparison.OrdinalIgnoreCase))
        {
            cfg.UsePublishMessageScheduler();
        }
        else if (string.Equals(rabbitOptions.RedeliveryMode, "RabbitMqDelayedExchange", StringComparison.OrdinalIgnoreCase))
        {
            cfg.UseDelayedMessageScheduler();
        }

        cfg.ConnectReceiveObserver(context.GetRequiredService<DeadLetterAlertObserver>());
        cfg.ConfigureEndpoints(context);
    });
});

var host = builder.Build();
host.Run();
