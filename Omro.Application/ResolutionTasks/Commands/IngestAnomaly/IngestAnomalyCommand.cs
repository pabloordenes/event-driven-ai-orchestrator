using MediatR;

namespace Omro.Application.ResolutionTasks.Commands.IngestAnomaly;

public record IngestAnomalyCommand(
    decimal RiskProbability,
    Guid ResourceId
    ) : IRequest<Guid>;