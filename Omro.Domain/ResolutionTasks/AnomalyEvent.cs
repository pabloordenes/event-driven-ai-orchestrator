namespace Omro.Domain.ResolutionTasks;

public record AnomalyEvent(
    decimal RiskProbability,
    Guid ResourceId,
    string AnomalyType = "GenericAnomaly",
    Guid CorrelationId = default
);