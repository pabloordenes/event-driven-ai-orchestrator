using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Omro.Application.Interfaces;

namespace Omro.Infrastructure.HttpClients;

public class WebhookDispatcher : IWebhookDispatcher
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WebhookDispatcher> _logger;
    private readonly IOmroTokenService? _tokenService;

    public WebhookDispatcher(
        HttpClient httpClient,
        ILogger<WebhookDispatcher> logger,
        IOmroTokenService? tokenService = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _tokenService = tokenService;
    }

    public async Task DispatchAsync(
        string endpoint,
        Dictionary<string, object> payload,
        CancellationToken cancellationToken,
        string? idempotencyKey = null)
    {
        _logger.LogInformation("OMRO disparando Webhook dinámico hacia: {Endpoint}", endpoint);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload)
        };

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        if (_tokenService != null)
        {
            var token = _tokenService.GenerateToken();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("El Webhook falló. HTTP Status: {StatusCode}", response.StatusCode);
            throw new HttpRequestException($"El Tenant rechazó el Webhook en {endpoint}. HTTP {response.StatusCode}");
        }
    }
}
