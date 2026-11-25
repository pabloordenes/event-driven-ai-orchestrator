using System.Globalization;
using System.Text.Json;
using Omro.Application.Interfaces;
using Omro.Application.Validation;
using Omro.Contracts.Events;
using Omro.Contracts.Plans;

namespace Omro.Application.Contracts;

public sealed class AiPlanContractParser : IAiPlanContractParser
{
    public AiPlanParseResult Parse(ResolutionApproved message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!string.IsNullOrWhiteSpace(message.SchemaVersion) &&
            !PlanContractVersions.IsSupported(message.SchemaVersion))
        {
            return AiPlanParseResult.Failure(
                "UNSUPPORTED_SCHEMA_VERSION",
                $"Versión de contrato '{message.SchemaVersion}' no soportada.");
        }

        if (message.AiPlan is not null)
        {
            return ParseStructuredV2(message.AiPlan);
        }

        if (!string.IsNullOrWhiteSpace(message.AiPlanJson))
        {
            return ParseJsonFallback(message.AiPlanJson, message.SchemaVersion);
        }

        return AiPlanParseResult.Failure(
            "MISSING_AI_PLAN",
            "El evento ResolutionApproved no contiene ni 'AiPlan' (v2) ni 'AiPlanJson' (v1).");
    }

    private static AiPlanParseResult ParseStructuredV2(StructuredAiPlan aiPlan)
    {
        if (!string.IsNullOrWhiteSpace(aiPlan.SchemaVersion) &&
            !PlanContractVersions.IsSupported(aiPlan.SchemaVersion))
        {
            return AiPlanParseResult.Failure(
                "UNSUPPORTED_SCHEMA_VERSION",
                $"Versión de contrato '{aiPlan.SchemaVersion}' en AiPlan no soportada.");
        }

        var effectiveVersion = !string.IsNullOrWhiteSpace(aiPlan.SchemaVersion)
            ? aiPlan.SchemaVersion.Trim()
            : PlanContractVersions.V2Structured;

        var deferredContracts = aiPlan.DeferredActions ?? Array.Empty<DeferredActionContract>();
        var candidates = new List<DeferredActionCandidate>(deferredContracts.Count);

        for (int i = 0; i < deferredContracts.Count; i++)
        {
            var action = deferredContracts[i];
            if (action is null)
            {
                return AiPlanParseResult.Failure(
                    "INVALID_DEFERRED_ACTION",
                    $"La acción diferida en índice {i} es nula.");
            }

            if (action.Payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                candidates.Add(new DeferredActionCandidate(
                    NormalizeOptionalString(action.ActionType),
                    action.Endpoint ?? string.Empty,
                    NormalizeOptionalString(action.HttpMethod),
                    new Dictionary<string, object>(StringComparer.Ordinal)));
                continue;
            }

            if (action.Payload.ValueKind != JsonValueKind.Object)
            {
                return AiPlanParseResult.Failure(
                    "INVALID_ACTION_PAYLOAD",
                    $"El payload de la acción diferida en índice {i} debe ser un objeto JSON (recibido: {action.Payload.ValueKind}).");
            }

            candidates.Add(new DeferredActionCandidate(
                NormalizeOptionalString(action.ActionType),
                action.Endpoint ?? string.Empty,
                NormalizeOptionalString(action.HttpMethod),
                ConvertObjectToDictionary(action.Payload)));
        }

        return AiPlanParseResult.Success(new ParsedAiPlan(
            SchemaVersion: effectiveVersion,
            IsLegacyFormat: false,
            Title: NormalizeOptionalString(aiPlan.Title),
            Reasoning: NormalizeOptionalString(aiPlan.Reasoning),
            OmroTaskId: aiPlan.OmroTaskId,
            ProposalExpiresAt: aiPlan.ProposalExpiresAt,
            DeferredActions: candidates));
    }

    private static AiPlanParseResult ParseJsonFallback(string aiPlanJson, string messageSchemaVersion)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(aiPlanJson);
        }
        catch (JsonException ex)
        {
            return AiPlanParseResult.Failure(
                "INVALID_AI_PLAN_JSON",
                $"El campo AiPlanJson no contiene JSON válido: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return AiPlanParseResult.Failure(
                    "INVALID_AI_PLAN_JSON",
                    "La raíz de AiPlanJson debe ser un objeto JSON.");
            }

            var effectiveVersion = !string.IsNullOrWhiteSpace(messageSchemaVersion)
                ? messageSchemaVersion.Trim()
                : PlanContractVersions.V1Legacy;

            if (TryGetPropertyIgnoreCase(root, "schemaVersion", out var versionProp) &&
                versionProp.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                if (versionProp.ValueKind != JsonValueKind.String)
                {
                    return AiPlanParseResult.Failure(
                        "UNSUPPORTED_SCHEMA_VERSION",
                        "La propiedad 'schemaVersion' en AiPlanJson debe ser un string válido.");
                }

                var versionText = versionProp.GetString();
                if (!string.IsNullOrWhiteSpace(versionText))
                {
                    if (!PlanContractVersions.IsSupported(versionText))
                    {
                        return AiPlanParseResult.Failure(
                            "UNSUPPORTED_SCHEMA_VERSION",
                            $"Versión de contrato '{versionText}' en AiPlanJson no soportada.");
                    }

                    effectiveVersion = versionText.Trim();
                }
            }

            var title = TryGetOptionalString(root, "title");
            var reasoning = TryGetOptionalString(root, "reasoning");

            Guid? omroTaskId = null;
            var omroTaskIdText = TryGetOptionalString(root, "omroTaskId");
            if (!string.IsNullOrWhiteSpace(omroTaskIdText) && Guid.TryParse(omroTaskIdText, out var parsedTaskId))
            {
                omroTaskId = parsedTaskId;
            }

            DateTimeOffset? proposalExpiresAt = null;
            var expiresAtText = TryGetOptionalString(root, "proposalExpiresAt");
            if (!string.IsNullOrWhiteSpace(expiresAtText) &&
                DateTimeOffset.TryParse(
                    expiresAtText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedExpiresAt))
            {
                proposalExpiresAt = parsedExpiresAt;
            }

            var candidates = new List<DeferredActionCandidate>();
            if (TryGetPropertyIgnoreCase(root, "deferredActions", out var deferredEl) &&
                deferredEl.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                if (deferredEl.ValueKind != JsonValueKind.Array)
                {
                    return AiPlanParseResult.Failure(
                        "INVALID_DEFERRED_ACTIONS",
                        "La propiedad 'deferredActions' en AiPlanJson debe ser un arreglo JSON.");
                }

                int index = 0;
                foreach (var item in deferredEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        return AiPlanParseResult.Failure(
                            "INVALID_DEFERRED_ACTION",
                            $"El elemento deferredActions[{index}] debe ser un objeto JSON.");
                    }

                    var actionType = TryGetOptionalString(item, "actionType");
                    var endpoint = TryGetOptionalString(item, "endpoint") ?? string.Empty;
                    var httpMethod = TryGetOptionalString(item, "httpMethod");

                    Dictionary<string, object> payloadDict;
                    if (!TryGetPropertyIgnoreCase(item, "payload", out var payloadEl) ||
                        payloadEl.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    {
                        payloadDict = new Dictionary<string, object>(StringComparer.Ordinal);
                    }
                    else if (payloadEl.ValueKind == JsonValueKind.Object)
                    {
                        payloadDict = ConvertObjectToDictionary(payloadEl);
                    }
                    else
                    {
                        return AiPlanParseResult.Failure(
                            "INVALID_ACTION_PAYLOAD",
                            $"El campo payload de deferredActions[{index}] debe ser un objeto JSON (recibido: {payloadEl.ValueKind}).");
                    }

                    candidates.Add(new DeferredActionCandidate(actionType, endpoint, httpMethod, payloadDict));
                    index++;
                }
            }

            return AiPlanParseResult.Success(new ParsedAiPlan(
                SchemaVersion: effectiveVersion,
                IsLegacyFormat: true,
                Title: title,
                Reasoning: reasoning,
                OmroTaskId: omroTaskId,
                ProposalExpiresAt: proposalExpiresAt,
                DeferredActions: candidates));
        }
    }

    private static Dictionary<string, object> ConvertObjectToDictionary(JsonElement obj)
    {
        var dict = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
        {
            dict[prop.Name] = ConvertElementToClr(prop.Value)!;
        }
        return dict;
    }

    private static object? ConvertElementToClr(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.TryGetInt64(out var l)
            ? l
            : (element.TryGetDecimal(out var d) ? d : element.GetDouble()),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Object => ConvertObjectToDictionary(element),
        JsonValueKind.Array => element.EnumerateArray().Select(ConvertElementToClr).ToList(),
        _ => element.GetRawText()
    };

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
            return true;

        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? TryGetOptionalString(JsonElement obj, string name)
    {
        if (!TryGetPropertyIgnoreCase(obj, name, out var el) || el.ValueKind != JsonValueKind.String)
            return null;

        return NormalizeOptionalString(el.GetString());
    }

    private static string? NormalizeOptionalString(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
