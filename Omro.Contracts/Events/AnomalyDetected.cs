using MassTransit;

namespace Omro.Contracts.Events;

[MessageUrn("omro:anomaly-detected")]
public record AnomalyDetected(
    Guid TenantId,
    Guid CorrelationId,
    string AnomalyType,
    Guid ResourceId,
    decimal RiskProbability,
    IReadOnlyDictionary<string, string> Attributes
);
