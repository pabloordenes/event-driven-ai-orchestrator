namespace Omro.Domain.ResolutionTasks;

public record CognitivePlan(
    string Title,
    string Reasoning,
    decimal ConfidenceScore,
    List<ProposedAction> ProposedActions
);

public record ProposedAction(
    string ActionType,
    string Endpoint,
    Dictionary<string, object> Payload
);