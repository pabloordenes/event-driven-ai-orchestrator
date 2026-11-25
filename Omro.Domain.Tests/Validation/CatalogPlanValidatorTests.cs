using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Omro.Application.Policies;
using Omro.Application.Tenants;
using Omro.Application.Validation;
using Omro.Domain.ResolutionTasks;
using Omro.Domain.Tenants;
using Omro.Infrastructure.Configuration;
using Omro.Infrastructure.Validation;

namespace Omro.Domain.Tests.Validation;

public class CatalogPlanValidatorTests
{
    private readonly CatalogPlanValidator _validator = new(Options.Create(new PlanValidationOptions()));
    private readonly JsonDecisionTablePolicyEngine _policyEngine = new();
    private readonly DeterministicPlanBuilder _planBuilder = new();

    [Fact]
    public void ValidateImmediatePlan_MedCareAutonomousAndHitlPlans_ShouldPassAll7Gates()
    {
        var tenantAuto = LoadMedCareSeedConfiguration(autonomousExecutionEnabled: true);
        var resourceId = Guid.NewGuid();
        var anomaly = new AnomalyEvent(0.92m, resourceId, "ConfirmationDeadlineMissed", Guid.NewGuid());
        var attrs = new Dictionary<string, string>
        {
            ["effective_score"] = "0.92",
            ["final_notice_status"] = "Delivered"
        };

        var autoDecision = _policyEngine.Evaluate(new PolicyEvaluationInput(tenantAuto, anomaly, attrs, DateTimeOffset.UtcNow));
        var autoPlan = _planBuilder.BuildBasePlan(tenantAuto, anomaly, attrs, autoDecision, Guid.NewGuid()).Plan!;
        var autoResult = _validator.ValidateImmediatePlan(tenantAuto, autoDecision, autoPlan);

        autoResult.IsValid.Should().BeTrue();
        autoResult.Violations.Should().BeEmpty();
        autoResult.ValidatedActions.Should().ContainSingle(a => a.ActionType == "GENERATE_OVERBOOK");

        var tenantShadow = LoadMedCareSeedConfiguration(autonomousExecutionEnabled: false);
        var hitlDecision = _policyEngine.Evaluate(new PolicyEvaluationInput(tenantShadow, anomaly, attrs, DateTimeOffset.UtcNow));
        var hitlPlan = _planBuilder.BuildBasePlan(tenantShadow, anomaly, attrs, hitlDecision, Guid.NewGuid()).Plan!;
        var hitlResult = _validator.ValidateImmediatePlan(tenantShadow, hitlDecision, hitlPlan);

        hitlResult.IsValid.Should().BeTrue();
        hitlResult.Violations.Should().BeEmpty();
        hitlResult.ValidatedActions.Should().ContainSingle(a => a.ActionType == "PROPOSE_RESOLUTION");
    }

    [Fact]
    public void ValidateImmediatePlan_WhenUnknownActionTypeOrPolicyMismatch_ShouldFailClosed()
    {
        var tenant = LoadMedCareSeedConfiguration(autonomousExecutionEnabled: true);
        var decision = CreateAutonomousDecision("GENERATE_OVERBOOK");
        var maliciousPlan = new CognitivePlan(
            Title: "Evil",
            Reasoning: "Injected",
            ConfidenceScore: 0.99m,
            ProposedActions:
            [
                new ProposedAction("DROP_ALL_RESOURCES", "https://medcare.test/api/appointments/overbook", new Dictionary<string, object>
                {
                    ["resourceId"] = Guid.NewGuid().ToString(),
                    ["decisionMode"] = "Autonomous"
                })
            ],
            ExpectsExternalApproval: true);

        var result = _validator.ValidateImmediatePlan(tenant, decision, maliciousPlan);

        result.IsValid.Should().BeFalse();
        result.ValidatedActions.Should().BeEmpty();
        result.Violations.Should().Contain(v => v.Code == "POLICY_APPROVAL_MISMATCH");
        result.Violations.Should().Contain(v => v.Code == "POLICY_ACTION_MISMATCH");
        result.Violations.Should().Contain(v => v.Code == "UNKNOWN_ACTION_TYPE");
    }

    [Theory]
    [InlineData("https://evil.attacker.com/api/appointments/overbook", "ENDPOINT_NOT_IN_ALLOWLIST")]
    [InlineData("http://169.254.169.254/latest/meta-data/", "DISALLOWED_ENDPOINT")]
    [InlineData("http://medcare.test/api/appointments/overbook", "DISALLOWED_ENDPOINT")]
    [InlineData("https://medcare.test/api/appointments/different", "DISALLOWED_ENDPOINT")]
    [InlineData("https://user:pass@medcare.test/api/appointments/overbook", "DISALLOWED_ENDPOINT")]
    public void ValidateImmediatePlan_WhenEndpointViolatesAllowlistOrScheme_ShouldReject(
        string maliciousEndpoint,
        string expectedCode)
    {
        var tenant = LoadMedCareSeedConfiguration(autonomousExecutionEnabled: true);
        var decision = CreateAutonomousDecision("GENERATE_OVERBOOK");
        var plan = new CognitivePlan(
            Title: "Valid title",
            Reasoning: "Valid reason",
            ConfidenceScore: 0.9m,
            ProposedActions:
            [
                new ProposedAction("GENERATE_OVERBOOK", maliciousEndpoint, new Dictionary<string, object>
                {
                    ["resourceId"] = Guid.NewGuid().ToString(),
                    ["decisionMode"] = "Autonomous"
                })
            ],
            ExpectsExternalApproval: false);

        var result = _validator.ValidateImmediatePlan(tenant, decision, plan);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v => v.Code == expectedCode);
    }

    [Fact]
    public void ValidateImmediatePlan_WhenEmptyAllowlist_ShouldRejectWithEmptyAllowlistCode()
    {
        var tenant = LoadMedCareSeedConfiguration(autonomousExecutionEnabled: true) with
        {
            AllowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };
        var decision = CreateAutonomousDecision("GENERATE_OVERBOOK");
        var plan = new CognitivePlan(
            "Title",
            "Reason",
            0.9m,
            [
                new ProposedAction("GENERATE_OVERBOOK", "https://medcare.test/api/appointments/overbook", new Dictionary<string, object>
                {
                    ["resourceId"] = Guid.NewGuid().ToString(),
                    ["decisionMode"] = "Autonomous"
                })
            ]);

        var result = _validator.ValidateImmediatePlan(tenant, decision, plan);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v => v.Code == "EMPTY_ENDPOINT_ALLOWLIST");
    }

    [Fact]
    public void ValidateImmediatePlan_WhenPayloadViolatesJsonSchema_ShouldRejectWithSchemaViolation()
    {
        var tenant = LoadMedCareSeedConfiguration(autonomousExecutionEnabled: true);
        var decision = CreateAutonomousDecision("GENERATE_OVERBOOK");

        // Falta decisionMode, UUID inválido, enum fuera de rango, propiedad adicional inyectada y reason > 500
        var invalidPlan = new CognitivePlan(
            "Title",
            "Reason",
            0.9m,
            [
                new ProposedAction("GENERATE_OVERBOOK", "https://medcare.test/api/appointments/overbook", new Dictionary<string, object>
                {
                    ["resourceId"] = "not-a-uuid",
                    ["decisionMode"] = "RootOverride",
                    ["reason"] = new string('x', 501),
                    ["injectedField"] = "evil"
                })
            ]);

        var result = _validator.ValidateImmediatePlan(tenant, decision, invalidPlan);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v => v.Code == "SCHEMA_VALIDATION_FAILED");
        result.Violations.Count(v => v.Code == "SCHEMA_VALIDATION_FAILED").Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void ValidateImmediatePlan_WhenEmbeddedDeferredActionIsMalicious_ShouldRejectRecursively()
    {
        var tenant = LoadMedCareSeedConfiguration(autonomousExecutionEnabled: false);
        var decision = new PolicyDecision(
            Outcome: PolicyOutcome.ProposeForApproval,
            PolicyId: Guid.NewGuid(),
            PolicyVersion: 1,
            MatchedRuleId: "hitl",
            Reason: "Need approval",
            ExpectsExternalApproval: true,
            InvokeLlmEnrichment: false,
            ProposalExpiresAt: DateTimeOffset.UtcNow.AddHours(3),
            Actions: [new PolicyPlannedAction("PROPOSE_RESOLUTION", new Dictionary<string, JsonElement>(), [])],
            ShadowIntent: null,
            EvaluationSnapshotJson: "{}");

        var maliciousAiPlanJson = JsonSerializer.Serialize(new
        {
            DeferredActions = new[]
            {
                new
                {
                    ActionType = "GENERATE_OVERBOOK",
                    Endpoint = "https://attacker.evil/exfiltrate",
                    Payload = new
                    {
                        resourceId = Guid.NewGuid().ToString(),
                        decisionMode = "Approved"
                    }
                }
            }
        });

        var plan = new CognitivePlan(
            "HITL",
            "Reason",
            0.85m,
            [
                new ProposedAction("PROPOSE_RESOLUTION", "https://medcare.test/api/resolutions/propose", new Dictionary<string, object>
                {
                    ["resourceId"] = Guid.NewGuid().ToString(),
                    ["riskProbability"] = 0.85m,
                    ["aiPlanJson"] = maliciousAiPlanJson
                })
            ],
            ExpectsExternalApproval: true);

        var result = _validator.ValidateImmediatePlan(tenant, decision, plan);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v =>
            v.Code == "ENDPOINT_NOT_IN_ALLOWLIST" &&
            v.Path.Contains("aiPlanJson.deferredActions[0]", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateImmediatePlan_WhenPayloadExceedsSizeDepthOrActionCountLimits_ShouldReject()
    {
        var tenant = LoadMedCareSeedConfiguration(autonomousExecutionEnabled: true) with
        {
            MaxPayloadBytes = 256
        };
        var decision = CreateAutonomousDecision("GENERATE_OVERBOOK");

        var oversizedPlan = new CognitivePlan(
            "Large",
            "Reason",
            0.9m,
            [
                new ProposedAction("GENERATE_OVERBOOK", "https://medcare.test/api/appointments/overbook", new Dictionary<string, object>
                {
                    ["resourceId"] = Guid.NewGuid().ToString(),
                    ["decisionMode"] = "Autonomous",
                    ["reason"] = new string('a', 300)
                })
            ]);

        var oversizedResult = _validator.ValidateImmediatePlan(tenant, decision, oversizedPlan);
        oversizedResult.IsValid.Should().BeFalse();
        oversizedResult.Violations.Should().Contain(v => v.Code == "PAYLOAD_TOO_LARGE");

        // Profundidad > 4 (límite custom 4)
        var strictDepthValidator = new CatalogPlanValidator(Options.Create(new PlanValidationOptions { MaxJsonDepth = 3, MaxActionsPerPlan = 1 }));
        var deepTenant = tenant with { MaxPayloadBytes = 65536 };
        var deepPayload = new Dictionary<string, object>
        {
            ["resourceId"] = Guid.NewGuid().ToString(),
            ["decisionMode"] = "Autonomous",
            ["nested"] = new Dictionary<string, object>
            {
                ["level2"] = new Dictionary<string, object>
                {
                    ["level3"] = "too-deep"
                }
            }
        };

        var deepDecision = new PolicyDecision(
            PolicyOutcome.ExecuteActions,
            Guid.NewGuid(),
            1,
            "r",
            "reason",
            false,
            false,
            null,
            [
                new PolicyPlannedAction("GENERATE_OVERBOOK", new Dictionary<string, JsonElement>(), []),
                new PolicyPlannedAction("GENERATE_OVERBOOK", new Dictionary<string, JsonElement>(), [])
            ],
            null,
            "{}");

        var deepAndTooMany = new CognitivePlan(
            "Deep",
            "Reason",
            0.9m,
            [
                new ProposedAction("GENERATE_OVERBOOK", "https://medcare.test/api/appointments/overbook", deepPayload),
                new ProposedAction("GENERATE_OVERBOOK", "https://medcare.test/api/appointments/overbook", deepPayload)
            ]);

        var limitResult = strictDepthValidator.ValidateImmediatePlan(deepTenant, deepDecision, deepAndTooMany);
        limitResult.IsValid.Should().BeFalse();
        limitResult.Violations.Should().Contain(v => v.Code == "TOO_MANY_ACTIONS");
        limitResult.Violations.Should().Contain(v => v.Code == "PAYLOAD_TOO_DEEP");
    }

    [Fact]
    public void ValidateDeferredActions_ShouldValidatePhaseAllowlistLegacyResolutionAndMethod()
    {
        var tenant = LoadMedCareSeedConfiguration(autonomousExecutionEnabled: false);

        // 1. Legacy v1 sin ActionType (resuelve por endpoint exacto del catálogo y valida JSON Schema)
        var validLegacy = _validator.ValidateDeferredActions(tenant,
        [
            new DeferredActionCandidate(
                ActionType: null,
                Endpoint: "https://medcare.test/api/appointments/monitor",
                HttpMethod: null,
                Payload: new Dictionary<string, object>
                {
                    ["resourceId"] = Guid.NewGuid().ToString(),
                    ["requiresAction"] = true
                })
        ]);

        validLegacy.IsValid.Should().BeTrue();
        validLegacy.ValidatedActions.Should().ContainSingle(a => a.ActionType == "MONITOR_RESOURCE");

        // 2. Endpoint desconocido sin ActionType -> UNKNOWN_DEFERRED_ENDPOINT
        var unknownEndpoint = _validator.ValidateDeferredActions(tenant,
        [
            new DeferredActionCandidate(null, "https://medcare.test/api/unknown", null, new())
        ]);
        unknownEndpoint.IsValid.Should().BeFalse();
        unknownEndpoint.Violations.Should().Contain(v => v.Code == "UNKNOWN_DEFERRED_ENDPOINT");

        // 3. PROPOSE_RESOLUTION tiene AllowedPhases = Immediate -> prohibida en fase Deferred + método DELETE inválido
        var wrongPhaseAndMethod = _validator.ValidateDeferredActions(tenant,
        [
            new DeferredActionCandidate(
                "PROPOSE_RESOLUTION",
                "https://medcare.test/api/resolutions/propose",
                "DELETE",
                new Dictionary<string, object>
                {
                    ["resourceId"] = Guid.NewGuid().ToString(),
                    ["riskProbability"] = 0.7m,
                    ["aiPlanJson"] = "{}"
                })
        ]);
        wrongPhaseAndMethod.IsValid.Should().BeFalse();
        wrongPhaseAndMethod.Violations.Should().Contain(v => v.Code == "PHASE_NOT_ALLOWED");
        wrongPhaseAndMethod.Violations.Should().Contain(v => v.Code == "DISALLOWED_HTTP_METHOD");
    }

    private static PolicyDecision CreateAutonomousDecision(string actionType) =>
        new(
            Outcome: PolicyOutcome.ExecuteActions,
            PolicyId: Guid.NewGuid(),
            PolicyVersion: 1,
            MatchedRuleId: "rule-auto",
            Reason: "Score >= 0.85",
            ExpectsExternalApproval: false,
            InvokeLlmEnrichment: false,
            ProposalExpiresAt: null,
            Actions: [new PolicyPlannedAction(actionType, new Dictionary<string, JsonElement>(), [])],
            ShadowIntent: null,
            EvaluationSnapshotJson: "{}");

    private static TenantConfiguration LoadMedCareSeedConfiguration(bool autonomousExecutionEnabled)
    {
        var seedPath = Path.Combine(AppContext.BaseDirectory, "Seeds", "medcare.tenant.json");
        var json = File.ReadAllText(seedPath)
            .Replace("${OMRO_SEED_MEDCARE_BASE_URL:-https://medcare-cqrs-clinical-api.onrender.com}", "https://medcare.test", StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var actions = new Dictionary<string, TenantActionDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var actionEl in root.GetProperty("actions").EnumerateArray())
        {
            var actionType = actionEl.GetProperty("actionType").GetString()!;
            actions[actionType] = new TenantActionDescriptor(
                Id: Guid.NewGuid(),
                ActionType: actionType,
                Description: actionEl.GetProperty("description").GetString()!,
                EndpointUrl: actionEl.GetProperty("endpointUrl").GetString()!,
                HttpMethod: actionEl.GetProperty("httpMethod").GetString()!,
                PayloadSchemaJson: actionEl.GetProperty("payloadSchema").GetRawText(),
                DefaultPayloadTemplateJson: actionEl.GetProperty("defaultPayloadTemplate").GetRawText(),
                LlmEditableFields: actionEl.GetProperty("llmEditableFields").EnumerateArray().Select(e => e.GetString()!).ToArray(),
                RequiresApproval: actionEl.GetProperty("requiresApproval").GetBoolean(),
                AllowedPhases: Enum.Parse<TenantActionPhase>(actionEl.GetProperty("allowedPhases").GetString()!, ignoreCase: true),
                RetryPolicy: new ActionRetryPolicy(),
                SchemaVersion: actionEl.GetProperty("schemaVersion").GetInt32());
        }

        var policies = new Dictionary<string, TenantPolicyDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var policyEl in root.GetProperty("policies").EnumerateArray())
        {
            var anomalyType = policyEl.GetProperty("anomalyType").GetString()!;
            policies[anomalyType] = new TenantPolicyDescriptor(
                Id: Guid.NewGuid(),
                AnomalyType: anomalyType,
                PolicyVersion: policyEl.GetProperty("policyVersion").GetInt32(),
                DedupWindowMinutes: policyEl.GetProperty("dedupWindowMinutes").GetInt32(),
                DecisionTableJson: policyEl.GetProperty("decisionTable").GetRawText());
        }

        return new TenantConfiguration(
            TenantId: root.GetProperty("tenantId").GetGuid(),
            TenantSlug: root.GetProperty("tenantSlug").GetString()!,
            DisplayName: root.GetProperty("displayName").GetString()!,
            IsActive: true,
            BasePromptContext: root.GetProperty("basePromptContext").GetString()!,
            AutonomousExecutionEnabled: autonomousExecutionEnabled,
            FeatureFlags: new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase),
            HitlProposalTtlMinutes: root.GetProperty("hitlProposalTtlMinutes").GetInt32(),
            HitlProposalMinLeadMinutes: root.GetProperty("hitlProposalMinLeadMinutes").GetInt32(),
            DedupWindowMinutes: root.GetProperty("dedupWindowMinutes").GetInt32(),
            AllowedOrigins: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "https://medcare.test" },
            Auth: new TenantAuthProfile(TenantAuthScheme.Rs256Jwt, "omro-orchestrator", "medcareos-api", "omro_service", null, null, null),
            MaxPayloadBytes: 65536,
            EgressTimeoutSeconds: 15,
            LlmSettings: new TenantLlmSettings(),
            ConfigVersion: 1,
            Actions: actions,
            Policies: policies);
    }
}
