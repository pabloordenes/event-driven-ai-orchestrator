namespace Omro.Domain.ResolutionTasks;

public class ActionExecution
{
    public Guid Id { get; private set; }
    public Guid ResolutionTaskId { get; private set; }
    public ActionExecutionPhase Phase { get; private set; }
    public int ActionIndex { get; private set; }
    public string ActionType { get; private set; } = string.Empty;
    public string Endpoint { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = "{}";
    public string IdempotencyKey { get; private set; } = string.Empty;
    public ActionExecutionStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public int? HttpStatus { get; private set; }
    public string? Error { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? ExecutedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private ActionExecution() { } // ef core

    public static ActionExecution CreatePending(
        Guid resolutionTaskId,
        ActionExecutionPhase phase,
        int actionIndex,
        string actionType,
        string endpoint,
        string payloadJson,
        string idempotencyKey)
    {
        if (resolutionTaskId == Guid.Empty)
            throw new ArgumentException("ResolutionTaskId cannot be empty.", nameof(resolutionTaskId));
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("Endpoint cannot be empty.", nameof(endpoint));

        var now = DateTime.UtcNow;
        return new ActionExecution
        {
            Id = Guid.NewGuid(),
            ResolutionTaskId = resolutionTaskId,
            Phase = phase,
            ActionIndex = actionIndex,
            ActionType = string.IsNullOrWhiteSpace(actionType) ? "WEBHOOK" : actionType.Trim(),
            Endpoint = endpoint.Trim(),
            PayloadJson = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey)
                ? $"{resolutionTaskId}:{phase.ToString().ToLowerInvariant()}:{actionIndex}"
                : idempotencyKey,
            Status = ActionExecutionStatus.Pending,
            Attempts = 0,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public void MarkInProgress()
    {
        Status = ActionExecutionStatus.InProgress;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkSucceeded(int? httpStatus, int attempts, DateTime executedAt)
    {
        Status = ActionExecutionStatus.Succeeded;
        HttpStatus = httpStatus;
        Attempts += Math.Max(1, attempts);
        Error = null;
        ExecutedAt = executedAt;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkFailedTransient(int? httpStatus, int attempts, string? error, DateTime executedAt)
    {
        Status = ActionExecutionStatus.FailedTransient;
        HttpStatus = httpStatus;
        Attempts += Math.Max(0, attempts);
        Error = TruncateError(error);
        ExecutedAt = executedAt;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkFailedPermanent(int? httpStatus, int attempts, string? error, DateTime executedAt)
    {
        Status = ActionExecutionStatus.FailedPermanent;
        HttpStatus = httpStatus;
        Attempts += Math.Max(1, attempts);
        Error = TruncateError(error);
        ExecutedAt = executedAt;
        UpdatedAt = DateTime.UtcNow;
    }

    private static string? TruncateError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return null;

        var trimmed = error.Trim();
        return trimmed.Length <= 1000 ? trimmed : trimmed[..1000];
    }
}
