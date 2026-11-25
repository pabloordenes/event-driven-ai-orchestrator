using System.Text.Json;
using FluentAssertions;
using Omro.Application.Policies;
using Omro.Application.Tenants;
using Omro.Domain.ResolutionTasks;
using Omro.Domain.Tenants;

namespace Omro.Domain.Tests.Policies;

public class JsonDecisionTablePolicyEngineTests
{
    private readonly JsonDecisionTablePolicyEngine _engine = new();
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("0.499", PolicyOutcome.NoAction, "deadline-low-risk-no-action", false)]
    [InlineData("0.50", PolicyOutcome.ProposeForApproval, "deadline-moderate-or-excluded-hitl", true)]
    [InlineData("0.5001", PolicyOutcome.ProposeForApproval, "deadline-moderate-or-excluded-hitl", true)]
    [InlineData("0.849", PolicyOutcome.ProposeForApproval, "deadline-moderate-or-excluded-hitl", true)]
    [InlineData("0.8499", PolicyOutcome.ProposeForApproval, "deadline-moderate-or-excluded-hitl", true)]
    public void ConfirmationDeadlineMissed_ScoreBoundariesBelowCritical_ShouldEvaluateHalfOpenRangesWithoutGaps(
        string scoreText,
        PolicyOutcome expectedOutcome,
        string expectedRuleId,
        bool expectedInvokeLlm)
    {
        var score = decimal.Parse(scoreText, System.Globalization.CultureInfo.InvariantCulture);
        var tenant = LoadMedCareSeedConfiguration(autonomousExecutionEnabled: true);
        var anomaly = new AnomalyEvent(score, Guid.NewGuid(), "ConfirmationDeadlineMissed", Guid.NewGuid());
        var attrs = new Dictionary<string, string>
        {
            ["effective_score"] = scoreText,
            ["final_notice_status"] = "Delivered"
        };

        var decision = _engine.Evaluate(new PolicyEvaluationInput(tenant, anomaly, attrs, FixedNow));

        decision.Outcome.Should().Be(expectedOutcome);
        decision.MatchedRuleId.Should().Be(expectedRuleId);
        decision.InvokeLlmEnrichment.Should().Be(expectedInvokeLlm);
        decision.ShadowIntent.Should().BeNull();

        if (expectedOutcome == PolicyOutcome.ProposeForApproval)
        {
            decision.ExpectsExternalApproval.Should().BeTrue();
            decision.ProposalExpiresAt.Should().Be(FixedNow.AddMinutes(180));
            decision.Actions.Should().ContainSingle(a => a.ActionType == "PROPOSE_RESOLUTION");
        }
        else
        {
            decision.Actions.Should().BeEmpty();
            decision.ProposalExpiresAt.Should().BeNull();
        }
    }

    [Theory]
    [InlineData("0.85")]
    [InlineData("0.8501")]
    [InlineData("1.00")]
    public void ConfirmationDeadlineMissed_CriticalScoreDelivered_WhenAutonomousOn_ShouldExecuteOverbook(string scoreText)
    {
        var score = decimal.Parse(scoreText, System.Globalization.CultureInfo.InvariantCulture);
        var tenant = LoadMedCareSeedConfiguration(autonomousExecutionEnabled: true);
        var anomaly = new AnomalyEvent(score, Guid.NewGuid(), "ConfirmationDeadlineMissed", Guid.NewGuid());
        var attrs = new Dictionary<string, string>
        {
            ["effective_score"] = scoreText,
            ["final_notice_status"] = "Delivered",
            ["clinical_flags"] = "[]",
            ["edad"] = "45"
        };

        var decision = _engine.Evaluate(new PolicyEvaluationInput(tenant, anomaly, attrs, FixedNow));

        decision.Outcome.Should().Be(PolicyOutcome.ExecuteActions);
        decision.MatchedRuleId.Should().Be("deadline-critical-autonomous-with-shadow-gate");
        decision.ExpectsExternalApproval.Should().BeFalse();
        decision.ShadowIntent.Should().BeNull();
        decision.ProposalExpiresAt.Should().BeNull();
        decision.Actions.Should().ContainSingle(a => a.ActionType == "GENERATE_OVERBOOK");
        decision.Actions[0].PayloadOverrides["decisionMode"].GetString().Should().Be("Autonomous");
    }

    [Fact]
    public void ConfirmationDeadlineMissed_CriticalScoreDelivered_WhenAutonomousOff_ShouldProposeHitlAndRecordShadowIntent()
    {
        var tenant = LoadMedCareSeedConfiguration(autonomousExecutionEnabled: false);
        var anomaly = new AnomalyEvent(0.85m, Guid.NewGuid(), "ConfirmationDeadlineMissed", Guid.NewGuid());
        var attrs = new Dictionary<string, string>
        {
            ["effective_score"] = "0.85",
            ["final_notice_status"] = "Delivered"
        };

        var decision = _engine.Evaluate(new PolicyEvaluationInput(tenant, anomaly, attrs, FixedNow));

        decision.Outcome.Should().Be(PolicyOutcome.ProposeForApproval);
        decision.MatchedRuleId.Should().Be("deadline-critical-autonomous-with-shadow-gate");
        decision.ExpectsExternalApproval.Should().BeTrue();
        decision.ProposalExpiresAt.Should().Be(FixedNow.AddMinutes(180));
        decision.Actions.Should().ContainSingle(a => a.ActionType == "PROPOSE_RESOLUTION");
        decision.Actions[0].DeferredActions.Should().ContainSingle(d => d.ActionType == "GENERATE_OVERBOOK");

        decision.ShadowIntent.Should().NotBeNull();
        decision.ShadowIntent!.IntendedActionType.Should().Be("GENERATE_OVERBOOK");
        decision.ShadowIntent.ActualActionType.Should().Be("PROPOSE_RESOLUTION");
        decision.ShadowIntent.SuppressionReason.Should().Be("AUTONOMOUS_EXECUTION_DISABLED");
    }

    [Fact]
    public void ConfirmationDeadlineMissed_WhenTenantAutonomousOn_ButEventAttributeFalse_ShouldFailClosedToShadowHitl()
    {
        var tenant = LoadMedCareSeedConfiguration(autonomousExecutionEnabled: true);
        var anomaly = new AnomalyEvent(0.92m, Guid.NewGuid(), "ConfirmationDeadlineMissed", Guid.NewGuid());
        var attrs = new Dictionary<string, string>
        {
            ["effective_score"] = "0.92",
            ["final_notification_delivered"] = "true",
            ["auto_overbook_enabled"] = "false"
        };

        var decision = _engine.Evaluate(new PolicyEvaluationInput(tenant, anomaly, attrs, FixedNow));

        decision.Outcome.Should().Be(PolicyOutcome.ProposeForApproval);
        decision.ShadowIntent.Should().NotBeNull();
        decision.ShadowIntent!.IntendedActionType.Should().Be("GENERATE_OVERBOOK");
        decision.ShadowIntent.ActualActionType.Should().Be("PROPOSE_RESOLUTION");
    }

    [Theory]
    [InlineData("final_notice_status", "Delivered")]
    [InlineData("final_notice_status", "Read")]
    [InlineData("final_notification_delivered", "true")]
    public void ConfirmationDeadlineMissed_ShouldAcceptDeliveredReadAndBooleanDeliveredAttributes(
        string attrKey,
        string attrValue)
    {
        var tenant = LoadMedCareSeedConfiguration(autonomousExecutionEnabled: true);
        var anomaly = new AnomalyEvent(0.91m, Guid.NewGuid(), "ConfirmationDeadlineMissed", Guid.NewGuid());
        var attrs = new Dictionary<string, string>
        {
            ["effective_score"] = "0.91",
            [attrKey] = attrValue
        };

        var decision = _engine.Evaluate(new PolicyEvaluationInput(tenant, anomaly, attrs, FixedNow));

        decision.Outcome.Should().Be(PolicyOutcome.ExecuteActions);
        decision.Actions.Should().ContainSingle(a => a.ActionType == "GENERATE_OVERBOOK");
    }

    [Theory]
    [InlineData("clinical_flags", "[\"oncologia\"]")]
    [InlineData("clinical_flags", "preoperatorio")]
    [InlineData("clinical_exclusion", "true")]
    [InlineData("edad", "70")]
    [InlineData("edad", "82")]
    public void ConfirmationDeadlineMissed_CriticalWithExclusionOrSeniorAge_ShouldDowngradeToHitlWithoutShadowDecision(
        string attrKey,
        string attrValue)
    {
        var tenant = LoadMedCareSeedConfiguration(autonomousExecutionEnabled: true);
        var anomaly = new AnomalyEvent(0.94m, Guid.NewGuid(), "ConfirmationDeadlineMissed", Guid.NewGuid());
        var attrs = new Dictionary<string, string>
        {
            ["effective_score"] = "0.94",
            ["final_notice_status"] = "Delivered",
            [attrKey] = attrValue
        };

        var decision = _engine.Evaluate(new PolicyEvaluationInput(tenant, anomaly, attrs, FixedNow));

        decision.Outcome.Should().Be(PolicyOutcome.ProposeForApproval);
        decision.MatchedRuleId.Should().Be("deadline-moderate-or-excluded-hitl");
        decision.ExpectsExternalApproval.Should().BeTrue();
        decision.ShadowIntent.Should().BeNull();
    }

    [Theory]
    [InlineData("final_notice_status", "Sent")]
    [InlineData("final_notice_status", "Failed")]
    [InlineData("final_notification_delivered", "false")]
    [InlineData(null, null)]
    public void ConfirmationDeadlineMissed_WhenFinalNoticeNotDelivered_ShouldReturnNoActionWithoutLlm(
        string? attrKey,
        string? attrValue)
    {
        var tenant = LoadMedCareSeedConfiguration(autonomousExecutionEnabled: true);
        var anomaly = new AnomalyEvent(0.97m, Guid.NewGuid(), "ConfirmationDeadlineMissed", Guid.NewGuid());
        var attrs = new Dictionary<string, string> { ["effective_score"] = "0.97" };
        if (attrKey is not null && attrValue is not null)
        {
            attrs[attrKey] = attrValue;
        }

        var decision = _engine.Evaluate(new PolicyEvaluationInput(tenant, anomaly, attrs, FixedNow));

        decision.Outcome.Should().Be(PolicyOutcome.NoAction);
        decision.MatchedRuleId.Should().Be("deadline-final-notice-not-delivered");
        decision.Reason.Should().Be("FINAL_NOTICE_NOT_DELIVERED");
        decision.InvokeLlmEnrichment.Should().BeFalse();
        decision.Actions.Should().BeEmpty();
    }

    [Fact]
    public void GenericTenantPolicy_ShouldSupportContainsAnyNotGtLteAndFeatureFlags()
    {
        var decisionTable = """
        {
          "schemaVersion": "1.0",
          "hitPolicy": "FIRST_MATCH",
          "rules": [
            {
              "ruleId": "vip-hub-escalation",
              "priority": 10,
              "when": {
                "all": [
                  { "field": "riskProbability", "op": "gt", "value": 0.70 },
                  { "field": "attributes.cargo_tags", "op": "contains_any", "values": [ "cold_chain", "hazmat" ] },
                  { "field": "tenant.flags.fast_reroute", "op": "eq", "value": true },
                  { "not": { "field": "attributes.hours_to_sla", "op": "lte", "value": 1 } }
                ]
              },
              "then": {
                "outcome": "ExecuteActions",
                "invokeLlmEnrichment": false,
                "actions": [
                  { "actionType": "REROUTE_SHIPMENT", "payloadOverrides": { "priority": "Urgent" } }
                ]
              }
            }
          ],
          "defaultDecision": {
            "outcome": "NoAction",
            "reason": "DEFAULT_SKIP",
            "invokeLlmEnrichment": false,
            "actions": []
          }
        }
        """;

        var tenant = CreateGenericTenant(
            "ShipmentDelayRisk",
            decisionTable,
            featureFlags: new Dictionary<string, JsonElement>
            {
                ["fast_reroute"] = JsonSerializer.SerializeToElement(true)
            });

        var match = _engine.Evaluate(new PolicyEvaluationInput(
            tenant,
            new AnomalyEvent(0.81m, Guid.NewGuid(), "ShipmentDelayRisk"),
            new Dictionary<string, string>
            {
                ["cargo_tags"] = "[\"fragile\", \"cold_chain\"]",
                ["hours_to_sla"] = "4"
            },
            FixedNow));

        match.Outcome.Should().Be(PolicyOutcome.ExecuteActions);
        match.MatchedRuleId.Should().Be("vip-hub-escalation");
        match.Actions.Should().ContainSingle(a => a.ActionType == "REROUTE_SHIPMENT");

        var fallback = _engine.Evaluate(new PolicyEvaluationInput(
            tenant,
            new AnomalyEvent(0.81m, Guid.NewGuid(), "ShipmentDelayRisk"),
            new Dictionary<string, string>
            {
                ["cargo_tags"] = "cold_chain",
                ["hours_to_sla"] = "1"
            },
            FixedNow));

        fallback.Outcome.Should().Be(PolicyOutcome.NoAction);
        fallback.MatchedRuleId.Should().Be("default-decision");
    }

    [Fact]
    public void Evaluate_WhenPolicyMissingOrCorruptOrExceedsMaxDepth_ShouldEscalateFailClosed()
    {
        var noPolicyTenant = CreateGenericTenant("OtherAnomaly", "{}");
        var missingDecision = _engine.Evaluate(new PolicyEvaluationInput(
            noPolicyTenant,
            new AnomalyEvent(0.95m, Guid.NewGuid(), "UnknownAnomaly"),
            null,
            FixedNow));

        missingDecision.Outcome.Should().Be(PolicyOutcome.EscalateToHumanReview);
        missingDecision.MatchedRuleId.Should().Be("no-active-policy");
        missingDecision.InvokeLlmEnrichment.Should().BeFalse();

        var corruptTenant = CreateGenericTenant("BadPolicy", "{ invalid json");
        var corruptDecision = _engine.Evaluate(new PolicyEvaluationInput(
            corruptTenant,
            new AnomalyEvent(0.95m, Guid.NewGuid(), "BadPolicy"),
            null,
            FixedNow));

        corruptDecision.Outcome.Should().Be(PolicyOutcome.EscalateToHumanReview);
        corruptDecision.MatchedRuleId.Should().Be("invalid-policy-definition");

        var tooDeepTable = """
        {
          "hitPolicy": "FIRST_MATCH",
          "rules": [
            {
              "ruleId": "deep",
              "when": {
                "all": [{ "any": [{ "all": [{ "any": [{ "all": [{ "field": "riskProbability", "op": "gte", "value": 0.5 }] }] }] }] }]
              },
              "then": { "outcome": "NoAction" }
            }
          ]
        }
        """;
        var deepTenant = CreateGenericTenant("DeepPolicy", tooDeepTable);
        var deepDecision = _engine.Evaluate(new PolicyEvaluationInput(
            deepTenant,
            new AnomalyEvent(0.95m, Guid.NewGuid(), "DeepPolicy"),
            null,
            FixedNow));

        deepDecision.Outcome.Should().Be(PolicyOutcome.EscalateToHumanReview);
        deepDecision.MatchedRuleId.Should().Be("invalid-policy-definition");
    }

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

    private static TenantConfiguration CreateGenericTenant(
        string anomalyType,
        string decisionTableJson,
        IReadOnlyDictionary<string, JsonElement>? featureFlags = null)
    {
        return new TenantConfiguration(
            TenantId: Guid.NewGuid(),
            TenantSlug: "generic-tenant",
            DisplayName: "Generic Tenant",
            IsActive: true,
            BasePromptContext: "Generic prompt",
            AutonomousExecutionEnabled: false,
            FeatureFlags: featureFlags ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase),
            HitlProposalTtlMinutes: 180,
            HitlProposalMinLeadMinutes: 60,
            DedupWindowMinutes: 60,
            AllowedOrigins: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Auth: new TenantAuthProfile(TenantAuthScheme.Rs256Jwt, "omro", "generic-api", "omro_service", null, null, null),
            MaxPayloadBytes: 65536,
            EgressTimeoutSeconds: 15,
            LlmSettings: new TenantLlmSettings(),
            ConfigVersion: 1,
            Actions: new Dictionary<string, TenantActionDescriptor>(StringComparer.OrdinalIgnoreCase),
            Policies: new Dictionary<string, TenantPolicyDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                [anomalyType] = new(Guid.NewGuid(), anomalyType, 1, 60, decisionTableJson)
            });
    }
}
