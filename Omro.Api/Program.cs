using System.Security.Cryptography;
using System.Text;
using MassTransit;
using Omro.Api.Services;
using Omro.Contracts.Events;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddScoped<IOmroQueueAdminService, OmroRabbitMqQueueAdminService>();

// MassTransit únicamente como publicador para endpoints de diagnóstico en Development
builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((context, cfg) =>
    {
        var rmqHost = builder.Configuration["RabbitMq:Host"] ?? "localhost";
        var rmqUser = builder.Configuration["RabbitMq:Username"] ?? "guest";
        var rmqPass = builder.Configuration["RabbitMq:Password"] ?? "guest";
        var rmqVHost = builder.Configuration["RabbitMq:VirtualHost"];
        if (string.IsNullOrWhiteSpace(rmqVHost))
        {
            rmqVHost = rmqHost is "localhost" or "127.0.0.1" or "rabbitmq" ? "/" : rmqUser;
        }

        cfg.Host(rmqHost, rmqVHost, h =>
        {
            h.Username(rmqUser);
            h.Password(rmqPass);
        });

        cfg.ConfigureEndpoints(context);
    });
});

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    app.MapPost("/api/test-gemini", async (IPublishEndpoint publisher) =>
        {
            var simulacro = new AnomalyDetected(
                TenantId: Guid.NewGuid(),
                CorrelationId: Guid.NewGuid(),
                AnomalyType: "GenericAnomaly",
                ResourceId: Guid.NewGuid(),
                RiskProbability: 0.90m,
                Attributes: new Dictionary<string, string>()
            );

            await publisher.Publish(simulacro);
            return Results.Ok(new { Mensaje = "Anomalía inyectada en RabbitMQ", Datos = simulacro });
        })
        .WithName("TestGeminiOrchestrator");
}

app.UseHttpsRedirection();

var adminGroup = app.MapGroup("/api/admin/queues")
    .AddEndpointFilter(async (ctx, next) =>
    {
        var configuredKey = ctx.HttpContext.RequestServices
            .GetRequiredService<IConfiguration>()["AdminAuth:ApiKey"];

        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!ctx.HttpContext.Request.Headers.TryGetValue("X-Admin-Api-Key", out var providedValues))
        {
            return Results.Unauthorized();
        }

        var expectedBytes = Encoding.UTF8.GetBytes(configuredKey);
        var actualBytes = Encoding.UTF8.GetBytes(providedValues.ToString().Trim());
        if (expectedBytes.Length != actualBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
        {
            return Results.Unauthorized();
        }

        return await next(ctx);
    });

adminGroup.MapGet("/errors", async (IOmroQueueAdminService adminService, CancellationToken ct) =>
{
    var summaries = await adminService.GetErrorQueueSummariesAsync(ct);
    return Results.Ok(summaries);
});

adminGroup.MapPost("/{queueName}/requeue", async (
    string queueName,
    int? maxMessages,
    IOmroQueueAdminService adminService,
    CancellationToken ct) =>
{
    var limit = maxMessages ?? 50;
    if (limit is < 1 or > 500)
        return Results.BadRequest(new { message = "maxMessages debe estar entre 1 y 500." });

    try
    {
        var result = await adminService.RequeueErrorMessagesAsync(queueName, limit, ct);
        return Results.Ok(result);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { message = ex.Message, allowedQueues = adminService.AllowedQueues });
    }
});

app.MapGet("/health", () => Results.Ok("OMRO Orchestrator is running"));

app.Run();
