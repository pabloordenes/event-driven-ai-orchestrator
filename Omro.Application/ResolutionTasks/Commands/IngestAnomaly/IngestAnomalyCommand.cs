using MediatR;

namespace Omro.Application.ResolutionTasks.Commands.IngestAnomaly;

public record IngestAnomalyCommand(
    Guid TenantId,
    decimal RiskProbability,
    Guid ResourceId
    ) : IRequest<Guid>;