namespace Omro.Application.IntegrationEvents;

public record AnomalyIntegrationEvent(
    Guid TenantId,
    decimal RiskProbability,
    Guid ResourceId
    );