using MediatR;

namespace Omro.Application.ResolutionTasks.Commands.IngestAnomaly;

public record IngestAnomalyCommand(
    Guid TenantId,
    decimal RiskProbability,
    Guid ResourceId,
    string AnomalyType = "AppointmentNoShowRisk",
    Guid CorrelationId = default,
    IReadOnlyDictionary<string, string>? Attributes = null
) : IRequest<Guid>;