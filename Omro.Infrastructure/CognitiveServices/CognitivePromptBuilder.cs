using System.Text.Json;
using Omro.Domain.ResolutionTasks;

namespace Omro.Infrastructure.CognitiveServices;

internal static class CognitivePromptBuilder
{
    public const string SystemInstruction =
        "Eres el orquestador cognitivo de OMRO. Responde estrictamente con un objeto JSON válido según el esquema solicitado.";

    private static readonly JsonSerializerOptions PlanJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string BuildUserPrompt(
        AnomalyEvent anomaly,
        string tenantPromptContext,
        IReadOnlyDictionary<string, string>? attributes)
    {
        var attributesSection = attributes is { Count: > 0 }
            ? string.Join("\n", attributes.OrderBy(kvp => kvp.Key, StringComparer.Ordinal).Select(kvp => $"- {kvp.Key}: {kvp.Value}"))
            : "- (sin atributos adicionales)";

        return $$"""
                 Actúas como el orquestador cognitivo del sistema OMRO.
                 Tu objetivo es proponer un plan de resolución para una anomalía operativa.

                 REGLAS DEL INQUILINO (CONTEXTO):
                 {{tenantPromptContext}}

                 DATOS DE LA ANOMALÍA:
                 - Tipo de Anomalía: {{anomaly.AnomalyType}}
                 - ID del Recurso afectado: {{anomaly.ResourceId}}
                 - CorrelationId: {{anomaly.CorrelationId}}
                 - Probabilidad de Riesgo: {{anomaly.RiskProbability}}

                 ATRIBUTOS DEL EVENTO:
                 {{attributesSection}}

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
    }

    public static string StripMarkdownCodeFence(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstNewLine = trimmed.IndexOf('\n');
        if (firstNewLine >= 0)
            trimmed = trimmed[(firstNewLine + 1)..];

        if (trimmed.EndsWith("```", StringComparison.Ordinal))
            trimmed = trimmed[..^3];

        return trimmed.Trim();
    }

    public static CognitivePlan ParseCognitivePlan(string rawText)
    {
        var cleaned = StripMarkdownCodeFence(rawText);
        return JsonSerializer.Deserialize<CognitivePlan>(cleaned, PlanJsonOptions)
               ?? throw new InvalidOperationException("La IA no devolvió un JSON parseable.");
    }
}
