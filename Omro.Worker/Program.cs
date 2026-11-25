using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Omro.Application.Contracts;
using Omro.Application.Interfaces;
using Omro.Application.Policies;
using Omro.Application.ResolutionTasks.Commands.IngestAnomaly;
using Omro.Application.ResolutionTasks.Services;
using Omro.Domain.Repositories;
using Omro.Infrastructure.CognitiveServices;
using Omro.Infrastructure.CognitiveServices.Agents;
using Omro.Infrastructure.CognitiveServices.Providers;
using Omro.Infrastructure.Configuration;
using Omro.Infrastructure.HttpClients;
using Omro.Infrastructure.Messaging.Configuration;
using Omro.Infrastructure.Messaging.Observers;
using Omro.Infrastructure.Persistence;
using Omro.Infrastructure.Persistence.Repositories;
using Omro.Infrastructure.Security;
using Omro.Infrastructure.Tenants;
using Omro.Infrastructure.Validation;
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
builder.Services.Configure<GeminiOptions>(
    builder.Configuration.GetSection(GeminiOptions.SectionName));
builder.Services.Configure<CognitiveOrchestratorOptions>(
    builder.Configuration.GetSection(CognitiveOrchestratorOptions.SectionName));
builder.Services.Configure<OmroServiceAuthOptions>(
    builder.Configuration.GetSection(OmroServiceAuthOptions.SectionName));
builder.Services.Configure<RabbitMqMessagingOptions>(
    builder.Configuration.GetSection(RabbitMqMessagingOptions.SectionName));
builder.Services.Configure<WebhookExecutionOptions>(
    builder.Configuration.GetSection(WebhookExecutionOptions.SectionName));
builder.Services.Configure<TenantCacheOptions>(
    builder.Configuration.GetSection(TenantCacheOptions.SectionName));
builder.Services.Configure<TenantSeedingOptions>(
    builder.Configuration.GetSection(TenantSeedingOptions.SectionName));
builder.Services.Configure<PlanValidationOptions>(
    builder.Configuration.GetSection(PlanValidationOptions.SectionName));
builder.Services.Configure<EgressSecurityOptions>(
    builder.Configuration.GetSection(EgressSecurityOptions.SectionName));

builder.Services.AddMemoryCache();

var rabbitOptions = builder.Configuration
    .GetSection(RabbitMqMessagingOptions.SectionName)
    .Get<RabbitMqMessagingOptions>() ?? new RabbitMqMessagingOptions();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IOmroTokenService, OmroTokenService>();
builder.Services.AddSingleton<ISecretResolver, ConfigurationSecretResolver>();
builder.Services.AddSingleton<ITenantAuthSigner, TenantAuthSigner>();
builder.Services.AddSingleton<IDnsResolver, SystemDnsResolver>();
builder.Services.AddSingleton<ISsrfGuard, SsrfGuard>();
builder.Services.AddSingleton<ITenantConfigProvider, CachedTenantConfigProvider>();
builder.Services.AddSingleton<IPolicyEngine, JsonDecisionTablePolicyEngine>();
builder.Services.AddSingleton<IDeterministicPlanBuilder, DeterministicPlanBuilder>();
builder.Services.AddSingleton<IPlanValidator, CatalogPlanValidator>();
builder.Services.AddSingleton<IAiPlanContractParser, AiPlanContractParser>();
builder.Services.AddSingleton<IActionIdempotencyKeyGenerator, ActionIdempotencyKeyGenerator>();
builder.Services.AddSingleton<IOperationalAlertSink, LoggingOperationalAlertSink>();
builder.Services.AddSingleton<DeadLetterAlertObserver>();

builder.Services.AddScoped<ILlmUsageLogRepository, LlmUsageLogRepository>();
builder.Services.AddScoped<IResolutionTaskRepository, ResolutionTaskRepository>();
builder.Services.AddScoped<IActionExecutionRepository, ActionExecutionRepository>();
builder.Services.AddScoped<ITenantConfigRepository, TenantConfigRepository>();
builder.Services.AddScoped<IShadowDecisionRepository, ShadowDecisionRepository>();
builder.Services.AddScoped<IActionExecutionCoordinator, ActionExecutionCoordinator>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<IMessageInboxStore, EfMessageInboxStore>();
builder.Services.AddScoped<ITenantSeedLoader, JsonTenantSeedLoader>();

builder.Services.AddSingleton<ILlmCostCalculator, LlmCostCalculator>();
builder.Services.AddSingleton<LlmTelemetryMetrics>();
builder.Services.AddSingleton<ILlmUsageRecorder, EfLlmUsageRecorder>();

builder.Services.AddHostedService<TenantSeedHostedService>();

builder.Services.AddHttpClient(GroqLlmProvider.HttpClientName, (sp, client) =>
{
    var groqOptions = sp.GetRequiredService<IOptions<GroqOptions>>().Value;
    var baseUrl = string.IsNullOrWhiteSpace(groqOptions.BaseUrl)
        ? "https://api.groq.com/openai/v1/"
        : groqOptions.BaseUrl;
    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    client.Timeout = Timeout.InfiniteTimeSpan;
});

builder.Services.AddHttpClient(GeminiLlmProvider.HttpClientName, (sp, client) =>
{
    var geminiOptions = sp.GetRequiredService<IOptions<GeminiOptions>>().Value;
    var baseUrl = string.IsNullOrWhiteSpace(geminiOptions.BaseUrl)
        ? "https://generativelanguage.googleapis.com/v1beta/"
        : geminiOptions.BaseUrl;
    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    client.Timeout = Timeout.InfiniteTimeSpan;
});

builder.Services.AddSingleton<ILlmProvider, GroqLlmProvider>();
builder.Services.AddSingleton<ILlmProvider, GeminiLlmProvider>();
builder.Services.AddSingleton<ResilientCognitiveOrchestrator>();
builder.Services.AddSingleton<ICognitiveOrchestrator>(sp => sp.GetRequiredService<ResilientCognitiveOrchestrator>());
builder.Services.AddSingleton<ITelemetryCognitiveOrchestrator>(sp => sp.GetRequiredService<ResilientCognitiveOrchestrator>());

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
