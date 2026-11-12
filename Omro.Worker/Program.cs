using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Omro.Application.ResolutionTasks.Commands.IngestAnomaly;
using Omro.Domain.Repositories;
using Omro.Infrastructure.Persistence;
using Omro.Infrastructure.Persistence.Repositories;
using Omro.Worker;
using Omro.Worker.Consumers;

var builder = Host.CreateApplicationBuilder(args);

// 1. Configuración de MediatR (CQRS)
builder.Services.AddMediatR(cfg => 
    cfg.RegisterServicesFromAssemblyContaining<IngestAnomalyCommand>());

// 2. Configuración de Entity Framework Core (Supabase PostgreSQL)
// Debes asegurarte de tener tu "DefaultConnection" en appsettings.json o en tus user-secrets
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
                       ?? throw new InvalidOperationException("La cadena de conexión 'DefaultConnection' no existe.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

// 3. Inyección de Repositorios y Unit of Work (Clean Architecture)
builder.Services.AddScoped<IResolutionTaskRepository, ResolutionTaskRepository>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

// 4. Configuración de MassTransit (RabbitMQ / CloudAMQP)
builder.Services.AddMassTransit(x =>
{
    // Registramos el consumidor que escuchará los eventos de MedCare
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

// 5. BackgroundService base (Opcional, pero se mantiene por estructura del Worker)
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();