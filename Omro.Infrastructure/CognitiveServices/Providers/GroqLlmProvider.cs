using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Omro.Application.Interfaces;
using Omro.Infrastructure.Configuration;

namespace Omro.Infrastructure.CognitiveServices.Providers;

public sealed class GroqLlmProvider : ILlmProvider
{
    public const string HttpClientName = "LlmGroq";
    private const string DefaultModel = "openai/gpt-oss-120b";
    private const string DefaultBaseUrl = "https://api.groq.com/openai/v1/";

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly HttpClient? _directHttpClient;
    private readonly GroqOptions _options;
    private readonly ILogger<GroqLlmProvider> _logger;

    public GroqLlmProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<GroqOptions> options,
        ILogger<GroqLlmProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    private GroqLlmProvider(
        HttpClient httpClient,
        IOptions<GroqOptions> options,
        ILogger<GroqLlmProvider> logger)
    {
        _directHttpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public static GroqLlmProvider CreateWithHttpClient(
        HttpClient httpClient,
        IOptions<GroqOptions> options,
        ILogger<GroqLlmProvider> logger)
        => new(httpClient, options, logger);

    public string ProviderName => "Groq";

    public string ModelName => string.IsNullOrWhiteSpace(_options.Model)
        ? DefaultModel
        : _options.Model.Trim();

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async Task<LlmProviderResponse> ProposeResolutionAsync(
        LlmProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsConfigured)
            throw new InvalidOperationException("Falta la configuración 'Groq:ApiKey'.");

        var sw = Stopwatch.StartNew();
        var model = ModelName;
        var prompt = CognitivePromptBuilder.BuildUserPrompt(
            request.Anomaly,
            request.TenantPromptContext,
            request.Attributes);

        var payload = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = CognitivePromptBuilder.SystemInstruction },
                new { role = "user", content = prompt }
            },
            response_format = new { type = "json_object" }
        };

        var client = GetHttpClient();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json")
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey.Trim());

        using var response = await client.SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"[Groq AI Error] HTTP {(int)response.StatusCode}: {errorBody}",
                inner: null,
                statusCode: response.StatusCode);
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(responseBody);

        var generatedText = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "{}";

        int promptTokens = 0;
        int completionTokens = 0;
        if (doc.RootElement.TryGetProperty("usage", out var usageEl))
        {
            if (usageEl.TryGetProperty("prompt_tokens", out var pEl) && pEl.TryGetInt32(out var p))
                promptTokens = p;
            if (usageEl.TryGetProperty("completion_tokens", out var cEl) && cEl.TryGetInt32(out var c))
                completionTokens = c;
        }

        var plan = CognitivePromptBuilder.ParseCognitivePlan(generatedText);
        sw.Stop();

        _logger.LogDebug(
            "GroqLlmProvider respondió para recurso {ResourceId} en {LatencyMs}ms (PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}).",
            request.Anomaly.ResourceId,
            sw.ElapsedMilliseconds,
            promptTokens,
            completionTokens);

        return new LlmProviderResponse(
            plan,
            ProviderName,
            model,
            promptTokens,
            completionTokens,
            (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds));
    }

    private HttpClient GetHttpClient()
    {
        if (_directHttpClient is not null)
        {
            if (_directHttpClient.BaseAddress is null)
            {
                var baseUrl = string.IsNullOrWhiteSpace(_options.BaseUrl) ? DefaultBaseUrl : _options.BaseUrl;
                _directHttpClient.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            }
            return _directHttpClient;
        }

        var client = _httpClientFactory!.CreateClient(HttpClientName);
        if (client.BaseAddress is null)
        {
            var baseUrl = string.IsNullOrWhiteSpace(_options.BaseUrl) ? DefaultBaseUrl : _options.BaseUrl;
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        }
        return client;
    }
}
