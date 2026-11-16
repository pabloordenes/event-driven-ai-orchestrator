using MassTransit;
using MediatR;
using Omro.Application.ResolutionTasks.Commands.IngestAnomaly;
using Omro.Contracts.Events;

namespace Omro.Api.Consumers;

public class AnomalyEventConsumer : IConsumer<AnomalyDetected>
{
    private readonly ISender _sender;
    private readonly ILogger<AnomalyEventConsumer> _logger;

    public AnomalyEventConsumer(ISender sender, ILogger<AnomalyEventConsumer> logger)
    {
        _sender = sender;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<AnomalyDetected> context)
    {
        var message = context.Message;

        _logger.LogInformation(
            "Anomalía {AnomalyType} recibida - Tenant: {TenantId}, Recurso: {ResourceId}, Riesgo: {RiskProbability}, CorrelationId: {CorrelationId}",
            message.AnomalyType, message.TenantId, message.ResourceId, message.RiskProbability, message.CorrelationId);

        var command = new IngestAnomalyCommand(
            TenantId: message.TenantId,
            RiskProbability: message.RiskProbability,
            ResourceId: message.ResourceId,
            AnomalyType: message.AnomalyType,
            CorrelationId: message.CorrelationId,
            Attributes: message.Attributes);

        Guid taskId = await _sender.Send(command, context.CancellationToken);

        _logger.LogInformation("ResolutionTask generada exitosamente con ID: {TaskId}", taskId);
    }
}