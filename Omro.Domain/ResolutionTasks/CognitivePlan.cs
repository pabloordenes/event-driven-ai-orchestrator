namespace Omro.Domain.ResolutionTasks;

public record CognitivePlan(
    string Title,
    string Reasoning,
    decimal ConfidenceScore,
    List<ProposedAction> ProposedActions,
    bool IsFallback = false,
    bool ExpectsExternalApproval = false
);

public record ProposedAction(
    string ActionType,
    string Endpoint,
    Dictionary<string, object> Payload
);
