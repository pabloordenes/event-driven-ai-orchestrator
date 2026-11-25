using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Omro.Application.Interfaces;
using Omro.Application.Tenants;
using Omro.Infrastructure.Configuration;
using Omro.Infrastructure.Security;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Omro.Infrastructure.HttpClients;

public class WebhookDispatcher : IWebhookDispatcher
{
    private static readonly HashSet<string> AllowedHttpMethods = new(StringComparer.Ordinal)
    {
        "POST",
        "PUT",
        "PATCH"
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<WebhookDispatcher> _logger;
    private readonly IOmroTokenService? _tokenService;
    private readonly ITenantConfigProvider? _tenantConfigProvider;
    private readonly ITenantAuthSigner? _tenantAuthSigner;
    private readonly ISsrfGuard? _ssrfGuard;
    private readonly EgressSecurityOptions _egressOptions;
    private readonly TimeProvider _timeProvider;

    public WebhookDispatcher(
        HttpClient httpClient,
        ILogger<WebhookDispatcher> logger,
        IOmroTokenService? tokenService = null,
        ITenantConfigProvider? tenantConfigProvider = null,
        ITenantAuthSigner? tenantAuthSigner = null,
        ISsrfGuard? ssrfGuard = null,
        IOptions<EgressSecurityOptions>? egressOptions = null,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _tokenService = tokenService;
        _tenantConfigProvider = tenantConfigProvider;
        _tenantAuthSigner = tenantAuthSigner;
        _ssrfGuard = ssrfGuard;
        _egressOptions = egressOptions?.Value ?? new EgressSecurityOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WebhookDispatchResult> DispatchAsync(
        string endpoint,
        Dictionary<string, object> payload,
        CancellationToken cancellationToken,
        string? idempotencyKey = null,
        Guid? omroTaskId = null,
        WebhookDispatchOptions? dispatchOptions = null)
    {
        var rawMethod = string.IsNullOrWhiteSpace(dispatchOptions?.HttpMethod)
            ? "POST"
            : dispatchOptions.HttpMethod.Trim();
        var normalizedMethod = rawMethod.ToUpperInvariant();
        if (!AllowedHttpMethods.Contains(normalizedMethod))
        {
            var methodError = $"Método HTTP '{rawMethod}' no permitido en egreso de webhooks.";
            _logger.LogError(methodError);
            return WebhookDispatchResult.PermanentFailure(null, 1, methodError);
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            _logger.LogError("Endpoint de webhook inválido recibido: {Endpoint}", endpoint);
            return WebhookDispatchResult.PermanentFailure(null, 1, $"Endpoint inválido: '{endpoint}'.");
        }

        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        if (!isHttps && !(_egressOptions.AllowInsecureHttp && isHttp))
        {
            var schemeError = $"Esquema '{uri.Scheme}' bloqueado por seguridad de egreso: '{endpoint}'.";
            _logger.LogError(schemeError);
            return WebhookDispatchResult.PermanentFailure(null, 1, schemeError);
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            var uriUnsafeError = $"Endpoint con credenciales embebidas o fragment bloqueado: '{endpoint}'.";
            _logger.LogError(uriUnsafeError);
            return WebhookDispatchResult.PermanentFailure(null, 1, uriUnsafeError);
        }

        if (_ssrfGuard is not null)
        {
            var ssrfResult = await _ssrfGuard.ValidateUriAsync(uri, cancellationToken);
            if (!ssrfResult.IsAllowed)
            {
                var ssrfReason = ssrfResult.BlockReason ?? $"Endpoint bloqueado por guardia SSRF: '{endpoint}'.";
                _logger.LogError("[OMRO EGRESS SSRF BLOCKED] Endpoint={Endpoint}: {Reason}", endpoint, ssrfReason);
                return WebhookDispatchResult.PermanentFailure(null, 1, ssrfReason);
            }
        }
        else if (SsrfGuard.IsBlockedHostOrIp(uri))
        {
            var ssrfReason = $"Host o IP bloqueado por seguridad SSRF: '{endpoint}'.";
            _logger.LogError("[OMRO EGRESS SSRF BLOCKED] Endpoint={Endpoint}: {Reason}", endpoint, ssrfReason);
            return WebhookDispatchResult.PermanentFailure(null, 1, ssrfReason);
        }

        TenantConfiguration? tenantConfig = null;
        if (dispatchOptions?.TenantId is Guid tenantId &&
            tenantId != Guid.Empty &&
            _tenantConfigProvider is not null)
        {
            tenantConfig = await _tenantConfigProvider.GetTenantConfigurationAsync(tenantId, cancellationToken);
            if (tenantConfig is null || !tenantConfig.IsActive)
            {
                var reason = tenantConfig is null
                    ? $"Tenant '{tenantId}' no tiene configuración activa registrada en OMRO para despachar webhook."
                    : $"Tenant '{tenantId}' ({tenantConfig.TenantSlug}) está inactivo.";
                _logger.LogError(reason);
                return WebhookDispatchResult.PermanentFailure(null, 1, reason);
            }

            var origin = $"{uri.Scheme.ToLowerInvariant()}://{uri.Authority.ToLowerInvariant()}";
            if (tenantConfig.AllowedOrigins.Count == 0 || !tenantConfig.AllowedOrigins.Contains(origin))
            {
                var allowlistError = $"El origen '{origin}' no está en la allowlist del tenant '{tenantConfig.TenantSlug}'.";
                _logger.LogError("[OMRO EGRESS ALLOWLIST BLOCKED] {Error}", allowlistError);
                return WebhookDispatchResult.PermanentFailure(null, 1, allowlistError);
            }
        }

        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(payload ?? new Dictionary<string, object>());
        var hardMaxBytes = Math.Max(1, _egressOptions.HardMaxPayloadBytes);
        var configuredMaxBytes = dispatchOptions?.MaxPayloadBytes
            ?? tenantConfig?.MaxPayloadBytes
            ?? _egressOptions.DefaultMaxPayloadBytes;
        var effectiveMaxBytes = Math.Clamp(configuredMaxBytes, 1, hardMaxBytes);

        if (bodyBytes.Length > effectiveMaxBytes)
        {
            var sizeError = $"Payload excede el tamaño máximo permitido ({bodyBytes.Length} > {effectiveMaxBytes} bytes).";
            _logger.LogError("[OMRO EGRESS PAYLOAD TOO LARGE] Endpoint={Endpoint}: {Error}", endpoint, sizeError);
            return WebhookDispatchResult.PermanentFailure(null, 1, sizeError);
        }

        _logger.LogInformation(
            "OMRO disparando Webhook seguro ({Method}) hacia {Endpoint} (Host={Host}, Bytes={Bytes}, IdempotencyKey={IdempotencyKey}, TaskId={TaskId})",
            normalizedMethod,
            endpoint,
            uri.Authority,
            bodyBytes.Length,
            idempotencyKey,
            omroTaskId);

        var counter = new WebhookAttemptCounter();
        var bodyContent = new ByteArrayContent(bodyBytes);
        bodyContent.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = Encoding.UTF8.WebName
        };

        using var request = new HttpRequestMessage(new HttpMethod(normalizedMethod), uri)
        {
            Content = bodyContent
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

        if (tenantConfig is not null && _tenantAuthSigner is not null)
        {
            TenantAuthHeaders authHeaders;
            try
            {
                authHeaders = _tenantAuthSigner.Sign(new TenantSigningContext(
                    tenantConfig,
                    normalizedMethod,
                    uri,
                    bodyBytes,
                    _timeProvider.GetUtcNow()));
            }
            catch (TenantAuthConfigurationException ex)
            {
                _logger.LogError(
                    ex,
                    "[OMRO EGRESS AUTH ERROR] No se pudo firmar petición para tenant {TenantSlug} ({TenantId}).",
                    tenantConfig.TenantSlug,
                    tenantConfig.TenantId);
                return WebhookDispatchResult.PermanentFailure(null, 1, ex.Message);
            }

            if (!string.IsNullOrWhiteSpace(authHeaders.BearerToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authHeaders.BearerToken);
            }

            if (!string.IsNullOrWhiteSpace(authHeaders.HmacSignature) &&
                !string.IsNullOrWhiteSpace(authHeaders.HmacTimestamp))
            {
                request.Headers.TryAddWithoutValidation("X-Omro-Timestamp", authHeaders.HmacTimestamp);
                request.Headers.TryAddWithoutValidation("X-Omro-Signature", authHeaders.HmacSignature);
            }

            request.Headers.TryAddWithoutValidation("X-Omro-Tenant-Id", authHeaders.TenantId.ToString());
        }
        else if (_tokenService is not null)
        {
            var token = _tokenService.GenerateToken();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var maxTimeoutSec = Math.Max(1, _egressOptions.MaxTimeoutSeconds);
        var configuredTimeoutSec = dispatchOptions?.TimeoutSeconds
            ?? tenantConfig?.EgressTimeoutSeconds
            ?? _egressOptions.DefaultTimeoutSeconds;
        var effectiveTimeoutSec = Math.Clamp(configuredTimeoutSec, 1, maxTimeoutSec);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(effectiveTimeoutSec));

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token);
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

            if (statusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location?.ToString();
                var redirectMsg = string.IsNullOrWhiteSpace(location)
                    ? $"Redirect HTTP {statusCode} rechazado por política de seguridad desde {endpoint}"
                    : $"Redirect HTTP {statusCode} hacia '{location}' rechazado por política de seguridad desde {endpoint}";
                _logger.LogError(
                    "[OMRO EGRESS REDIRECT BLOCKED] {Message} (Attempts={Attempts})",
                    redirectMsg,
                    attempts);
                return WebhookDispatchResult.PermanentFailure(statusCode, attempts, redirectMsg);
            }

            var errorBody = await ReadBoundedBodyAsync(response, _egressOptions.MaxResponseBodyBytes, timeoutCts.Token);
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
        catch (SsrfBlockedException ex)
        {
            _logger.LogError(
                ex,
                "[OMRO EGRESS SSRF BLOCKED SOCKET] Endpoint={Endpoint}: {Reason}",
                endpoint,
                ex.Message);
            return WebhookDispatchResult.PermanentFailure(null, Math.Max(1, counter.Count), ex.Message);
        }
        catch (HttpRequestException ex) when (ex.GetBaseException() is SsrfBlockedException ssrfEx)
        {
            _logger.LogError(
                ex,
                "[OMRO EGRESS SSRF BLOCKED SOCKET] Endpoint={Endpoint}: {Reason}",
                endpoint,
                ssrfEx.Message);
            return WebhookDispatchResult.PermanentFailure(null, Math.Max(1, counter.Count), ssrfEx.Message);
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

    private static async Task<string> ReadBoundedBodyAsync(
        HttpResponseMessage response,
        int maxResponseBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var cap = Math.Clamp(maxResponseBytes, 64, 65536);
            var buffer = new byte[cap];
            int totalRead = 0;

            while (totalRead < cap)
            {
                var bytesRead = await stream.ReadAsync(
                    buffer.AsMemory(totalRead, cap - totalRead),
                    cancellationToken);

                if (bytesRead == 0)
                    break;

                totalRead += bytesRead;
            }

            if (totalRead == 0)
                return string.Empty;

            var body = Encoding.UTF8.GetString(buffer, 0, totalRead).Trim();
            return body.Length <= 300 ? body : body[..300];
        }
        catch
        {
            return string.Empty;
        }
    }
}
