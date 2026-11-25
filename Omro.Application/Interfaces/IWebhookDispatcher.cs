namespace Omro.Application.Interfaces;

public record WebhookDispatchResult(
    bool IsSuccess,
    int? HttpStatusCode,
    int Attempts,
    bool IsPermanentFailure,
    string? ErrorMessage,
    DateTime ExecutedAtUtc)
{
    public static WebhookDispatchResult Succeeded(int httpStatusCode, int attempts) =>
        new(true, httpStatusCode, Math.Max(1, attempts), false, null, DateTime.UtcNow);

    public static WebhookDispatchResult PermanentFailure(int? httpStatusCode, int attempts, string errorMessage) =>
        new(false, httpStatusCode, Math.Max(1, attempts), true, errorMessage, DateTime.UtcNow);

    public static WebhookDispatchResult TransientFailure(int? httpStatusCode, int attempts, string errorMessage) =>
        new(false, httpStatusCode, Math.Max(0, attempts), false, errorMessage, DateTime.UtcNow);
}

public sealed record WebhookDispatchOptions(
    Guid? TenantId = null,
    string HttpMethod = "POST",
    int? TimeoutSeconds = null,
    int? MaxPayloadBytes = null,
    string? ActionType = null);

public interface IWebhookDispatcher
{
    Task<WebhookDispatchResult> DispatchAsync(
        string endpoint,
        Dictionary<string, object> payload,
        CancellationToken cancellationToken,
        string? idempotencyKey = null,
        Guid? omroTaskId = null,
        WebhookDispatchOptions? dispatchOptions = null);
}
