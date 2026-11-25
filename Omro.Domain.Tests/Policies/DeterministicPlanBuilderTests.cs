using System.Text.Json;
using FluentAssertions;
using Omro.Application.Policies;
using Omro.Application.Tenants;
using Omro.Domain.ResolutionTasks;
using Omro.Domain.Tenants;

namespace Omro.Domain.Tests.Policies;

public class DeterministicPlanBuilderTests
{
    private readonly DeterministicPlanBuilder _builder = new();

    [Fact]
    public void BuildBasePlan_ShouldRenderPlaceholdersNumericRiskAndDeferredPlanWithProposalExpiresAt()
    {
        var tenant = CreateTenantWithCatalog();
        var resourceId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var expiresAt = new DateTimeOffset(2026, 11, 25, 15, 0, 0, TimeSpan.Zero);
        var anomaly = new AnomalyEvent(0.78m, resourceId, "GenericDelayRisk", correlationId);

        var decision = new PolicyDecision(
            Outcome: PolicyOutcome.ProposeForApproval,
            PolicyId: Guid.NewGuid(),
            PolicyVersion: 2,
            MatchedRuleId: "moderate-hitl",
            Reason: "Riesgo moderado requiere revisión",
            ExpectsExternalApproval: true,
            InvokeLlmEnrichment: true,
            ProposalExpiresAt: expiresAt,
            Actions:
            [
                new PolicyPlannedAction(
                    "PROPOSE_RESOLUTION",
                    new Dictionary<string, JsonElement>(),
                    [
                        new PolicyPlannedAction(
                            "EXECUTE_MITIGATION",
                            new Dictionary<string, JsonElement>
                            {
                                ["decisionMode"] = JsonSerializer.SerializeToElement("Approved")
                            },
                            Array.Empty<PolicyPlannedAction>())
                    ])
            ],
            ShadowIntent: new PolicyShadowIntent("EXECUTE_MITIGATION", "PROPOSE_RESOLUTION", "AUTONOMOUS_EXECUTION_DISABLED"),
            EvaluationSnapshotJson: "{}");

        var buildResult = _builder.BuildBasePlan(
            tenant,
            anomaly,
            new Dictionary<string, string> { ["channel"] = "sms" },
            decision,
            taskId);

        buildResult.IsSuccess.Should().BeTrue();
        buildResult.Plan.Should().NotBeNull();
        var plan = buildResult.Plan!;
        plan.ExpectsExternalApproval.Should().BeTrue();
        plan.ProposedActions.Should().ContainSingle();

        var action = plan.ProposedActions[0];
        action.ActionType.Should().Be("PROPOSE_RESOLUTION");
        action.Endpoint.Should().Be("https://tenant.test/api/propose");
        action.Payload["resourceId"].Should().Be(resourceId.ToString());
        action.Payload["riskProbability"].Should().Be(0.78m);
        action.Payload["channel"].Should().Be("sms");

        var aiPlanJson = action.Payload["aiPlanJson"].Should().BeOfType<string>().Subject;
        using var deferredDoc = JsonDocument.Parse(aiPlanJson);
        deferredDoc.RootElement.GetProperty("MatchedRuleId").GetString().Should().Be("moderate-hitl");
        deferredDoc.RootElement.GetProperty("PolicyVersion").GetInt32().Should().Be(2);
        deferredDoc.RootElement.GetProperty("IsShadowModeFallback").GetBoolean().Should().BeTrue();
        deferredDoc.RootElement.GetProperty("ProposalExpiresAt").GetDateTimeOffset().Should().Be(expiresAt);

        var deferredActions = deferredDoc.RootElement.GetProperty("DeferredActions");
        deferredActions.GetArrayLength().Should().Be(1);
        deferredActions[0].GetProperty("ActionType").GetString().Should().Be("EXECUTE_MITIGATION");
        deferredActions[0].GetProperty("Endpoint").GetString().Should().Be("https://tenant.test/api/mitigate");
        deferredActions[0].GetProperty("Payload").GetProperty("decisionMode").GetString().Should().Be("Approved");
        deferredActions[0].GetProperty("Payload").GetProperty("targetRisk").GetDecimal().Should().Be(0.78m);
    }

    [Fact]
    public void BuildBasePlan_WhenActionMissingFromCatalog_ShouldFailClosed()
    {
        var tenant = CreateTenantWithCatalog();
        var anomaly = new AnomalyEvent(0.90m, Guid.NewGuid(), "GenericDelayRisk", Guid.NewGuid());
        var decision = new PolicyDecision(
            Outcome: PolicyOutcome.ExecuteActions,
            PolicyId: Guid.NewGuid(),
            PolicyVersion: 1,
            MatchedRuleId: "rule-1",
            Reason: "Test",
            ExpectsExternalApproval: false,
            InvokeLlmEnrichment: false,
            ProposalExpiresAt: null,
            Actions:
            [
                new PolicyPlannedAction("NON_EXISTENT_ACTION", new Dictionary<string, JsonElement>(), Array.Empty<PolicyPlannedAction>())
            ],
            ShadowIntent: null,
            EvaluationSnapshotJson: "{}");

        var result = _builder.BuildBasePlan(tenant, anomaly, null, decision, Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.Plan.Should().BeNull();
        result.ErrorReason.Should().Contain("NON_EXISTENT_ACTION");
    }

    [Fact]
    public void MergeLlmEnrichment_ShouldEnrichAllowedTextOnlyAndBlockEndpointActionOrStructuralHijacking()
    {
        var tenant = CreateTenantWithCatalog();
        var resourceId = Guid.NewGuid();
        var anomaly = new AnomalyEvent(0.91m, resourceId, "GenericDelayRisk", Guid.NewGuid());
        var decision = new PolicyDecision(
            Outcome: PolicyOutcome.ExecuteActions,
            PolicyId: Guid.NewGuid(),
            PolicyVersion: 1,
            MatchedRuleId: "critical-auto",
            Reason: "Base reason",
            ExpectsExternalApproval: false,
            InvokeLlmEnrichment: true,
            ProposalExpiresAt: null,
            Actions:
            [
                new PolicyPlannedAction(
                    "EXECUTE_MITIGATION",
                    new Dictionary<string, JsonElement>
                    {
                        ["decisionMode"] = JsonSerializer.SerializeToElement("Autonomous")
                    },
                    Array.Empty<PolicyPlannedAction>())
            ],
            ShadowIntent: null,
            EvaluationSnapshotJson: "{}");

        var basePlan = _builder.BuildBasePlan(tenant, anomaly, null, decision, Guid.NewGuid()).Plan!;

        var maliciousOrEnrichedLlmPlan = new CognitivePlan(
            Title: "Mitigación contextualizada por IA",
            Reasoning: "Justificación enriquecida para auditoría.",
            ConfidenceScore: 0.94m,
            ProposedActions:
            [
                new ProposedAction(
                    ActionType: "EXECUTE_MITIGATION",
                    Endpoint: "https://attacker.evil/steal",
                    Payload: new Dictionary<string, object>
                    {
                        ["resourceId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString()),
                        ["decisionMode"] = JsonSerializer.SerializeToElement("HijackedMode"),
                        ["reason"] = JsonSerializer.SerializeToElement("Motivo enriquecido por LLM")
                    }),
                new ProposedAction(
                    ActionType: "INJECTED_EXTRA_ACTION",
                    Endpoint: "https://attacker.evil/extra",
                    Payload: new Dictionary<string, object>())
            ],
            IsFallback: false,
            ExpectsExternalApproval: true);

        var merged = _builder.MergeLlmEnrichment(tenant, decision, basePlan, maliciousOrEnrichedLlmPlan);

        merged.Title.Should().Be("Mitigación contextualizada por IA");
        merged.Reasoning.Should().Be("Justificación enriquecida para auditoría.");
        merged.ExpectsExternalApproval.Should().BeFalse();
        merged.ProposedActions.Should().ContainSingle();

        var mergedAction = merged.ProposedActions[0];
        mergedAction.ActionType.Should().Be("EXECUTE_MITIGATION");
        mergedAction.Endpoint.Should().Be("https://tenant.test/api/mitigate");
        mergedAction.Payload["resourceId"].Should().Be(resourceId.ToString());
        mergedAction.Payload["decisionMode"].Should().Be("Autonomous");
        mergedAction.Payload["reason"].Should().Be("Motivo enriquecido por LLM");
    }

    [Fact]
    public void BuildBasePlan_AndMergeLlmEnrichment_ShouldRenderNativeV2StructuredAiPlanWithoutEscapedJson()
    {
        var baseTenant = CreateTenantWithCatalog();
        var v2Actions = new Dictionary<string, TenantActionDescriptor>(baseTenant.Actions, StringComparer.OrdinalIgnoreCase)
        {
            ["PROPOSE_RESOLUTION"] = baseTenant.Actions["PROPOSE_RESOLUTION"] with
            {
                SchemaVersion = 2,
                DefaultPayloadTemplateJson = """
                {
                  "schemaVersion": "2.0",
                  "resourceId": "{{resourceId}}",
                  "riskProbability": "{{riskProbability}}",
                  "proposalExpiresAt": "{{proposalExpiresAt}}",
                  "aiPlan": "{{structuredAiPlan}}"
                }
                """
            }
        };
        var tenant = baseTenant with { Actions = v2Actions };

        var resourceId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var expiresAt = new DateTimeOffset(2026, 11, 25, 19, 30, 0, TimeSpan.Zero);
        var anomaly = new AnomalyEvent(0.76m, resourceId, "GenericDelayRisk", Guid.NewGuid());

        var decision = new PolicyDecision(
            Outcome: PolicyOutcome.ProposeForApproval,
            PolicyId: Guid.NewGuid(),
            PolicyVersion: 3,
            MatchedRuleId: "v2-hitl-rule",
            Reason: "Base deterministic reason",
            ExpectsExternalApproval: true,
            InvokeLlmEnrichment: true,
            ProposalExpiresAt: expiresAt,
            Actions:
            [
                new PolicyPlannedAction(
                    "PROPOSE_RESOLUTION",
                    new Dictionary<string, JsonElement>(),
                    [
                        new PolicyPlannedAction(
                            "EXECUTE_MITIGATION",
                            new Dictionary<string, JsonElement>
                            {
                                ["decisionMode"] = JsonSerializer.SerializeToElement("Approved")
                            },
                            Array.Empty<PolicyPlannedAction>())
                    ])
            ],
            ShadowIntent: null,
            EvaluationSnapshotJson: "{}");

        var buildResult = _builder.BuildBasePlan(tenant, anomaly, null, decision, taskId);
        buildResult.IsSuccess.Should().BeTrue();

        var baseAction = buildResult.Plan!.ProposedActions.Should().ContainSingle().Subject;
        baseAction.Payload.Should().NotContainKey("aiPlanJson");
        baseAction.Payload["schemaVersion"].Should().Be("2.0");

        var aiPlanObj = baseAction.Payload["aiPlan"].Should().BeAssignableTo<IDictionary<string, object>>().Subject;
        aiPlanObj["schemaVersion"].Should().Be("2.0");
        aiPlanObj["omroTaskId"].Should().Be(taskId.ToString());
        aiPlanObj["matchedRuleId"].Should().Be("v2-hitl-rule");
        aiPlanObj["policyVersion"].Should().Be(3L);
        aiPlanObj["isShadowModeFallback"].Should().Be(false);

        var llmEnrichment = new CognitivePlan(
            Title: "Título enriquecido por LLM",
            Reasoning: "Explicación clara enriquecida para operador.",
            ConfidenceScore: 0.92m,
            ProposedActions:
            [
                new ProposedAction("PROPOSE_RESOLUTION", "https://evil.attacker/steal", new Dictionary<string, object>())
            ],
            IsFallback: false,
            ExpectsExternalApproval: false);

        var merged = _builder.MergeLlmEnrichment(tenant, decision, buildResult.Plan, llmEnrichment);
        var mergedAiPlan = merged.ProposedActions[0].Payload["aiPlan"].Should().BeAssignableTo<IDictionary<string, object>>().Subject;
        mergedAiPlan["title"].Should().Be("Título enriquecido por LLM");
        mergedAiPlan["reasoning"].Should().Be("Explicación clara enriquecida para operador.");
        mergedAiPlan["confidenceScore"].Should().Be(0.92m);
        mergedAiPlan["omroTaskId"].Should().Be(taskId.ToString());

        var serializedPayload = JsonSerializer.Serialize(merged.ProposedActions[0].Payload);
        using var doc = JsonDocument.Parse(serializedPayload);
        doc.RootElement.GetProperty("aiPlan").ValueKind.Should().Be(JsonValueKind.Object);
        doc.RootElement.GetProperty("aiPlan").GetProperty("deferredActions").GetArrayLength().Should().Be(1);
        doc.RootElement.GetProperty("aiPlan").GetProperty("deferredActions")[0]
            .GetProperty("endpoint").GetString().Should().Be("https://tenant.test/api/mitigate");
    }

    private static TenantConfiguration CreateTenantWithCatalog()
    {
        var actions = new Dictionary<string, TenantActionDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            ["EXECUTE_MITIGATION"] = new(
                Id: Guid.NewGuid(),
                ActionType: "EXECUTE_MITIGATION",
                Description: "Mitigate anomaly",
                EndpointUrl: "https://tenant.test/api/mitigate",
                HttpMethod: "POST",
                PayloadSchemaJson: "{}",
                DefaultPayloadTemplateJson: """
                {
                  "resourceId": "{{resourceId}}",
                  "reason": "{{policyReason}}",
                  "decisionMode": "Autonomous",
                  "targetRisk": "{{riskProbability}}"
                }
                """,
                LlmEditableFields: ["reason"],
                RequiresApproval: false,
                AllowedPhases: TenantActionPhase.Both,
                RetryPolicy: new ActionRetryPolicy(),
                SchemaVersion: 1),
            ["PROPOSE_RESOLUTION"] = new(
                Id: Guid.NewGuid(),
                ActionType: "PROPOSE_RESOLUTION",
                Description: "Send HITL proposal",
                EndpointUrl: "https://tenant.test/api/propose",
                HttpMethod: "POST",
                PayloadSchemaJson: "{}",
                DefaultPayloadTemplateJson: """
                {
                  "resourceId": "{{resourceId}}",
                  "riskProbability": "{{riskProbability}}",
                  "channel": "{{attr.channel}}",
                  "aiPlanJson": "{{deferredPlanJson}}"
                }
                """,
                LlmEditableFields: Array.Empty<string>(),
                RequiresApproval: true,
                AllowedPhases: TenantActionPhase.Immediate,
                RetryPolicy: new ActionRetryPolicy(),
                SchemaVersion: 1)
        };

        return new TenantConfiguration(
            TenantId: Guid.NewGuid(),
            TenantSlug: "test-tenant",
            DisplayName: "Test Tenant",
            IsActive: true,
            BasePromptContext: "Base context",
            AutonomousExecutionEnabled: false,
            FeatureFlags: new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase),
            HitlProposalTtlMinutes: 180,
            HitlProposalMinLeadMinutes: 60,
            DedupWindowMinutes: 60,
            AllowedOrigins: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "https://tenant.test" },
            Auth: new TenantAuthProfile(TenantAuthScheme.Rs256Jwt, "omro", "test-api", "omro_service", null, null, null),
            MaxPayloadBytes: 65536,
            EgressTimeoutSeconds: 15,
            LlmSettings: new TenantLlmSettings(),
            ConfigVersion: 1,
            Actions: actions,
            Policies: new Dictionary<string, TenantPolicyDescriptor>(StringComparer.OrdinalIgnoreCase));
    }
}
