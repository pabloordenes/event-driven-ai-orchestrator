using MassTransit;
using Microsoft.EntityFrameworkCore;
using Omro.Application.ResolutionTasks.Commands.IngestAnomaly;
using Omro.Domain.Repositories;
using Omro.Infrastructure.Persistence;
using Omro.Infrastructure.Persistence.Repositories;
using Omro.Api.Consumers;

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

app.UseHttpsRedirection();

// health check
app.MapGet("/health", () => Results.Ok("OMRO Orchestrator is running"));

app.Run();