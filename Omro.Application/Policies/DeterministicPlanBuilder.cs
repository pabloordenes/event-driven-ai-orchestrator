using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Omro.Application.Interfaces;
using Omro.Application.Tenants;
using Omro.Domain.ResolutionTasks;

namespace Omro.Application.Policies;

public sealed class DeterministicPlanBuilder : IDeterministicPlanBuilder
{
    public DeterministicPlanBuildResult BuildBasePlan(
        TenantConfiguration tenant,
        AnomalyEvent anomaly,
        IReadOnlyDictionary<string, string>? attributes,
        PolicyDecision decision,
        Guid omroTaskId)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(anomaly);
        ArgumentNullException.ThrowIfNull(decision);

        var proposedActions = new List<ProposedAction>(decision.Actions.Count);
        var defaultTitle = $"{anomaly.AnomalyType} - {decision.MatchedRuleId}";

        foreach (var planned in decision.Actions)
        {
            if (!tenant.TryGetAction(planned.ActionType, out var actionDescriptor))
            {
                return new DeterministicPlanBuildResult(
                    false,
                    null,
                    $"Acción '{planned.ActionType}' no existe o está inactiva en el catálogo del tenant '{tenant.TenantSlug}'.");
            }

            string? deferredPlanJson = null;
            if (planned.DeferredActions.Count > 0)
            {
                var deferredList = new List<object>(planned.DeferredActions.Count);
                foreach (var deferredPlanned in planned.DeferredActions)
                {
                    if (!tenant.TryGetAction(deferredPlanned.ActionType, out var deferredDescriptor))
                    {
                        return new DeterministicPlanBuildResult(
                            false,
                            null,
                            $"Acción diferida '{deferredPlanned.ActionType}' no existe o está inactiva en el catálogo del tenant '{tenant.TenantSlug}'.");
                    }

                    var deferredPayload = RenderPayload(
                        deferredDescriptor.DefaultPayloadTemplateJson,
                        deferredPlanned.PayloadOverrides,
                        anomaly,
                        attributes,
                        decision,
                        omroTaskId,
                        deferredPlanJson: null);

                    deferredList.Add(new
                    {
                        ActionType = deferredDescriptor.ActionType,
                        Endpoint = deferredDescriptor.EndpointUrl,
                        HttpMethod = deferredDescriptor.HttpMethod,
                        Payload = deferredPayload
                    });
                }

                var deferredEnvelope = new
                {
                    SchemaVersion = "1.0",
                    Title = defaultTitle,
                    Reasoning = decision.Reason,
                    MatchedRuleId = decision.MatchedRuleId,
                    PolicyVersion = decision.PolicyVersion,
                    ProposalExpiresAt = decision.ProposalExpiresAt,
                    IsShadowModeFallback = decision.ShadowIntent is not null,
                    DeferredActions = deferredList
                };

                deferredPlanJson = JsonSerializer.Serialize(deferredEnvelope);
            }

            var renderedPayload = RenderPayload(
                actionDescriptor.DefaultPayloadTemplateJson,
                planned.PayloadOverrides,
                anomaly,
                attributes,
                decision,
                omroTaskId,
                deferredPlanJson);

            proposedActions.Add(new ProposedAction(
                ActionType: actionDescriptor.ActionType,
                Endpoint: actionDescriptor.EndpointUrl,
                Payload: renderedPayload));
        }

        var plan = new CognitivePlan(
            Title: defaultTitle,
            Reasoning: decision.Reason,
            ConfidenceScore: 1.0m,
            ProposedActions: proposedActions,
            IsFallback: false,
            ExpectsExternalApproval: decision.ExpectsExternalApproval);

        return new DeterministicPlanBuildResult(true, plan, null);
    }

    public CognitivePlan MergeLlmEnrichment(
        TenantConfiguration tenant,
        PolicyDecision decision,
        CognitivePlan basePlan,
        CognitivePlan llmPlan)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(basePlan);
        ArgumentNullException.ThrowIfNull(llmPlan);

        if (llmPlan.IsFallback)
            return basePlan;

        var enrichedTitle = !string.IsNullOrWhiteSpace(llmPlan.Title)
            ? Truncate(llmPlan.Title.Trim(), 200)
            : basePlan.Title;

        var enrichedReasoning = !string.IsNullOrWhiteSpace(llmPlan.Reasoning)
            ? Truncate(llmPlan.Reasoning.Trim(), 1000)
            : basePlan.Reasoning;

        var enrichedConfidence = llmPlan.ConfidenceScore > 0m
            ? Math.Clamp(llmPlan.ConfidenceScore, 0m, 1m)
            : basePlan.ConfidenceScore;

        var enrichedActions = new List<ProposedAction>(basePlan.ProposedActions.Count);

        foreach (var baseAction in basePlan.ProposedActions)
        {
            var mergedPayload = new Dictionary<string, object>(baseAction.Payload, StringComparer.OrdinalIgnoreCase);

            if (tenant.TryGetAction(baseAction.ActionType, out var descriptor) &&
                descriptor.LlmEditableFields.Count > 0)
            {
                var matchingLlmAction = llmPlan.ProposedActions?.FirstOrDefault(a =>
                    string.Equals(a.ActionType, baseAction.ActionType, StringComparison.OrdinalIgnoreCase));

                if (matchingLlmAction?.Payload is { Count: > 0 })
                {
                    foreach (var editableField in descriptor.LlmEditableFields)
                    {
                        if (TryGetIgnoreCase(matchingLlmAction.Payload, editableField, out var candidateVal) &&
                            candidateVal is not null)
                        {
                            var normalizedVal = NormalizeObjectValue(candidateVal);
                            if (normalizedVal is not null)
                            {
                                var targetKey = FindExistingKeyOrDefault(mergedPayload, editableField);
                                mergedPayload[targetKey] = normalizedVal;
                            }
                        }
                    }
                }
            }

            if (mergedPayload.TryGetValue("aiPlanJson", out var aiPlanObj) &&
                aiPlanObj is string aiPlanJsonStr &&
                !string.IsNullOrWhiteSpace(aiPlanJsonStr))
            {
                var updatedAiPlanJson = EnrichDeferredEnvelopeText(aiPlanJsonStr, enrichedTitle, enrichedReasoning);
                if (updatedAiPlanJson is not null)
                {
                    mergedPayload["aiPlanJson"] = updatedAiPlanJson;
                }
            }

            enrichedActions.Add(new ProposedAction(
                ActionType: baseAction.ActionType,
                Endpoint: baseAction.Endpoint,
                Payload: mergedPayload));
        }

        return new CognitivePlan(
            Title: enrichedTitle,
            Reasoning: enrichedReasoning,
            ConfidenceScore: enrichedConfidence,
            ProposedActions: enrichedActions,
            IsFallback: false,
            ExpectsExternalApproval: basePlan.ExpectsExternalApproval);
    }

    private static Dictionary<string, object> RenderPayload(
        string templateJson,
        IReadOnlyDictionary<string, JsonElement> overrides,
        AnomalyEvent anomaly,
        IReadOnlyDictionary<string, string>? attributes,
        PolicyDecision decision,
        Guid omroTaskId,
        string? deferredPlanJson)
    {
        var rootNode = JsonNode.Parse(templateJson)?.AsObject() ?? new JsonObject();
        var renderedNode = SubstitutePlaceholders(
            rootNode,
            anomaly,
            attributes,
            decision,
            omroTaskId,
            deferredPlanJson)!.AsObject();

        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in renderedNode)
        {
            if (kvp.Value is not null)
            {
                var converted = ConvertNodeToClr(kvp.Value);
                if (converted is not null)
                    result[kvp.Key] = converted;
            }
        }

        foreach (var ov in overrides)
        {
            var normalized = ConvertElementToClr(ov.Value);
            var targetKey = FindExistingKeyOrDefault(result, ov.Key);
            if (normalized is not null)
                result[targetKey] = normalized;
            else
                result.Remove(targetKey);
        }

        return result;
    }

    private static JsonNode? SubstitutePlaceholders(
        JsonNode? node,
        AnomalyEvent anomaly,
        IReadOnlyDictionary<string, string>? attributes,
        PolicyDecision decision,
        Guid omroTaskId,
        string? deferredPlanJson)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var clone = new JsonObject();
                foreach (var prop in obj)
                {
                    clone[prop.Key] = SubstitutePlaceholders(
                        prop.Value,
                        anomaly,
                        attributes,
                        decision,
                        omroTaskId,
                        deferredPlanJson);
                }
                return clone;
            }
            case JsonArray arr:
            {
                var clone = new JsonArray();
                foreach (var item in arr)
                {
                    clone.Add(SubstitutePlaceholders(
                        item,
                        anomaly,
                        attributes,
                        decision,
                        omroTaskId,
                        deferredPlanJson));
                }
                return clone;
            }
            case JsonValue val when val.TryGetValue<string>(out var rawStr):
            {
                if (string.Equals(rawStr, "{{riskProbability}}", StringComparison.Ordinal))
                    return JsonValue.Create(anomaly.RiskProbability);

                if (string.Equals(rawStr, "{{proposalExpiresAt}}", StringComparison.Ordinal))
                    return decision.ProposalExpiresAt.HasValue
                        ? JsonValue.Create(decision.ProposalExpiresAt.Value.ToString("O", CultureInfo.InvariantCulture))
                        : null;

                var replaced = ReplaceInlineTokens(
                    rawStr,
                    anomaly,
                    attributes,
                    decision,
                    omroTaskId,
                    deferredPlanJson);

                return JsonValue.Create(replaced);
            }
            default:
                return node?.DeepClone();
        }
    }

    private static string ReplaceInlineTokens(
        string text,
        AnomalyEvent anomaly,
        IReadOnlyDictionary<string, string>? attributes,
        PolicyDecision decision,
        Guid omroTaskId,
        string? deferredPlanJson)
    {
        var effectiveCorrelationId = anomaly.CorrelationId != Guid.Empty ? anomaly.CorrelationId : omroTaskId;
        var result = text
            .Replace("{{resourceId}}", anomaly.ResourceId.ToString(), StringComparison.Ordinal)
            .Replace("{{correlationId}}", effectiveCorrelationId.ToString(), StringComparison.Ordinal)
            .Replace("{{omroTaskId}}", omroTaskId.ToString(), StringComparison.Ordinal)
            .Replace("{{riskProbability}}", anomaly.RiskProbability.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{policyReason}}", decision.Reason, StringComparison.Ordinal)
            .Replace("{{matchedRuleId}}", decision.MatchedRuleId, StringComparison.Ordinal)
            .Replace("{{proposalExpiresAt}}", decision.ProposalExpiresAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.Ordinal);

        if (deferredPlanJson is not null)
        {
            result = result.Replace("{{deferredPlanJson}}", deferredPlanJson, StringComparison.Ordinal);
        }

        if (attributes is { Count: > 0 } && result.Contains("{{attr.", StringComparison.Ordinal))
        {
            foreach (var kvp in attributes)
            {
                result = result.Replace($"{{{{attr.{kvp.Key}}}}}", kvp.Value ?? string.Empty, StringComparison.Ordinal);
            }
        }

        return result;
    }

    private static string? EnrichDeferredEnvelopeText(string aiPlanJson, string title, string reasoning)
    {
        try
        {
            var node = JsonNode.Parse(aiPlanJson) as JsonObject;
            if (node is null)
                return null;

            node["Title"] = title;
            node["Reasoning"] = reasoning;
            return node.ToJsonString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static object? ConvertNodeToClr(JsonNode node)
    {
        var element = JsonSerializer.SerializeToElement(node);
        return ConvertElementToClr(element);
    }

    private static object? NormalizeObjectValue(object value) => value is JsonElement el
        ? ConvertElementToClr(el)
        : value;

    private static object? ConvertElementToClr(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.TryGetDecimal(out var d) ? d : element.GetDouble(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(p => p.Name, p => ConvertElementToClr(p.Value)!, StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray()
            .Select(ConvertElementToClr)
            .ToList(),
        _ => element.GetRawText()
    };

    private static bool TryGetIgnoreCase(
        IReadOnlyDictionary<string, object> dict,
        string key,
        out object? value)
    {
        if (dict.TryGetValue(key, out value))
            return true;

        foreach (var kvp in dict)
        {
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = kvp.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string FindExistingKeyOrDefault(IDictionary<string, object> dict, string key)
    {
        foreach (var existing in dict.Keys)
        {
            if (string.Equals(existing, key, StringComparison.OrdinalIgnoreCase))
                return existing;
        }

        return key;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
