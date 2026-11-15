using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Omro.Application.Interfaces;

namespace Omro.Infrastructure.HttpClients;

public class WebhookDispatcher : IWebhookDispatcher
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WebhookDispatcher> _logger;

    public WebhookDispatcher(HttpClient httpClient, ILogger<WebhookDispatcher> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task DispatchAsync(string endpoint, Dictionary<string, object> payload, CancellationToken cancellationToken)
    {
        _logger.LogInformation("OMRO disparando Webhook dinámico hacia: {Endpoint}", endpoint);

        // Disparo ciego del payload generado por la IA hacia la URL indicada por la IA
        var response = await _httpClient.PostAsJsonAsync(endpoint, payload, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("El Webhook falló. HTTP Status: {StatusCode}", response.StatusCode);
            throw new HttpRequestException($"El Tenant rechazó el Webhook en {endpoint}. HTTP {response.StatusCode}");
        }
    }
}