namespace Omro.Domain.ResolutionTasks;

public enum ActionExecutionStatus
{
    Pending,
    InProgress,
    Succeeded,
    FailedTransient,
    FailedPermanent
}
