namespace Omro.Domain.ResolutionTasks;

public enum ResolutionStatus
{
    Ingested,
    Analyzing,
    WaitingForExternalData,
    ActionProposed,
    Resolved,
    Failed
}