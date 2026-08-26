namespace Omro.Domain.ResolutionTasks;

public readonly record struct AnomalyEvent(
    decimal RiskProbability,
    Guid ResourceId
    );  