using MassTransit;
using Omro.Contracts.Events;

var builder = WebApplication.CreateBuilder(args);

// MassTransit únicamente como publicador para endpoints de diagnóstico en Development
builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((context, cfg) =>
    {
        var rmqHost = builder.Configuration["RabbitMq:Host"];
        var rmqUser = builder.Configuration["RabbitMq:Username"];
        var rmqPass = builder.Configuration["RabbitMq:Password"];

        cfg.Host(rmqHost, rmqUser, h =>
        {
            h.Username(rmqUser!);
            h.Password(rmqPass!);
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
                AnomalyType: "AppointmentNoShowRisk",
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

app.MapGet("/health", () => Results.Ok("OMRO Orchestrator is running"));

app.Run();
