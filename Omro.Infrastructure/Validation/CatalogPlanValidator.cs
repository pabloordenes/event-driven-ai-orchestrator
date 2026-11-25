using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.Extensions.Options;
using Omro.Application.Interfaces;
using Omro.Application.Policies;
using Omro.Application.Tenants;
using Omro.Application.Validation;
using Omro.Domain.ResolutionTasks;
using Omro.Domain.Tenants;
using Omro.Infrastructure.Configuration;

namespace Omro.Infrastructure.Validation;

public sealed class CatalogPlanValidator : IPlanValidator
{
    private readonly PlanValidationOptions _options;
    private readonly ConcurrentDictionary<string, JsonSchema> _schemaCache = new(StringComparer.Ordinal);

    public CatalogPlanValidator(IOptions<PlanValidationOptions>? options = null)
    {
        _options = options?.Value ?? new PlanValidationOptions();
    }

    public PlanValidationResult ValidateImmediatePlan(
        TenantConfiguration tenant,
        PolicyDecision decision,
        CognitivePlan plan)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(plan);

        var violations = new List<PlanValidationViolation>();
        var validated = new List<ValidatedActionSpec>();

        // Gate 1: Conformidad Política <-> Plan
        if (plan.ExpectsExternalApproval != decision.ExpectsExternalApproval)
        {
            violations.Add(new PlanValidationViolation(
                "POLICY_APPROVAL_MISMATCH",
                null,
                "expectsExternalApproval",
                $"ExpectsExternalApproval ({plan.ExpectsExternalApproval}) no coincide con la política ({decision.ExpectsExternalApproval})."));
        }

        var proposedActions = plan.ProposedActions ?? new List<ProposedAction>();
        if (proposedActions.Count != decision.Actions.Count)
        {
            violations.Add(new PlanValidationViolation(
                "POLICY_ACTION_COUNT_MISMATCH",
                null,
                "proposedActions",
                $"Cantidad de acciones ({proposedActions.Count}) no coincide con la política ({decision.Actions.Count})."));
        }

        // Gate 6 (fan-out)
        var maxActions = Math.Max(1, _options.MaxActionsPerPlan);
        if (proposedActions.Count > maxActions)
        {
            violations.Add(new PlanValidationViolation(
                "TOO_MANY_ACTIONS",
                null,
                "proposedActions",
                $"El plan supera el límite máximo de acciones permitido ({proposedActions.Count} > {maxActions})."));
        }

        var checkCount = Math.Min(proposedActions.Count, decision.Actions.Count);
        for (int i = 0; i < checkCount; i++)
        {
            var proposed = proposedActions[i];
            var expectedAction = decision.Actions[i];
            if (!string.Equals(proposed.ActionType, expectedAction.ActionType, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(new PlanValidationViolation(
                    "POLICY_ACTION_MISMATCH",
                    i,
                    $"proposedActions[{i}].actionType",
                    $"ActionType '{proposed.ActionType}' no coincide con la política '{expectedAction.ActionType}'."));
            }
        }

        if (proposedActions.Count > 0 && tenant.AllowedOrigins.Count == 0)
        {
            violations.Add(new PlanValidationViolation(
                "EMPTY_ENDPOINT_ALLOWLIST",
                null,
                "tenant.allowedOrigins",
                $"El tenant '{tenant.TenantSlug}' no tiene orígenes permitidos configurados."));
        }

        for (int i = 0; i < proposedActions.Count; i++)
        {
            var action = proposedActions[i];
            var pathPrefix = $"proposedActions[{i}]";

            // Gate 2: Existencia en catálogo
            if (!tenant.TryGetAction(action.ActionType, out var descriptor))
            {
                violations.Add(new PlanValidationViolation(
                    "UNKNOWN_ACTION_TYPE",
                    i,
                    $"{pathPrefix}.actionType",
                    $"ActionType '{action.ActionType}' no existe o está inactivo en el catálogo del tenant '{tenant.TenantSlug}'."));
                continue;
            }

            // Gate 3: Fase permitida y flag RequiresApproval
            if (descriptor.AllowedPhases is not (TenantActionPhase.Immediate or TenantActionPhase.Both))
            {
                violations.Add(new PlanValidationViolation(
                    "PHASE_NOT_ALLOWED",
                    i,
                    $"{pathPrefix}.allowedPhases",
                    $"La acción '{descriptor.ActionType}' no está permitida en fase Immediate (AllowedPhases={descriptor.AllowedPhases})."));
            }

            if (descriptor.RequiresApproval && !plan.ExpectsExternalApproval)
            {
                violations.Add(new PlanValidationViolation(
                    "APPROVAL_FLAG_MISMATCH",
                    i,
                    $"{pathPrefix}.requiresApproval",
                    $"La acción '{descriptor.ActionType}' requiere aprobación externa pero ExpectsExternalApproval=false."));
            }

            // Gate 4: Allowlist y endpoint exacto
            ValidateEndpointAndMethod(
                tenant,
                descriptor,
                action.Endpoint,
                requestedMethod: null,
                actionIndex: i,
                pathPrefix: pathPrefix,
                violations: violations);

            // Gates 5 & 6: Payload JSON Schema + profundidad + tamaño + validación recursiva de DeferredActions
            var payloadJson = JsonSerializer.Serialize(action.Payload ?? new Dictionary<string, object>());
            if (ValidatePayloadGates(tenant, descriptor, payloadJson, i, $"{pathPrefix}.payload", violations, out var rootNode))
            {
                ValidateEmbeddedDeferredActions(tenant, rootNode, i, $"{pathPrefix}.payload", violations);
            }

            if (!violations.Any(v => v.ActionIndex == i))
            {
                validated.Add(new ValidatedActionSpec(
                    descriptor.ActionType,
                    descriptor.EndpointUrl,
                    descriptor.HttpMethod,
                    payloadJson,
                    descriptor.RequiresApproval,
                    descriptor.RetryPolicy));
            }
        }

        // Gate 7: Fail-Closed
        return violations.Count == 0
            ? PlanValidationResult.Valid(validated)
            : PlanValidationResult.Invalid(violations);
    }

    public PlanValidationResult ValidateDeferredActions(
        TenantConfiguration tenant,
        IReadOnlyList<DeferredActionCandidate> actions)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(actions);

        var violations = new List<PlanValidationViolation>();
        var validated = new List<ValidatedActionSpec>();

        var maxActions = Math.Max(1, _options.MaxActionsPerPlan);
        if (actions.Count > maxActions)
        {
            violations.Add(new PlanValidationViolation(
                "TOO_MANY_ACTIONS",
                null,
                "deferredActions",
                $"El plan diferido supera el límite máximo de acciones permitido ({actions.Count} > {maxActions})."));
        }

        if (actions.Count > 0 && tenant.AllowedOrigins.Count == 0)
        {
            violations.Add(new PlanValidationViolation(
                "EMPTY_ENDPOINT_ALLOWLIST",
                null,
                "tenant.allowedOrigins",
                $"El tenant '{tenant.TenantSlug}' no tiene orígenes permitidos configurados."));
        }

        var normalizedCandidates = actions.Select(a => (
            a.ActionType,
            a.Endpoint,
            a.HttpMethod,
            PayloadJson: JsonSerializer.Serialize(a.Payload ?? new Dictionary<string, object>()))).ToList();

        ValidateDeferredListInternal(tenant, normalizedCandidates, "deferredActions", violations, validated);

        return violations.Count == 0
            ? PlanValidationResult.Valid(validated)
            : PlanValidationResult.Invalid(violations);
    }

    private void ValidateDeferredListInternal(
        TenantConfiguration tenant,
        IReadOnlyList<(string? ActionType, string Endpoint, string? HttpMethod, string PayloadJson)> candidates,
        string listPrefix,
        List<PlanValidationViolation> violations,
        List<ValidatedActionSpec>? validatedOutput)
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var pathPrefix = $"{listPrefix}[{i}]";
            TenantActionDescriptor? descriptor;

            if (!string.IsNullOrWhiteSpace(candidate.ActionType))
            {
                if (!tenant.TryGetAction(candidate.ActionType, out descriptor))
                {
                    violations.Add(new PlanValidationViolation(
                        "UNKNOWN_ACTION_TYPE",
                        i,
                        $"{pathPrefix}.actionType",
                        $"ActionType diferido '{candidate.ActionType}' no existe o está inactivo en el catálogo."));
                    continue;
                }
            }
            else
            {
                if (!TryNormalizeEndpoint(candidate.Endpoint, _options.AllowInsecureHttpEndpoints, out var normalizedCandidateUri, out _))
                {
                    violations.Add(new PlanValidationViolation(
                        "DISALLOWED_ENDPOINT",
                        i,
                        $"{pathPrefix}.endpoint",
                        $"Endpoint diferido inválido o inseguro: '{candidate.Endpoint}'."));
                    continue;
                }

                var matches = tenant.Actions.Values
                    .Where(a => TryNormalizeEndpoint(a.EndpointUrl, _options.AllowInsecureHttpEndpoints, out var catalogUri, out _) &&
                                string.Equals(catalogUri, normalizedCandidateUri, StringComparison.Ordinal))
                    .ToList();

                if (matches.Count != 1)
                {
                    violations.Add(new PlanValidationViolation(
                        "UNKNOWN_DEFERRED_ENDPOINT",
                        i,
                        $"{pathPrefix}.endpoint",
                        $"Endpoint diferido '{candidate.Endpoint}' no corresponde exactamente a ninguna acción activa del catálogo."));
                    continue;
                }

                descriptor = matches[0];
            }

            if (descriptor.AllowedPhases is not (TenantActionPhase.Deferred or TenantActionPhase.Both))
            {
                violations.Add(new PlanValidationViolation(
                    "PHASE_NOT_ALLOWED",
                    i,
                    $"{pathPrefix}.allowedPhases",
                    $"La acción '{descriptor.ActionType}' no está permitida en fase Deferred (AllowedPhases={descriptor.AllowedPhases})."));
            }

            ValidateEndpointAndMethod(
                tenant,
                descriptor,
                candidate.Endpoint,
                candidate.HttpMethod,
                i,
                pathPrefix,
                violations);

            ValidatePayloadGates(tenant, descriptor, candidate.PayloadJson, i, $"{pathPrefix}.payload", violations, out _);

            if (validatedOutput is not null && !violations.Any(v => v.ActionIndex == i && v.Path.StartsWith(pathPrefix, StringComparison.Ordinal)))
            {
                validatedOutput.Add(new ValidatedActionSpec(
                    descriptor.ActionType,
                    descriptor.EndpointUrl,
                    descriptor.HttpMethod,
                    candidate.PayloadJson,
                    descriptor.RequiresApproval,
                    descriptor.RetryPolicy));
            }
        }
    }

    private void ValidateEndpointAndMethod(
        TenantConfiguration tenant,
        TenantActionDescriptor descriptor,
        string candidateEndpoint,
        string? requestedMethod,
        int actionIndex,
        string pathPrefix,
        List<PlanValidationViolation> violations)
    {
        if (!TryNormalizeEndpoint(candidateEndpoint, _options.AllowInsecureHttpEndpoints, out var normalizedCandidate, out var candidateOrigin))
        {
            violations.Add(new PlanValidationViolation(
                "DISALLOWED_ENDPOINT",
                actionIndex,
                $"{pathPrefix}.endpoint",
                $"Endpoint inválido o esquema no permitido: '{candidateEndpoint}'."));
            return;
        }

        if (!tenant.AllowedOrigins.Contains(candidateOrigin))
        {
            violations.Add(new PlanValidationViolation(
                "ENDPOINT_NOT_IN_ALLOWLIST",
                actionIndex,
                $"{pathPrefix}.endpoint",
                $"El origen '{candidateOrigin}' no está en la allowlist del tenant '{tenant.TenantSlug}'."));
            return;
        }

        if (!TryNormalizeEndpoint(descriptor.EndpointUrl, _options.AllowInsecureHttpEndpoints, out var normalizedCatalogEndpoint, out _))
        {
            violations.Add(new PlanValidationViolation(
                "DISALLOWED_ENDPOINT",
                actionIndex,
                $"{pathPrefix}.endpoint",
                $"Endpoint configurado en catálogo inválido para '{descriptor.ActionType}'."));
            return;
        }

        if (!string.Equals(normalizedCandidate, normalizedCatalogEndpoint, StringComparison.Ordinal))
        {
            violations.Add(new PlanValidationViolation(
                "DISALLOWED_ENDPOINT",
                actionIndex,
                $"{pathPrefix}.endpoint",
                $"Endpoint '{candidateEndpoint}' no coincide exactamente con el endpoint autorizado '{descriptor.EndpointUrl}'."));
        }

        if (!string.IsNullOrWhiteSpace(requestedMethod) &&
            !string.Equals(requestedMethod.Trim(), descriptor.HttpMethod, StringComparison.OrdinalIgnoreCase))
        {
            violations.Add(new PlanValidationViolation(
                "DISALLOWED_HTTP_METHOD",
                actionIndex,
                $"{pathPrefix}.httpMethod",
                $"Método HTTP '{requestedMethod}' no permitido; el catálogo exige '{descriptor.HttpMethod}'."));
        }
    }

    private bool ValidatePayloadGates(
        TenantConfiguration tenant,
        TenantActionDescriptor descriptor,
        string payloadJson,
        int actionIndex,
        string pathPrefix,
        List<PlanValidationViolation> violations,
        out JsonNode? parsedPayloadNode)
    {
        parsedPayloadNode = null;

        // Gate 6: Tamaño en bytes UTF-8
        var payloadByteCount = Encoding.UTF8.GetByteCount(payloadJson);
        if (payloadByteCount > tenant.MaxPayloadBytes)
        {
            violations.Add(new PlanValidationViolation(
                "PAYLOAD_TOO_LARGE",
                actionIndex,
                pathPrefix,
                $"Tamaño de payload ({payloadByteCount} bytes) supera el límite del tenant ({tenant.MaxPayloadBytes} bytes)."));
            return false;
        }

        // Gate 6: Profundidad JSON
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var depth = MeasureJsonDepth(doc.RootElement);
            var maxDepth = Math.Max(1, _options.MaxJsonDepth);
            if (depth > maxDepth)
            {
                violations.Add(new PlanValidationViolation(
                    "PAYLOAD_TOO_DEEP",
                    actionIndex,
                    pathPrefix,
                    $"Profundidad JSON ({depth}) supera el máximo permitido ({maxDepth})."));
                return false;
            }
        }
        catch (JsonException ex)
        {
            violations.Add(new PlanValidationViolation(
                "INVALID_PAYLOAD_JSON",
                actionIndex,
                pathPrefix,
                $"Payload JSON inválido: {ex.Message}"));
            return false;
        }

        try
        {
            parsedPayloadNode = JsonNode.Parse(payloadJson);
        }
        catch (JsonException ex)
        {
            violations.Add(new PlanValidationViolation(
                "INVALID_PAYLOAD_JSON",
                actionIndex,
                pathPrefix,
                $"Payload JSON inválido: {ex.Message}"));
            return false;
        }

        // Gate 5: JSON Schema Draft 2020-12
        JsonSchema schema;
        try
        {
            schema = _schemaCache.GetOrAdd(descriptor.PayloadSchemaJson, static text => JsonSchema.FromText(text));
        }
        catch (Exception ex)
        {
            violations.Add(new PlanValidationViolation(
                "INVALID_ACTION_SCHEMA",
                actionIndex,
                pathPrefix,
                $"El JSON Schema configurado para '{descriptor.ActionType}' es inválido: {ex.Message}"));
            return false;
        }

        var evalOptions = new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true
        };

        var evalResult = schema.Evaluate(parsedPayloadNode, evalOptions);
        if (!evalResult.IsValid)
        {
            var addedDetail = false;
            if (evalResult.Details is { Count: > 0 })
            {
                foreach (var detail in evalResult.Details.Where(d => d.HasErrors && d.Errors is { Count: > 0 }))
                {
                    foreach (var err in detail.Errors!)
                    {
                        var location = detail.InstanceLocation.ToString();
                        var fullPath = string.IsNullOrEmpty(location) ? pathPrefix : $"{pathPrefix}{location}";
                        violations.Add(new PlanValidationViolation(
                            "SCHEMA_VALIDATION_FAILED",
                            actionIndex,
                            fullPath,
                            $"{err.Key}: {err.Value}"));
                        addedDetail = true;
                    }
                }
            }

            if (!addedDetail && evalResult.Errors is { Count: > 0 })
            {
                foreach (var err in evalResult.Errors)
                {
                    violations.Add(new PlanValidationViolation(
                        "SCHEMA_VALIDATION_FAILED",
                        actionIndex,
                        pathPrefix,
                        $"{err.Key}: {err.Value}"));
                    addedDetail = true;
                }
            }

            if (!addedDetail)
            {
                violations.Add(new PlanValidationViolation(
                    "SCHEMA_VALIDATION_FAILED",
                    actionIndex,
                    pathPrefix,
                    $"El payload no cumple el esquema JSON de la acción '{descriptor.ActionType}'."));
            }
        }

        return true;
    }

    private void ValidateEmbeddedDeferredActions(
        TenantConfiguration tenant,
        JsonNode? payloadNode,
        int parentActionIndex,
        string pathPrefix,
        List<PlanValidationViolation> violations)
    {
        if (payloadNode is not JsonObject payloadObj)
            return;

        JsonArray? deferredArray = null;
        string embeddedPrefix = $"{pathPrefix}.deferredActions";

        if (TryGetPropertyIgnoreCase(payloadObj, "aiPlanJson", out var aiPlanJsonNode) &&
            aiPlanJsonNode is JsonValue aiPlanVal &&
            aiPlanVal.TryGetValue<string>(out var aiPlanJsonText) &&
            !string.IsNullOrWhiteSpace(aiPlanJsonText))
        {
            try
            {
                var parsedAiPlan = JsonNode.Parse(aiPlanJsonText);
                if (parsedAiPlan is JsonObject aiPlanObj &&
                    TryGetPropertyIgnoreCase(aiPlanObj, "deferredActions", out var nestedArrNode) &&
                    nestedArrNode is JsonArray nestedArr)
                {
                    deferredArray = nestedArr;
                    embeddedPrefix = $"{pathPrefix}.aiPlanJson.deferredActions";
                }
            }
            catch (JsonException ex)
            {
                violations.Add(new PlanValidationViolation(
                    "INVALID_EMBEDDED_PLAN_JSON",
                    parentActionIndex,
                    $"{pathPrefix}.aiPlanJson",
                    $"aiPlanJson no es un JSON válido: {ex.Message}"));
                return;
            }
        }
        else if (TryGetPropertyIgnoreCase(payloadObj, "aiPlan", out var aiPlanNode) &&
                 aiPlanNode is JsonObject aiPlanObj &&
                 TryGetPropertyIgnoreCase(aiPlanObj, "deferredActions", out var objArrNode) &&
                 objArrNode is JsonArray objArr)
        {
            deferredArray = objArr;
            embeddedPrefix = $"{pathPrefix}.aiPlan.deferredActions";
        }
        else if (TryGetPropertyIgnoreCase(payloadObj, "deferredActions", out var directArrNode) &&
                 directArrNode is JsonArray directArr)
        {
            deferredArray = directArr;
        }

        if (deferredArray is null)
            return;

        var maxActions = Math.Max(1, _options.MaxActionsPerPlan);
        if (deferredArray.Count > maxActions)
        {
            violations.Add(new PlanValidationViolation(
                "TOO_MANY_ACTIONS",
                parentActionIndex,
                embeddedPrefix,
                $"Las acciones diferidas embebidas ({deferredArray.Count}) superan el límite ({maxActions})."));
        }

        var candidates = new List<(string? ActionType, string Endpoint, string? HttpMethod, string PayloadJson)>();
        for (int j = 0; j < deferredArray.Count; j++)
        {
            if (deferredArray[j] is not JsonObject itemObj)
            {
                violations.Add(new PlanValidationViolation(
                    "INVALID_DEFERRED_ACTION",
                    parentActionIndex,
                    $"{embeddedPrefix}[{j}]",
                    "Cada elemento de DeferredActions debe ser un objeto JSON."));
                continue;
            }

            var actionType = TryGetStringIgnoreCase(itemObj, "actionType");
            var endpoint = TryGetStringIgnoreCase(itemObj, "endpoint") ?? string.Empty;
            var httpMethod = TryGetStringIgnoreCase(itemObj, "httpMethod");
            var nestedPayloadJson = TryGetPropertyIgnoreCase(itemObj, "payload", out var pNode) && pNode is not null
                ? pNode.ToJsonString()
                : "{}";

            candidates.Add((actionType, endpoint, httpMethod, nestedPayloadJson));
        }

        if (candidates.Count > 0)
        {
            var nestedViolations = new List<PlanValidationViolation>();
            ValidateDeferredListInternal(tenant, candidates, embeddedPrefix, nestedViolations, validatedOutput: null);
            foreach (var v in nestedViolations)
            {
                violations.Add(v with { ActionIndex = parentActionIndex });
            }
        }
    }

    private static bool TryNormalizeEndpoint(
        string? rawEndpoint,
        bool allowInsecureHttp,
        out string normalizedUrl,
        out string origin)
    {
        normalizedUrl = string.Empty;
        origin = string.Empty;

        if (string.IsNullOrWhiteSpace(rawEndpoint))
            return false;

        var trimmed = rawEndpoint.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return false;

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            return false;

        if (trimmed.Contains("..", StringComparison.Ordinal))
            return false;

        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        if (!isHttps && !(allowInsecureHttp && isHttp))
            return false;

        origin = $"{uri.Scheme.ToLowerInvariant()}://{uri.Authority.ToLowerInvariant()}";
        normalizedUrl = $"{origin}{uri.AbsolutePath}{uri.Query}";
        return true;
    }

    private static int MeasureJsonDepth(JsonElement root)
    {
        int maxDepth = 0;
        var stack = new Stack<(JsonElement Element, int Depth)>();
        stack.Push((root, 1));

        while (stack.Count > 0)
        {
            var (el, depth) = stack.Pop();
            if (depth > maxDepth)
                maxDepth = depth;

            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                    stack.Push((prop.Value, depth + 1));
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                    stack.Push((item, depth + 1));
            }
        }

        return maxDepth;
    }

    private static bool TryGetPropertyIgnoreCase(JsonObject obj, string name, out JsonNode? node)
    {
        if (obj.TryGetPropertyValue(name, out node))
            return true;

        foreach (var kvp in obj)
        {
            if (string.Equals(kvp.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                node = kvp.Value;
                return true;
            }
        }

        node = null;
        return false;
    }

    private static string? TryGetStringIgnoreCase(JsonObject obj, string name)
    {
        if (!TryGetPropertyIgnoreCase(obj, name, out var node) || node is not JsonValue val)
            return null;

        return val.TryGetValue<string>(out var str) ? str : null;
    }
}
