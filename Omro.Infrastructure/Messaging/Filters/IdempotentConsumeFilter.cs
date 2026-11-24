using MassTransit;
using Microsoft.Extensions.Logging;
using Omro.Application.Interfaces;

namespace Omro.Infrastructure.Messaging.Filters;

public class IdempotentConsumeFilter<T> : IFilter<ConsumeContext<T>>
    where T : class
{
    private static readonly TimeSpan DefaultInboxRetention = TimeSpan.FromDays(7);

    private readonly IMessageInboxStore _inboxStore;
    private readonly ILogger<IdempotentConsumeFilter<T>> _logger;

    public IdempotentConsumeFilter(
        IMessageInboxStore inboxStore,
        ILogger<IdempotentConsumeFilter<T>> logger)
    {
        _inboxStore = inboxStore;
        _logger = logger;
    }

    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        if (!context.MessageId.HasValue || context.MessageId.Value == Guid.Empty)
        {
            await next.Send(context);
            return;
        }

        var messageId = context.MessageId.Value;
        var endpointSegment = context.ReceiveContext?.InputAddress?.AbsolutePath.Trim('/');
        var consumerKey = string.IsNullOrWhiteSpace(endpointSegment)
            ? typeof(T).Name
            : $"{endpointSegment}:{typeof(T).Name}";

        if (await _inboxStore.HasBeenCompletedAsync(messageId, consumerKey, context.CancellationToken))
        {
            _logger.LogInformation(
                "Mensaje duplicado ignorado por Inbox OMRO. MessageId={MessageId}, Consumer={Consumer}",
                messageId,
                consumerKey);
            return;
        }

        await next.Send(context);

        await _inboxStore.MarkCompletedAsync(
            messageId,
            consumerKey,
            DefaultInboxRetention,
            context.CancellationToken);
    }

    public void Probe(ProbeContext context)
    {
        context.CreateFilterScope("omroIdempotentConsume");
    }
}
