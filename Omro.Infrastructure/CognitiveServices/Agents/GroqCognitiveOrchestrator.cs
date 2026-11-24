using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Omro.Application.Interfaces;
using Omro.Domain.ResolutionTasks;
using Omro.Infrastructure.Configuration;

namespace Omro.Infrastructure.CognitiveServices.Agents;

public class GroqCognitiveOrchestrator : ICognitiveOrchestrator
{
    private readonly HttpClient _httpClient;
    private readonly GroqOptions _options;
    private readonly string _apiKey;
    private readonly ILogger<GroqCognitiveOrchestrator> _logger;

    public GroqCognitiveOrchestrator(
        HttpClient httpClient,
        IOptions<GroqOptions> options,
        IConfiguration configuration,
        ILogger<GroqCognitiveOrchestrator> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        _apiKey = !string.IsNullOrWhiteSpace(_options.ApiKey)
            ? _options.ApiKey
            : configuration["Google:ApiKey"] ?? string.Empty;

        if (_httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        }
    }

    public async Task<(CognitivePlan Plan, int PromptTokens, int CompletionTokens)> ProposeResolutionAsync(
        AnomalyEvent anomaly,
        string tenantPromptContext,
        CancellationToken cancellationToken = default)
    {
        string prompt = $$"""
                          Actúas como el orquestador cognitivo del sistema OMRO.
                          Tu objetivo es proponer un plan de resolución para una anomalía operativa.

                          REGLAS DEL INQUILINO (CONTEXTO):
                          {{tenantPromptContext}}

                          DATOS DE LA ANOMALÍA:
                          - ID del Recurso afectado: {{anomaly.ResourceId}}
                          - Probabilidad de Riesgo: {{anomaly.RiskProbability}}

                          INSTRUCCIONES DE SALIDA:
                          Debes responder ÚNICA y EXCLUSIVAMENTE con un objeto JSON válido que cumpla esta estructura exacta.
                          No incluyas texto conversacional ni formato markdown (como ```json). Solo las llaves JSON.

                          {
                            "Title": "Título corto y claro",
                            "Reasoning": "Justificación basada en las reglas del inquilino",
                            "ConfidenceScore": 0.95,
                            "ExpectsExternalApproval": false,
                            "ProposedActions": [
                              {
                                "ActionType": "TIPO_DE_ACCION",
                                "Endpoint": "https://api.tenant.com/webhook",
                                "Payload": { "clave": "valor" }
                              }
                            ]
                          }
                          Nota: establece "ExpectsExternalApproval": true únicamente si las reglas del inquilino indican que tras estas acciones iniciales se requiere aprobación externa/humana antes de ejecutar acciones diferidas.
                          """;

        try
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException("Falta la configuración 'Groq:ApiKey'.");
            }

            var model = string.IsNullOrWhiteSpace(_options.Model) ? "openai/gpt-oss-120b" : _options.Model;

            var requestPayload = new
            {
                model,
                messages = new[]
                {
                    new
                    {
                        role = "system",
                        content =
                            "Eres el orquestador cognitivo de OMRO. Responde estrictamente con un objeto JSON válido según el esquema solicitado."
                    },
                    new { role = "user", content = prompt }
                },
                response_format = new { type = "json_object" }
            };

            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(requestPayload),
                    Encoding.UTF8,
                    "application/json")
            };
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var response = await _httpClient.SendAsync(requestMessage, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"[Groq AI Error] HTTP {(int)response.StatusCode}: {errorBody}");
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseBody);

            var generatedText = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content").GetString() ?? "{}";

            int promptTokens = doc.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt32();
            int completionTokens = doc.RootElement.GetProperty("usage").GetProperty("completion_tokens").GetInt32();

            generatedText = StripMarkdownCodeFence(generatedText);

            var serializerOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var cognitivePlan = JsonSerializer.Deserialize<CognitivePlan>(generatedText, serializerOptions)
                                ?? throw new InvalidOperationException("La IA no devolvió un JSON parseable.");

            return (cognitivePlan, promptTokens, completionTokens);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[OMRO FALLBACK] Groq no disponible para recurso {ResourceId}. Escalando a NeedsHumanReview sin despachar webhooks.",
                anomaly.ResourceId);

            var fallbackPlan = new CognitivePlan(
                Title: "NeedsHumanReview",
                Reasoning: $"Orquestador IA no disponible ({ex.Message}). Se bloquea ejecución autónoma y se deriva a revisión humana.",
                ConfidenceScore: 0m,
                ProposedActions: new List<ProposedAction>(),
                IsFallback: true);

            return (fallbackPlan, 0, 0);
        }
    }

    private static string StripMarkdownCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstNewLine = trimmed.IndexOf('\n');
        if (firstNewLine >= 0)
            trimmed = trimmed[(firstNewLine + 1)..];

        if (trimmed.EndsWith("```", StringComparison.Ordinal))
            trimmed = trimmed[..^3];

        return trimmed.Trim();
    }
}
