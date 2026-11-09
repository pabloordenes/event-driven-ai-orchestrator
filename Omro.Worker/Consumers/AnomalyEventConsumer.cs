using MassTransit;
using MediatR;
using Omro.Application.ResolutionTasks.Commands.IngestAnomaly;
using Omro.Domain.ResolutionTasks;

namespace Omro.Worker.Consumers;

public class AnomalyEventConsumer : IConsumer<AnomalyEvent>
{
    private readonly ISender _sender;
    private readonly ILogger<AnomalyEventConsumer> _logger;

    public AnomalyEventConsumer(ISender sender, ILogger<AnomalyEventConsumer> logger)
    {
        _sender = sender;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<AnomalyEvent> context)
    {
        AnomalyEvent message = context.Message; // estructura de anomaly event
        
        _logger.LogInformation(
            "Anomalía recibida desde MedCare AI - Cita: {ResourceId}, Riesgo: {RiskProbability}",
            message.ResourceId, message.RiskProbability
            );

        var command = new IngestAnomalyCommand(
            RiskProbability: message.RiskProbability,
            ResourceId: message.ResourceId);
        
        Guid TaskId = await _sender.Send(command, context.CancellationToken);
        
        _logger.LogInformation("ResolutionTask generada exitosamente con ID: {TaskId}", TaskId);
    }
}