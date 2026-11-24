namespace Omro.Domain.Messaging;

public class ProcessedMessage
{
    public const string StatusProcessing = "Processing";
    public const string StatusCompleted = "Completed";

    public Guid MessageId { get; private set; }
    public string ConsumerName { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    private ProcessedMessage() { } // ef core

    public static ProcessedMessage CreateCompleted(Guid messageId, string consumerName, TimeSpan retention, DateTimeOffset utcNow)
    {
        if (messageId == Guid.Empty)
            throw new ArgumentException("MessageId cannot be empty.", nameof(messageId));
        if (string.IsNullOrWhiteSpace(consumerName))
            throw new ArgumentException("ConsumerName cannot be empty.", nameof(consumerName));

        return new ProcessedMessage
        {
            MessageId = messageId,
            ConsumerName = consumerName.Trim(),
            Status = StatusCompleted,
            CreatedAt = utcNow,
            ProcessedAt = utcNow,
            ExpiresAt = utcNow.Add(retention)
        };
    }

    public void MarkCompleted(DateTimeOffset utcNow, TimeSpan? extendRetention = null)
    {
        Status = StatusCompleted;
        ProcessedAt = utcNow;
        if (extendRetention.HasValue)
        {
            ExpiresAt = utcNow.Add(extendRetention.Value);
        }
    }
}
