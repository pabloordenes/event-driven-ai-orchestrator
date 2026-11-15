namespace Omro.Application.Interfaces;

public interface IWebhookDispatcher
{
    Task DispatchAsync(string endpoint, Dictionary<string, object> payload, CancellationToken cancellationToken);
}