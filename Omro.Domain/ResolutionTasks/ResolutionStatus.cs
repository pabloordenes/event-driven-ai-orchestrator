namespace Omro.Domain.ResolutionTasks;

public enum ResolutionStatus
{
    Ingested,
    Analyzing,
    WaitingForExternalData,
    ActionProposed,
    NeedsHumanReview,
    Resolved,
    Failed
}
