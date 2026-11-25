using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Omro.Application.Interfaces;
using Omro.Infrastructure.Configuration;

namespace Omro.Infrastructure.CognitiveServices.Providers;

public sealed class GeminiLlmProvider : ILlmProvider
{
    public const string HttpClientName = "LlmGemini";
    private const string DefaultModel = "gemini-2.5-flash";
    private const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta/";

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly HttpClient? _directHttpClient;
    private readonly GeminiOptions _options;
    private readonly string _apiKey;
    private readonly ILogger<GeminiLlmProvider> _logger;

    public GeminiLlmProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<GeminiOptions> options,
        ILogger<GeminiLlmProvider> logger,
        IConfiguration? configuration = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        _apiKey = ResolveApiKey(_options, configuration);
    }

    private GeminiLlmProvider(
        HttpClient httpClient,
        IOptions<GeminiOptions> options,
        IConfiguration? configuration,
        ILogger<GeminiLlmProvider> logger)
    {
        _directHttpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _apiKey = ResolveApiKey(_options, configuration);
    }

    public static GeminiLlmProvider CreateWithHttpClient(
        HttpClient httpClient,
        IOptions<GeminiOptions> options,
        IConfiguration? configuration,
        ILogger<GeminiLlmProvider> logger)
        => new(httpClient, options, configuration, logger);

    public string ProviderName => "Gemini";

    public string ModelName => string.IsNullOrWhiteSpace(_options.Model)
        ? DefaultModel
        : _options.Model.Trim();

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<LlmProviderResponse> ProposeResolutionAsync(
        LlmProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsConfigured)
            throw new InvalidOperationException("Falta la configuración 'Gemini:ApiKey' ni 'Google:ApiKey'.");

        var sw = Stopwatch.StartNew();
        var model = ModelName;
        var prompt = CognitivePromptBuilder.BuildUserPrompt(
            request.Anomaly,
            request.TenantPromptContext,
            request.Attributes);

        var payload = new
        {
            systemInstruction = new
            {
                parts = new[] { new { text = CognitivePromptBuilder.SystemInstruction } }
            },
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = prompt } }
                }
            },
            generationConfig = new
            {
                responseMimeType = "application/json"
            }
        };

        var client = GetHttpClient();
        var relativePath = $"models/{Uri.EscapeDataString(model)}:generateContent";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, relativePath)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json")
        };
        httpRequest.Headers.TryAddWithoutValidation("x-goog-api-key", _apiKey);

        using var response = await client.SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"[Gemini AI Error] HTTP {(int)response.StatusCode}: {errorBody}",
                inner: null,
                statusCode: response.StatusCode);
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(responseBody);

        var generatedText = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? "{}";

        int promptTokens = 0;
        int completionTokens = 0;
        if (doc.RootElement.TryGetProperty("usageMetadata", out var usageEl))
        {
            if (usageEl.TryGetProperty("promptTokenCount", out var pEl) && pEl.TryGetInt32(out var p))
                promptTokens = p;
            if (usageEl.TryGetProperty("candidatesTokenCount", out var cEl) && cEl.TryGetInt32(out var c))
                completionTokens = c;
        }

        var plan = CognitivePromptBuilder.ParseCognitivePlan(generatedText);
        sw.Stop();

        _logger.LogDebug(
            "GeminiLlmProvider respondió para recurso {ResourceId} en {LatencyMs}ms (PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}).",
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

    private static string ResolveApiKey(GeminiOptions options, IConfiguration? configuration)
    {
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
            return options.ApiKey.Trim();

        var fromGoogleSection = configuration?["Google:ApiKey"];
        return string.IsNullOrWhiteSpace(fromGoogleSection) ? string.Empty : fromGoogleSection.Trim();
    }
}
