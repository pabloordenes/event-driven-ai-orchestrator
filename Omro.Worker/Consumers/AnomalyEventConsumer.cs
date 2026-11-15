using MassTransit;
using MediatR;
using Omro.Application.IntegrationEvents;
using Omro.Application.ResolutionTasks.Commands.IngestAnomaly;
using Omro.Domain.ResolutionTasks;

namespace Omro.Worker.Consumers;

public class AnomalyEventConsumer : IConsumer<AnomalyIntegrationEvent>
{
    private readonly ISender _sender;
    private readonly ILogger<AnomalyEventConsumer> _logger;

    public AnomalyEventConsumer(ISender sender, ILogger<AnomalyEventConsumer> logger)
    {
        _sender = sender;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<AnomalyIntegrationEvent> context)
    {
        var message = context.Message; // estructura de anomaly event
        
        _logger.LogInformation(
            "Anomalía recibida desde MedCare AI - Cita: {ResourceId}, Riesgo: {RiskProbability}",
            message.ResourceId, message.RiskProbability
            );

        var command = new IngestAnomalyCommand(
            TenantId: message.TenantId,
            RiskProbability: message.RiskProbability,
            ResourceId: message.ResourceId);
        
        Guid taskId = await _sender.Send(command, context.CancellationToken);
        
        _logger.LogInformation("ResolutionTask generada exitosamente con ID: {taskId}", taskId);
    }
}