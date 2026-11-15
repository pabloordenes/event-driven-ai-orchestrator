using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Omro.Application.Interfaces;
using Omro.Domain.ResolutionTasks;

namespace Omro.Infrastructure.CognitiveServices.Agents;

public class GeminiCognitiveOrchestrator : ICognitiveOrchestrator
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    /*
     * [PIVOTE TÁCTICO - MVP Nov 2026]
     * Debido a bloqueos del motor antifraude en Google Payments que impedían activar
     * el SLA empresarial (HTTP 503 / 100s Timeout), se implementó un bypass temporal
     * utilizando Groq (Llama) para garantizar 100% de disponibilidad durante la demo.
     *
     * TODO (Post-MVP): Refactorizar la inyección de dependencias para separar
     * GroqCognitiveOrchestrator de GeminiCognitiveOrchestrator y usar Polly
     * para implementar un Tiered Fallback real entre ambos proveedores.
     */
    
    public GeminiCognitiveOrchestrator(IConfiguration configuration)
    {
        _apiKey = configuration["Google:ApiKey"] 
                  ?? throw new InvalidOperationException("Falta la configuración 'Google:ApiKey' (Usada para Groq).");
        
        _httpClient = new HttpClient
        {
            // api estandar de Groq
            BaseAddress = new Uri("https://api.groq.com/openai/v1/")
        };
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
                            "ProposedActions": [
                              {
                                "ActionType": "TIPO_DE_ACCION",
                                "Endpoint": "[https://api.tenant.com/webhook](https://api.tenant.com/webhook)",
                                "Payload": { "clave": "valor" }
                              }
                            ]
                          }
                          """;
        
        try
        {
            // construir el payload con formato compatible OpenAI/Groq
            var requestPayload = new
            {
                model = "openai/gpt-oss-120b",
                messages = new[]
                {
                    new
                    {
                        role = "system",
                        content =
                            "Eres el orquestador cognitivo de MedCareOS. Responde estrictamente con un objeto JSON válido según el esquema solicitado."
                    },
                    new { role = "user", content = prompt }
                },
                response_format = new { type = "json_object" } // fuerza a que la respuesta sea un JSON puro
            };

            var jsonContent = new StringContent(
                JsonSerializer.Serialize(requestPayload),
                Encoding.UTF8,
                "application/json");

            // la autorización en Groq se envía por Header, no por QueryString
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");

            // ejecutar la llamada REST
            var response = await _httpClient.PostAsync(
                "chat/completions",
                jsonContent,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"[Groq AI Error] HTTP {(int)response.StatusCode}: {errorBody}");
            }

            // extraer el JSON generado desde la estructura de Groq
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseBody);

            var generatedText = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content").GetString() ?? "{}";

            // extraer FinOps (Métricas de tokens)
            int promptTokens = doc.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt32();
            int completionTokens = doc.RootElement.GetProperty("usage").GetProperty("completion_tokens").GetInt32();

            // sanitización de markdown (por si el LLM lo incluye a pesar del response_format)
            if (generatedText.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            {
                generatedText = generatedText.Substring(7);
                if (generatedText.EndsWith("```"))
                {
                    generatedText = generatedText.Substring(0, generatedText.Length - 3);
                }

                generatedText = generatedText.Trim();
            }

            // deserialización
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var cognitivePlan = JsonSerializer.Deserialize<CognitivePlan>(generatedText, options)
                                ?? throw new InvalidOperationException("La IA no devolvió un JSON parseable.");

            return (cognitivePlan, promptTokens, completionTokens);
        }
        catch (Exception ex)
        {
            // Fallback -> escalamiento a HITL
            Console.WriteLine($"\n[OMRO FALLBACK] API Externa no disponible. Motivo: {ex.Message}");
            Console.WriteLine(
                "[OMRO FALLBACK] Activando plan de contingencia pre-calculado para mantener el flujo operativo...\n");

            string fallbackJson = $$"""
                                    {
                                        "Title": "Escalamiento a HITL (Modo Fallback)",
                                        "Reasoning": "Orquestador IA no disponible. Dado que la anomalía presenta un riesgo crítico ({{anomaly.RiskProbability}}), se aplica el principio de precaución. Se bloquea el sobrecupo automático y se transfiere la decisión a un administrador humano.",
                                        "ConfidenceScore": 1.0,
                                        "ProposedActions": [
                                            {
                                                "ActionType": "ESCALAR_A_HITL",
                                                "Endpoint": "[https://api.medcareos.com/resolutions/manual](https://api.medcareos.com/resolutions/manual)",
                                                "Payload": { 
                                                    "resourceId": "{{anomaly.ResourceId}}", 
                                                    "status": "pending_human_review", 
                                                    "overbook_allowed": false 
                                                }
                                            }
                                        ]
                                    }
                                    """;

            var fallbackOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var fallbackPlan = JsonSerializer.Deserialize<CognitivePlan>(fallbackJson, fallbackOptions)
                               ?? throw new InvalidOperationException("Error en JSON de Fallback.");

            // Retornamos el plan estático y 0 tokens consumidos
            return (fallbackPlan, 0, 0);
        }
    }
}