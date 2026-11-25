using System.Globalization;
using System.Text.Json;
using Omro.Application.Interfaces;
using Omro.Application.Tenants;

namespace Omro.Application.Policies;

public sealed class JsonDecisionTablePolicyEngine : IPolicyEngine
{
    private const int MaxConditionDepth = 5;

    public PolicyDecision Evaluate(PolicyEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Tenant);
        ArgumentNullException.ThrowIfNull(input.Anomaly);

        var policy = input.Tenant.ResolvePolicy(input.Anomaly.AnomalyType);
        if (policy is null)
        {
            return BuildEscalationDecision(
                input,
                policyId: null,
                policyVersion: 0,
                matchedRuleId: "no-active-policy",
                reason: $"No existe política activa para '{input.Anomaly.AnomalyType}' en el tenant '{input.Tenant.TenantSlug}'.");
        }

        try
        {
            using var doc = JsonDocument.Parse(policy.DecisionTableJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return BuildEscalationDecision(
                    input,
                    policy.Id,
                    policy.PolicyVersion,
                    "invalid-policy-definition",
                    "La definición de tabla de decisión debe ser un objeto JSON.");
            }

            if (TryGetString(root, "hitPolicy", out var hitPolicy) &&
                !string.Equals(hitPolicy, "FIRST_MATCH", StringComparison.OrdinalIgnoreCase))
            {
                return BuildEscalationDecision(
                    input,
                    policy.Id,
                    policy.PolicyVersion,
                    "invalid-policy-definition",
                    $"HitPolicy '{hitPolicy}' no soportado.");
            }

            var resourceTimeAttr = TryGetString(root, "resourceTimeAttribute", out var configuredTimeAttr) &&
                                   !string.IsNullOrWhiteSpace(configuredTimeAttr)
                ? configuredTimeAttr!
                : "resource_event_time_utc";

            var resourceEventTimeUtc = ResolveResourceEventTimeUtc(input.Attributes, resourceTimeAttr);

            var rules = ExtractOrderedRules(root);
            foreach (var rule in rules)
            {
                if (!rule.TryGetProperty("when", out var whenElement) ||
                    !rule.TryGetProperty("then", out var thenElement) ||
                    thenElement.ValueKind != JsonValueKind.Object)
                {
                    return BuildEscalationDecision(
                        input,
                        policy.Id,
                        policy.PolicyVersion,
                        "invalid-policy-definition",
                        "Regla de política malformada (requiere propiedades 'when' y 'then').");
                }

                if (EvaluateCondition(whenElement, input, depth: 1))
                {
                    var ruleId = TryGetString(rule, "ruleId", out var parsedRuleId) && !string.IsNullOrWhiteSpace(parsedRuleId)
                        ? parsedRuleId!
                        : "unnamed-rule";
                    var ruleDescription = TryGetString(rule, "description", out var desc) ? desc : null;

                    return BuildDecisionFromThen(
                        input,
                        policy,
                        ruleId,
                        ruleDescription,
                        thenElement,
                        resourceEventTimeUtc);
                }
            }

            if (root.TryGetProperty("defaultDecision", out var defaultElement) &&
                defaultElement.ValueKind == JsonValueKind.Object)
            {
                return BuildDecisionFromThen(
                    input,
                    policy,
                    "default-decision",
                    null,
                    defaultElement,
                    resourceEventTimeUtc);
            }

            return BuildEscalationDecision(
                input,
                policy.Id,
                policy.PolicyVersion,
                "default-decision",
                "NO_MATCHING_POLICY_RULE");
        }
        catch (PolicyDefinitionException ex)
        {
            return BuildEscalationDecision(
                input,
                policy.Id,
                policy.PolicyVersion,
                "invalid-policy-definition",
                ex.Message);
        }
        catch (JsonException ex)
        {
            return BuildEscalationDecision(
                input,
                policy.Id,
                policy.PolicyVersion,
                "invalid-policy-definition",
                $"JSON de política inválido: {ex.Message}");
        }
    }

    private static List<JsonElement> ExtractOrderedRules(JsonElement root)
    {
        if (!root.TryGetProperty("rules", out var rulesElement))
            return new List<JsonElement>();

        if (rulesElement.ValueKind != JsonValueKind.Array)
            throw new PolicyDefinitionException("La propiedad 'rules' de la política debe ser un arreglo.");

        return rulesElement
            .EnumerateArray()
            .Select((element, index) => (
                Element: element,
                Priority: element.ValueKind == JsonValueKind.Object &&
                          element.TryGetProperty("priority", out var prioEl) &&
                          prioEl.TryGetInt32(out var prio)
                    ? prio
                    : 0,
                Index: index))
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.Index)
            .Select(item =>
            {
                if (item.Element.ValueKind != JsonValueKind.Object)
                    throw new PolicyDefinitionException("Cada regla en 'rules' debe ser un objeto JSON.");
                return item.Element;
            })
            .ToList();
    }

    private static PolicyDecision BuildDecisionFromThen(
        PolicyEvaluationInput input,
        TenantPolicyDescriptor policy,
        string ruleId,
        string? ruleDescription,
        JsonElement thenElement,
        DateTimeOffset? resourceEventTimeUtc)
    {
        var outcomeText = TryGetString(thenElement, "outcome", out var rawOutcome)
            ? rawOutcome
            : "EscalateToHumanReview";

        if (!Enum.TryParse<PolicyOutcome>(outcomeText, ignoreCase: true, out var outcome))
        {
            throw new PolicyDefinitionException($"Outcome de política '{outcomeText}' no soportado.");
        }

        var reason = TryGetString(thenElement, "escalateReason", out var escReason) && !string.IsNullOrWhiteSpace(escReason)
            ? escReason!
            : TryGetString(thenElement, "reason", out var explicitReason) && !string.IsNullOrWhiteSpace(explicitReason)
                ? explicitReason!
                : !string.IsNullOrWhiteSpace(ruleDescription)
                    ? ruleDescription!
                    : $"Regla '{ruleId}' aplicada.";

        var invokeLlm = thenElement.TryGetProperty("invokeLlmEnrichment", out var invokeEl) &&
                        invokeEl.ValueKind == JsonValueKind.True;

        var expectsApproval = thenElement.TryGetProperty("expectsExternalApproval", out var approvalEl)
            ? approvalEl.ValueKind == JsonValueKind.True
            : outcome == PolicyOutcome.ProposeForApproval;

        var actions = ParsePlannedActions(thenElement, "actions");
        PolicyShadowIntent? shadowIntent = null;

        if (thenElement.TryGetProperty("autonomyGate", out var gateElement) &&
            gateElement.ValueKind == JsonValueKind.Object &&
            !IsAutonomyGateOpen(gateElement, input))
        {
            var suppressionReason = TryGetString(gateElement, "suppressionReason", out var supReason) &&
                                    !string.IsNullOrWhiteSpace(supReason)
                ? supReason!
                : "AUTONOMOUS_EXECUTION_DISABLED";

            var fallbackOutcomeText = TryGetString(gateElement, "fallbackOutcome", out var fbOutcomeText)
                ? fbOutcomeText
                : "ProposeForApproval";

            if (!Enum.TryParse<PolicyOutcome>(fallbackOutcomeText, ignoreCase: true, out var fallbackOutcome))
            {
                throw new PolicyDefinitionException($"FallbackOutcome '{fallbackOutcomeText}' no soportado.");
            }

            var fallbackExpectsApproval = gateElement.TryGetProperty("fallbackExpectsExternalApproval", out var fbAppEl)
                ? fbAppEl.ValueKind == JsonValueKind.True
                : fallbackOutcome == PolicyOutcome.ProposeForApproval;

            var fallbackActions = ParsePlannedActions(gateElement, "fallbackActions");
            var recordShadow = !gateElement.TryGetProperty("recordShadowDecision", out var shadowEl) ||
                               shadowEl.ValueKind == JsonValueKind.True;

            if (recordShadow)
            {
                var intendedAction = actions.Count > 0 ? actions[0].ActionType : "NONE";
                var actualAction = fallbackActions.Count > 0 ? fallbackActions[0].ActionType : fallbackOutcome.ToString();
                shadowIntent = new PolicyShadowIntent(intendedAction, actualAction, suppressionReason);
            }

            outcome = fallbackOutcome;
            expectsApproval = fallbackExpectsApproval;
            actions = fallbackActions;
            reason = $"{reason} ({suppressionReason})";
        }

        DateTimeOffset? proposalExpiresAt = outcome == PolicyOutcome.ProposeForApproval
            ? input.Tenant.ComputeHitlProposalExpiresAt(input.EvaluatedAtUtc, resourceEventTimeUtc)
            : null;

        var snapshotJson = BuildEvaluationSnapshotJson(
            input,
            policy.Id,
            policy.PolicyVersion,
            ruleId,
            outcome,
            expectsApproval,
            proposalExpiresAt,
            shadowIntent);

        return new PolicyDecision(
            Outcome: outcome,
            PolicyId: policy.Id,
            PolicyVersion: policy.PolicyVersion,
            MatchedRuleId: ruleId,
            Reason: reason,
            ExpectsExternalApproval: expectsApproval,
            InvokeLlmEnrichment: invokeLlm,
            ProposalExpiresAt: proposalExpiresAt,
            Actions: actions,
            ShadowIntent: shadowIntent,
            EvaluationSnapshotJson: snapshotJson);
    }

    private static bool IsAutonomyGateOpen(JsonElement gateElement, PolicyEvaluationInput input)
    {
        var requiresTenantFlag = !gateElement.TryGetProperty("requiresTenantAutonomousExecution", out var reqTenantEl) ||
                                 reqTenantEl.ValueKind == JsonValueKind.True;

        if (requiresTenantFlag && !input.Tenant.AutonomousExecutionEnabled)
            return false;

        if (TryGetString(gateElement, "requiredEventAttributeNotFalse", out var attrName) &&
            !string.IsNullOrWhiteSpace(attrName) &&
            TryGetAttributeValue(input.Attributes, attrName!, out var attrVal) &&
            string.Equals(attrVal.Trim(), "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static IReadOnlyList<PolicyPlannedAction> ParsePlannedActions(JsonElement container, string propertyName)
    {
        if (!container.TryGetProperty(propertyName, out var arrayElement) ||
            arrayElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<PolicyPlannedAction>();
        }

        var list = new List<PolicyPlannedAction>();
        foreach (var item in arrayElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new PolicyDefinitionException($"Elemento de '{propertyName}' debe ser un objeto.");

            if (!TryGetString(item, "actionType", out var actionType) || string.IsNullOrWhiteSpace(actionType))
                throw new PolicyDefinitionException($"Cada acción en '{propertyName}' requiere 'actionType'.");

            var overrides = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (item.TryGetProperty("payloadOverrides", out var overridesEl) &&
                overridesEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in overridesEl.EnumerateObject())
                {
                    overrides[prop.Name] = prop.Value.Clone();
                }
            }

            var deferred = ParsePlannedActions(item, "deferredActions");
            list.Add(new PolicyPlannedAction(actionType!.Trim(), overrides, deferred));
        }

        return list;
    }

    private static bool EvaluateCondition(JsonElement node, PolicyEvaluationInput input, int depth)
    {
        if (depth > MaxConditionDepth)
            throw new PolicyDefinitionException($"Profundidad máxima de condiciones ({MaxConditionDepth}) excedida.");

        if (node.ValueKind != JsonValueKind.Object)
            throw new PolicyDefinitionException("Cada nodo de condición debe ser un objeto JSON.");

        if (node.TryGetProperty("all", out var allElement))
        {
            if (allElement.ValueKind != JsonValueKind.Array)
                throw new PolicyDefinitionException("La propiedad 'all' debe ser un arreglo JSON.");

            foreach (var child in allElement.EnumerateArray())
            {
                if (!EvaluateCondition(child, input, depth + 1))
                    return false;
            }
            return true;
        }

        if (node.TryGetProperty("any", out var anyElement))
        {
            if (anyElement.ValueKind != JsonValueKind.Array)
                throw new PolicyDefinitionException("La propiedad 'any' debe ser un arreglo JSON.");

            foreach (var child in anyElement.EnumerateArray())
            {
                if (EvaluateCondition(child, input, depth + 1))
                    return true;
            }
            return false;
        }

        if (node.TryGetProperty("not", out var notElement))
        {
            return !EvaluateCondition(notElement, input, depth + 1);
        }

        return EvaluatePredicate(node, input);
    }

    private static bool EvaluatePredicate(JsonElement predicate, PolicyEvaluationInput input)
    {
        if (!TryGetString(predicate, "field", out var field) || string.IsNullOrWhiteSpace(field))
            throw new PolicyDefinitionException("Predicado sin propiedad 'field'.");

        if (!TryGetString(predicate, "op", out var op) || string.IsNullOrWhiteSpace(op))
            throw new PolicyDefinitionException($"Predicado para '{field}' sin propiedad 'op'.");

        TryGetString(predicate, "fallbackField", out var fallbackField);
        var normalizedOp = op!.Trim().ToLowerInvariant();
        var (found, rawValue) = ResolveFieldWithFallback(field!, fallbackField, normalizedOp, input);

        var onMissing = ResolveOnMissing(predicate, normalizedOp);

        if (normalizedOp == "exists")
            return found && !IsNullRawValue(rawValue);

        if (normalizedOp == "not_exists")
            return !found || IsNullRawValue(rawValue);

        if (normalizedOp == "is_empty")
            return !found ? onMissing : IsEmptyRawValue(rawValue);

        if (normalizedOp == "not_empty")
            return !found ? !onMissing : !IsEmptyRawValue(rawValue);

        if (!found || IsNullRawValue(rawValue))
            return onMissing;

        return normalizedOp switch
        {
            "gte" => TryGetExpectedDecimal(predicate, "value", out var minInc) &&
                     TryToDecimal(rawValue, out var valGte) &&
                     valGte >= minInc,

            "gt" => TryGetExpectedDecimal(predicate, "value", out var minExc) &&
                    TryToDecimal(rawValue, out var valGt) &&
                    valGt > minExc,

            "lte" => TryGetExpectedDecimal(predicate, "value", out var maxInc) &&
                     TryToDecimal(rawValue, out var valLte) &&
                     valLte <= maxInc,

            "lt" => TryGetExpectedDecimal(predicate, "value", out var maxExc) &&
                    TryToDecimal(rawValue, out var valLt) &&
                    valLt < maxExc,

            "range_half_open" => TryGetExpectedDecimal(predicate, "minInclusive", out var rMin) &&
                                 TryGetExpectedDecimal(predicate, "maxExclusive", out var rMax) &&
                                 TryToDecimal(rawValue, out var valHo) &&
                                 valHo >= rMin && valHo < rMax,

            "range_closed" => TryGetExpectedDecimal(predicate, "minInclusive", out var cMin) &&
                              TryGetExpectedDecimal(predicate, "maxInclusive", out var cMax) &&
                              TryToDecimal(rawValue, out var valCl) &&
                              valCl >= cMin && valCl <= cMax,

            "eq" => EvaluateEquals(predicate, rawValue),

            "neq" => !EvaluateEquals(predicate, rawValue),

            "in" => EvaluateIn(predicate, rawValue),

            "not_in" => !EvaluateIn(predicate, rawValue),

            "contains_any" => EvaluateContainsAny(predicate, rawValue),

            _ => throw new PolicyDefinitionException($"Operador de política '{op}' no soportado.")
        };
    }

    private static (bool Found, object? Value) ResolveFieldWithFallback(
        string field,
        string? fallbackField,
        string normalizedOp,
        PolicyEvaluationInput input)
    {
        var primary = ResolveField(field, input);
        if (primary.Found && !ShouldUseFallbackForValue(primary.Value, normalizedOp))
            return primary;

        if (!string.IsNullOrWhiteSpace(fallbackField))
        {
            var fallback = ResolveField(fallbackField!, input);
            if (fallback.Found)
                return fallback;
        }

        return primary;
    }

    private static bool ShouldUseFallbackForValue(object? value, string normalizedOp)
    {
        if (IsNullRawValue(value))
            return true;

        if (normalizedOp is "exists" or "not_exists" or "is_empty" or "not_empty")
            return false;

        return value is string s && string.IsNullOrWhiteSpace(s);
    }

    private static (bool Found, object? Value) ResolveField(string field, PolicyEvaluationInput input)
    {
        var trimmed = field.Trim();

        if (string.Equals(trimmed, "riskProbability", StringComparison.OrdinalIgnoreCase))
            return (true, input.Anomaly.RiskProbability);

        if (string.Equals(trimmed, "anomalyType", StringComparison.OrdinalIgnoreCase))
            return (true, input.Anomaly.AnomalyType);

        if (string.Equals(trimmed, "tenant.autonomousExecutionEnabled", StringComparison.OrdinalIgnoreCase))
            return (true, input.Tenant.AutonomousExecutionEnabled);

        const string attrPrefix = "attributes.";
        if (trimmed.StartsWith(attrPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var key = trimmed[attrPrefix.Length..];
            if (TryGetAttributeValue(input.Attributes, key, out var attrValue))
                return (true, attrValue);

            return (false, null);
        }

        const string flagPrefix = "tenant.flags.";
        if (trimmed.StartsWith(flagPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var key = trimmed[flagPrefix.Length..];
            if (input.Tenant.FeatureFlags.TryGetValue(key, out var flagElement) &&
                flagElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                return (true, flagElement);
            }

            return (false, null);
        }

        return (false, null);
    }

    private static bool TryGetAttributeValue(
        IReadOnlyDictionary<string, string>? attributes,
        string key,
        out string value)
    {
        value = string.Empty;
        if (attributes is null || attributes.Count == 0 || string.IsNullOrWhiteSpace(key))
            return false;

        if (attributes.TryGetValue(key, out var direct) && direct is not null)
        {
            value = direct;
            return true;
        }

        foreach (var kvp in attributes)
        {
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase) && kvp.Value is not null)
            {
                value = kvp.Value;
                return true;
            }
        }

        return false;
    }

    private static bool ResolveOnMissing(JsonElement predicate, string normalizedOp)
    {
        if (predicate.TryGetProperty("onMissing", out var onMissingEl))
        {
            if (onMissingEl.ValueKind == JsonValueKind.True)
                return true;
            if (onMissingEl.ValueKind == JsonValueKind.False)
                return false;
            if (onMissingEl.ValueKind == JsonValueKind.String &&
                bool.TryParse(onMissingEl.GetString(), out var parsedBool))
                return parsedBool;
        }

        if (predicate.TryGetProperty("whenPresent", out var whenPresentEl) &&
            whenPresentEl.ValueKind == JsonValueKind.True)
        {
            return false;
        }

        return normalizedOp is "neq" or "not_in" or "is_empty" or "not_exists";
    }

    private static bool IsNullRawValue(object? rawValue)
    {
        if (rawValue is null)
            return true;

        return rawValue is JsonElement el && el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
    }

    private static bool IsEmptyRawValue(object? rawValue)
    {
        if (IsNullRawValue(rawValue))
            return true;

        if (rawValue is JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Array)
            {
                return el.EnumerateArray().All(item =>
                    item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
                    (item.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(item.GetString())));
            }

            if (el.ValueKind == JsonValueKind.String)
                return IsEmptyStringRepresentation(el.GetString());

            return false;
        }

        if (rawValue is string text)
            return IsEmptyStringRepresentation(text);

        return false;
    }

    private static bool IsEmptyStringRepresentation(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var trimmed = text.Trim();
        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return doc.RootElement.EnumerateArray().All(item =>
                        item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
                        (item.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(item.GetString())));
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return false;
    }

    private static bool EvaluateEquals(JsonElement predicate, object? rawValue)
    {
        if (!predicate.TryGetProperty("value", out var expected))
            return false;

        if (expected.ValueKind == JsonValueKind.Number &&
            expected.TryGetDecimal(out var expectedDec) &&
            TryToDecimal(rawValue, out var actualDec))
        {
            return actualDec == expectedDec;
        }

        var expectedStr = JsonScalarToString(expected);
        var actualStr = RawScalarToString(rawValue);
        if (expectedStr is null || actualStr is null)
            return expectedStr is null && actualStr is null;

        return string.Equals(actualStr.Trim(), expectedStr.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool EvaluateIn(JsonElement predicate, object? rawValue)
    {
        var listElement = GetTargetArrayElement(predicate);
        if (!listElement.HasValue)
            return false;

        var actualStr = RawScalarToString(rawValue)?.Trim();
        var hasActualDec = TryToDecimal(rawValue, out var actualDec);

        foreach (var item in listElement.Value.EnumerateArray())
        {
            if (hasActualDec && item.ValueKind == JsonValueKind.Number && item.TryGetDecimal(out var itemDec) && itemDec == actualDec)
                return true;

            var itemStr = JsonScalarToString(item)?.Trim();
            if (itemStr is not null && actualStr is not null &&
                string.Equals(actualStr, itemStr, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool EvaluateContainsAny(JsonElement predicate, object? rawValue)
    {
        var listElement = GetTargetArrayElement(predicate);
        if (!listElement.HasValue)
            return false;

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in listElement.Value.EnumerateArray())
        {
            var target = JsonScalarToString(item);
            if (!string.IsNullOrWhiteSpace(target))
                targets.Add(target.Trim());
        }

        if (targets.Count == 0)
            return false;

        var actualTokens = ExtractCollectionTokens(rawValue);
        return actualTokens.Any(targets.Contains);
    }

    private static IReadOnlyList<string> ExtractCollectionTokens(object? rawValue)
    {
        if (rawValue is JsonElement el && el.ValueKind == JsonValueKind.Array)
        {
            return el.EnumerateArray()
                .Select(JsonScalarToString)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim())
                .ToArray();
        }

        var text = RawScalarToString(rawValue);
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        var trimmed = text.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return doc.RootElement.EnumerateArray()
                        .Select(JsonScalarToString)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s!.Trim())
                        .ToArray();
                }
            }
            catch (JsonException)
            {
                // fallback a CSV
            }
        }

        return trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static JsonElement? GetTargetArrayElement(JsonElement predicate)
    {
        if (predicate.TryGetProperty("values", out var valuesEl) && valuesEl.ValueKind == JsonValueKind.Array)
            return valuesEl;

        if (predicate.TryGetProperty("value", out var valueEl) && valueEl.ValueKind == JsonValueKind.Array)
            return valueEl;

        return null;
    }

    private static bool TryGetExpectedDecimal(JsonElement predicate, string propertyName, out decimal value)
    {
        value = 0m;
        if (!predicate.TryGetProperty(propertyName, out var el))
            return false;

        if (el.ValueKind == JsonValueKind.Number)
            return el.TryGetDecimal(out value);

        if (el.ValueKind == JsonValueKind.String)
            return decimal.TryParse(el.GetString()?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out value);

        return false;
    }

    private static bool TryToDecimal(object? rawValue, out decimal value)
    {
        value = 0m;
        switch (rawValue)
        {
            case decimal d:
                value = d;
                return true;
            case int i:
                value = i;
                return true;
            case long l:
                value = l;
                return true;
            case double db:
                value = Convert.ToDecimal(db, CultureInfo.InvariantCulture);
                return true;
            case string s:
                return decimal.TryParse(s.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
            case JsonElement el when el.ValueKind == JsonValueKind.Number:
                return el.TryGetDecimal(out value);
            case JsonElement el when el.ValueKind == JsonValueKind.String:
                return decimal.TryParse(el.GetString()?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
            default:
                return false;
        }
    }

    private static string? RawScalarToString(object? rawValue) => rawValue switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        JsonElement el => JsonScalarToString(el),
        _ => Convert.ToString(rawValue, CultureInfo.InvariantCulture)
    };

    private static string? JsonScalarToString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => el.GetRawText()
    };

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
            return false;

        value = prop.GetString();
        return true;
    }

    private static DateTimeOffset? ResolveResourceEventTimeUtc(
        IReadOnlyDictionary<string, string>? attributes,
        string attributeName)
    {
        if (!TryGetAttributeValue(attributes, attributeName, out var rawTime) ||
            string.IsNullOrWhiteSpace(rawTime))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            rawTime.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static PolicyDecision BuildEscalationDecision(
        PolicyEvaluationInput input,
        Guid? policyId,
        int policyVersion,
        string matchedRuleId,
        string reason)
    {
        var snapshot = BuildEvaluationSnapshotJson(
            input,
            policyId,
            policyVersion,
            matchedRuleId,
            PolicyOutcome.EscalateToHumanReview,
            expectsApproval: false,
            proposalExpiresAt: null,
            shadowIntent: null);

        return new PolicyDecision(
            Outcome: PolicyOutcome.EscalateToHumanReview,
            PolicyId: policyId,
            PolicyVersion: policyVersion,
            MatchedRuleId: matchedRuleId,
            Reason: reason,
            ExpectsExternalApproval: false,
            InvokeLlmEnrichment: false,
            ProposalExpiresAt: null,
            Actions: Array.Empty<PolicyPlannedAction>(),
            ShadowIntent: null,
            EvaluationSnapshotJson: snapshot);
    }

    private static string BuildEvaluationSnapshotJson(
        PolicyEvaluationInput input,
        Guid? policyId,
        int policyVersion,
        string matchedRuleId,
        PolicyOutcome outcome,
        bool expectsApproval,
        DateTimeOffset? proposalExpiresAt,
        PolicyShadowIntent? shadowIntent)
    {
        var snapshot = new
        {
            tenantId = input.Tenant.TenantId,
            anomalyType = input.Anomaly.AnomalyType,
            resourceId = input.Anomaly.ResourceId,
            riskProbability = input.Anomaly.RiskProbability,
            policyId,
            policyVersion,
            matchedRuleId,
            outcome = outcome.ToString(),
            expectsExternalApproval = expectsApproval,
            autonomousExecutionEnabled = input.Tenant.AutonomousExecutionEnabled,
            shadowSuppressed = shadowIntent is not null,
            suppressionReason = shadowIntent?.SuppressionReason,
            proposalExpiresAt,
            evaluatedAtUtc = input.EvaluatedAtUtc,
            attributes = input.Attributes ?? new Dictionary<string, string>()
        };

        return JsonSerializer.Serialize(snapshot);
    }

    private sealed class PolicyDefinitionException : Exception
    {
        public PolicyDefinitionException(string message) : base(message) { }
    }
}
