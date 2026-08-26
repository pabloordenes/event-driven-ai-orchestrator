using MediatR;
using Omro.Domain.ResolutionTasks;

namespace Omro.Application.ResolutionTasks.Commands.IngestAnomaly;

public class IngestAnomalyCommandHandler : IRequestHandler<IngestAnomalyCommand, Guid>
{
    public Task<Guid> Handle(IngestAnomalyCommand request, CancellationToken cancellationToken)
    {
        var anomalyEvent = new AnomalyEvent(
            RiskProbability: request.RiskProbability,
            ResourceId: request.ResourceId);
        
        ResolutionTask resolutionTask = new(anomalyEvent);

        return Task.FromResult(resolutionTask.Id);
    }
}