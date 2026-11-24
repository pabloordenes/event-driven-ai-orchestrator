namespace Omro.Infrastructure.HttpClients;

public sealed class WebhookAttemptCounter
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Increment() => Interlocked.Increment(ref _count);
}

public class WebhookAttemptCounterHandler : DelegatingHandler
{
    public static readonly HttpRequestOptionsKey<WebhookAttemptCounter> CounterOptionKey = new("OmroWebhookAttemptCounter");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(CounterOptionKey, out var counter))
        {
            counter.Increment();
        }

        return base.SendAsync(request, cancellationToken);
    }
}
