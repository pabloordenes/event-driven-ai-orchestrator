using MassTransit;
using Microsoft.Extensions.Logging;

namespace Omro.Infrastructure.Messaging.Observers;

public interface IOperationalAlertSink
{
    Task NotifyDeadLetterAsync(DeadLetterAlert alert, CancellationToken cancellationToken = default);
}

public record DeadLetterAlert(
    string ServiceName,
    string MessageType,
    Guid? MessageId,
    Guid? CorrelationId,
    string? QueueName,
    int RetryAttempt,
    int RedeliveryCount,
    string ExceptionType,
    string ErrorMessage,
    DateTimeOffset OccurredAtUtc);

public class LoggingOperationalAlertSink : IOperationalAlertSink
{
    public static readonly EventId DeadLetterEventId = new(9001, "DeadLetterFault");

    private readonly ILogger<LoggingOperationalAlertSink> _logger;

    public LoggingOperationalAlertSink(ILogger<LoggingOperationalAlertSink> logger)
    {
        _logger = logger;
    }

    public Task NotifyDeadLetterAsync(DeadLetterAlert alert, CancellationToken cancellationToken = default)
    {
        _logger.LogCritical(
            DeadLetterEventId,
            "ALERTA DLQ [{ServiceName}] Mensaje enviado a cola *_error. MessageType={MessageType}, MessageId={MessageId}, CorrelationId={CorrelationId}, Queue={QueueName}, RetryAttempt={RetryAttempt}, RedeliveryCount={RedeliveryCount}, ExceptionType={ExceptionType}, Error={ErrorMessage}",
            alert.ServiceName,
            alert.MessageType,
            alert.MessageId,
            alert.CorrelationId,
            alert.QueueName,
            alert.RetryAttempt,
            alert.RedeliveryCount,
            alert.ExceptionType,
            alert.ErrorMessage);

        return Task.CompletedTask;
    }
}

public class DeadLetterAlertObserver : IReceiveObserver
{
    private readonly IOperationalAlertSink _alertSink;

    public DeadLetterAlertObserver(IOperationalAlertSink alertSink)
    {
        _alertSink = alertSink;
    }

    public Task PreReceive(ReceiveContext context) => Task.CompletedTask;

    public Task PostReceive(ReceiveContext context) => Task.CompletedTask;

    public Task PostConsume<T>(ConsumeContext<T> context, TimeSpan duration, string consumerType) where T : class
        => Task.CompletedTask;

    public async Task ConsumeFault<T>(
        ConsumeContext<T> context,
        TimeSpan duration,
        string consumerType,
        Exception exception) where T : class
    {
        var alert = new DeadLetterAlert(
            ServiceName: "OMRO",
            MessageType: typeof(T).Name,
            MessageId: context.MessageId,
            CorrelationId: context.CorrelationId,
            QueueName: context.ReceiveContext?.InputAddress?.AbsolutePath.Trim('/'),
            RetryAttempt: context.GetRetryAttempt(),
            RedeliveryCount: context.GetRedeliveryCount(),
            ExceptionType: exception.GetType().Name,
            ErrorMessage: exception.Message,
            OccurredAtUtc: DateTimeOffset.UtcNow);

        await _alertSink.NotifyDeadLetterAsync(alert, context.CancellationToken);
    }

    public async Task ReceiveFault(ReceiveContext context, Exception exception)
    {
        var alert = new DeadLetterAlert(
            ServiceName: "OMRO",
            MessageType: "UnparsedReceivePayload",
            MessageId: context.GetMessageId(),
            CorrelationId: context.GetCorrelationId(),
            QueueName: context.InputAddress?.AbsolutePath.Trim('/'),
            RetryAttempt: 0,
            RedeliveryCount: 0,
            ExceptionType: exception.GetType().Name,
            ErrorMessage: exception.Message,
            OccurredAtUtc: DateTimeOffset.UtcNow);

        await _alertSink.NotifyDeadLetterAsync(alert, context.CancellationToken);
    }
}
