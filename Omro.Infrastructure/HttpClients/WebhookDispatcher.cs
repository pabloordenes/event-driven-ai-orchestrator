using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Omro.Application.Interfaces;
using Polly.CircuitBreaker;
using Polly.Timeout;

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

    public async Task<WebhookDispatchResult> DispatchAsync(
        string endpoint,
        Dictionary<string, object> payload,
        CancellationToken cancellationToken,
        string? idempotencyKey = null,
        Guid? omroTaskId = null)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _logger.LogError("Endpoint de webhook inválido recibido: {Endpoint}", endpoint);
            return WebhookDispatchResult.PermanentFailure(null, 1, $"Endpoint inválido: '{endpoint}'.");
        }

        _logger.LogInformation(
            "OMRO disparando Webhook dinámico hacia {Endpoint} (Host={Host}, IdempotencyKey={IdempotencyKey}, TaskId={TaskId})",
            endpoint,
            uri.Authority,
            idempotencyKey,
            omroTaskId);

        var counter = new WebhookAttemptCounter();
        var serializedPayload = JsonSerializer.Serialize(payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(serializedPayload, Encoding.UTF8, "application/json")
        };
        request.Options.Set(WebhookAttemptCounterHandler.CounterOptionKey, counter);

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        if (omroTaskId.HasValue && omroTaskId.Value != Guid.Empty)
        {
            request.Headers.TryAddWithoutValidation("X-Omro-Task-Id", omroTaskId.Value.ToString());
        }

        if (_tokenService != null)
        {
            var token = _tokenService.GenerateToken();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var attempts = Math.Max(1, counter.Count);
            var statusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Webhook entregado hacia {Endpoint} con HTTP {StatusCode} en {Attempts} intento(s).",
                    endpoint,
                    statusCode,
                    attempts);
                return WebhookDispatchResult.Succeeded(statusCode, attempts);
            }

            var errorBody = await ReadBoundedBodyAsync(response, cancellationToken);
            var message = string.IsNullOrWhiteSpace(errorBody)
                ? $"HTTP {statusCode} desde {endpoint}"
                : $"HTTP {statusCode} desde {endpoint}: {errorBody}";

            if (WebhookResilienceExtensions.IsPermanentHttpStatus(response.StatusCode))
            {
                _logger.LogError(
                    "Webhook rechazado permanentemente por {Endpoint}. HTTP {StatusCode}, Attempts={Attempts}, Error={Error}",
                    endpoint,
                    statusCode,
                    attempts,
                    message);
                return WebhookDispatchResult.PermanentFailure(statusCode, attempts, message);
            }

            _logger.LogWarning(
                "Webhook falló transitoriamente hacia {Endpoint}. HTTP {StatusCode}, Attempts={Attempts}, Error={Error}",
                endpoint,
                statusCode,
                attempts,
                message);
            return WebhookDispatchResult.TransientFailure(statusCode, attempts, message);
        }
        catch (BrokenCircuitException ex)
        {
            _logger.LogWarning(
                ex,
                "Circuit breaker abierto para host {Host} al despachar webhook hacia {Endpoint}.",
                uri.Authority,
                endpoint);
            return WebhookDispatchResult.TransientFailure(
                null,
                counter.Count,
                $"Circuit breaker abierto para {uri.Authority}: {ex.Message}");
        }
        catch (TimeoutRejectedException ex)
        {
            var attempts = Math.Max(1, counter.Count);
            _logger.LogWarning(
                ex,
                "Timeout de resiliencia al despachar webhook hacia {Endpoint} tras {Attempts} intento(s).",
                endpoint,
                attempts);
            return WebhookDispatchResult.TransientFailure(408, attempts, $"Timeout hacia {endpoint}: {ex.Message}");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            var attempts = Math.Max(1, counter.Count);
            _logger.LogWarning(
                ex,
                "Timeout HTTP al despachar webhook hacia {Endpoint} tras {Attempts} intento(s).",
                endpoint,
                attempts);
            return WebhookDispatchResult.TransientFailure(408, attempts, $"Timeout HTTP hacia {endpoint}: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            var attempts = Math.Max(1, counter.Count);
            var statusCode = ex.StatusCode.HasValue ? (int?)ex.StatusCode.Value : null;
            _logger.LogWarning(
                ex,
                "Error de red al despachar webhook hacia {Endpoint} tras {Attempts} intento(s).",
                endpoint,
                attempts);
            return WebhookDispatchResult.TransientFailure(statusCode, attempts, ex.Message);
        }
    }

    private static async Task<string> ReadBoundedBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
                return string.Empty;

            var trimmed = body.Trim();
            return trimmed.Length <= 300 ? trimmed : trimmed[..300];
        }
        catch
        {
            return string.Empty;
        }
    }
}
