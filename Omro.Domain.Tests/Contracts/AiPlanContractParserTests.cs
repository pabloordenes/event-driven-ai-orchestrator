using System.Text.Json;
using FluentAssertions;
using Omro.Application.Contracts;
using Omro.Contracts.Events;
using Omro.Contracts.Plans;

namespace Omro.Domain.Tests.Contracts;

public class AiPlanContractParserTests
{
    private readonly AiPlanContractParser _parser = new();

    [Fact]
    public void Parse_V2StructuredAiPlanWithNullAiPlanJson_ShouldExtractTypedDeferredActions()
    {
        var omroTaskId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var expiresAt = new DateTimeOffset(2026, 11, 25, 18, 0, 0, TimeSpan.Zero);

        var structuredPlan = new StructuredAiPlan(
            SchemaVersion: PlanContractVersions.V2Structured,
            Title: "HITL Mitigation",
            Reasoning: "Requiere aprobación manual",
            ConfidenceScore: 0.89m,
            OmroTaskId: omroTaskId,
            MatchedRuleId: "rule-hitl",
            PolicyVersion: 2,
            ProposalExpiresAt: expiresAt,
            IsShadowModeFallback: true,
            DeferredActions:
            [
                new DeferredActionContract(
                    ActionType: "EXECUTE_MITIGATION",
                    Endpoint: "https://tenant.test/api/mitigate",
                    HttpMethod: "POST",
                    Payload: JsonSerializer.SerializeToElement(new
                    {
                        resourceId = resourceId.ToString(),
                        decisionMode = "Approved",
                        targetRisk = 0.89m,
                        attemptCount = 1,
                        tags = new[] { "hitl", "priority" }
                    }))
            ]);

        var message = new ResolutionApproved(
            TenantId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            ResolutionTaskId: Guid.NewGuid(),
            ResourceId: resourceId,
            AiPlanJson: null,
            SchemaVersion: PlanContractVersions.V2Structured,
            AiPlan: structuredPlan);

        var result = _parser.Parse(message);

        result.IsSuccess.Should().BeTrue();
        result.Plan.Should().NotBeNull();
        result.Plan!.IsLegacyFormat.Should().BeFalse();
        result.Plan.SchemaVersion.Should().Be(PlanContractVersions.V2Structured);
        result.Plan.OmroTaskId.Should().Be(omroTaskId);
        result.Plan.ProposalExpiresAt.Should().Be(expiresAt);
        result.Plan.Title.Should().Be("HITL Mitigation");
        result.Plan.Reasoning.Should().Be("Requiere aprobación manual");

        result.Plan.DeferredActions.Should().ContainSingle();
        var action = result.Plan.DeferredActions[0];
        action.ActionType.Should().Be("EXECUTE_MITIGATION");
        action.Endpoint.Should().Be("https://tenant.test/api/mitigate");
        action.HttpMethod.Should().Be("POST");
        action.Payload!["resourceId"].Should().Be(resourceId.ToString());
        action.Payload["decisionMode"].Should().Be("Approved");
        action.Payload["attemptCount"].Should().Be(1L);
        action.Payload["targetRisk"].Should().Be(0.89m);
    }

    [Fact]
    public void Parse_WhenBothAiPlanAndAiPlanJsonProvided_ShouldPreferStructuredAiPlanV2()
    {
        var message = new ResolutionApproved(
            TenantId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            ResolutionTaskId: Guid.NewGuid(),
            ResourceId: Guid.NewGuid(),
            AiPlanJson: """{"DeferredActions":[{"Endpoint":"https://legacy.test/old","Payload":{}}]}""",
            SchemaVersion: PlanContractVersions.V2Structured,
            AiPlan: new StructuredAiPlan(
                SchemaVersion: PlanContractVersions.V2Structured,
                Title: "V2 Preferred",
                Reasoning: "Structured wins",
                ConfidenceScore: 0.95m,
                OmroTaskId: null,
                MatchedRuleId: "r1",
                PolicyVersion: 1,
                ProposalExpiresAt: null,
                IsShadowModeFallback: false,
                DeferredActions:
                [
                    new DeferredActionContract(
                        "PREFERRED_ACTION",
                        "https://tenant.test/api/v2",
                        "POST",
                        JsonSerializer.SerializeToElement(new { ok = true }))
                ]));

        var result = _parser.Parse(message);

        result.IsSuccess.Should().BeTrue();
        result.Plan!.IsLegacyFormat.Should().BeFalse();
        result.Plan.DeferredActions.Should().ContainSingle(a =>
            a.ActionType == "PREFERRED_ACTION" &&
            a.Endpoint == "https://tenant.test/api/v2");
    }

    [Fact]
    public void Parse_V1LegacyAiPlanJsonWithoutActionType_ShouldParseAsLegacyFormat()
    {
        var message = new ResolutionApproved(
            TenantId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            ResolutionTaskId: Guid.NewGuid(),
            ResourceId: Guid.NewGuid(),
            AiPlanJson: """
                        {
                          "DeferredActions": [
                            {
                              "Endpoint": "https://tenant.test/api/monitor",
                              "Payload": { "requiresAction": true }
                            }
                          ]
                        }
                        """);

        var result = _parser.Parse(message);

        result.IsSuccess.Should().BeTrue();
        result.Plan!.IsLegacyFormat.Should().BeTrue();
        result.Plan.SchemaVersion.Should().Be(PlanContractVersions.V1Legacy);
        result.Plan.DeferredActions.Should().ContainSingle();
        result.Plan.DeferredActions[0].ActionType.Should().BeNull();
        result.Plan.DeferredActions[0].Endpoint.Should().Be("https://tenant.test/api/monitor");
        result.Plan.DeferredActions[0].Payload!["requiresAction"].Should().Be(true);
    }

    [Fact]
    public void Parse_V2JsonStringInsideAiPlanJsonWhenAiPlanIsNull_ShouldParseAndPreserveV2Version()
    {
        var omroTaskId = Guid.NewGuid();
        var v2String = JsonSerializer.Serialize(new
        {
            schemaVersion = "2.0",
            title = "Serialized V2",
            reasoning = "Sent via legacy field",
            omroTaskId,
            deferredActions = new[]
            {
                new
                {
                    actionType = "ACT_V2",
                    endpoint = "https://tenant.test/api/v2",
                    httpMethod = "PUT",
                    payload = new { flag = true }
                }
            }
        });

        var message = new ResolutionApproved(
            TenantId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            ResolutionTaskId: Guid.NewGuid(),
            ResourceId: Guid.NewGuid(),
            AiPlanJson: v2String,
            SchemaVersion: PlanContractVersions.V2Structured,
            AiPlan: null);

        var result = _parser.Parse(message);

        result.IsSuccess.Should().BeTrue();
        result.Plan!.IsLegacyFormat.Should().BeTrue();
        result.Plan.SchemaVersion.Should().Be("2.0");
        result.Plan.OmroTaskId.Should().Be(omroTaskId);
        result.Plan.DeferredActions.Should().ContainSingle(a =>
            a.ActionType == "ACT_V2" &&
            a.HttpMethod == "PUT");
    }

    [Theory]
    [InlineData("3.0")]
    [InlineData("0.9")]
    public void Parse_WhenSchemaVersionUnsupported_ShouldFailClosed(string unsupportedVersion)
    {
        var message = new ResolutionApproved(
            TenantId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            ResolutionTaskId: Guid.NewGuid(),
            ResourceId: Guid.NewGuid(),
            AiPlanJson: """{"DeferredActions":[]}""",
            SchemaVersion: unsupportedVersion);

        var result = _parser.Parse(message);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("UNSUPPORTED_SCHEMA_VERSION");
    }

    [Fact]
    public void Parse_WhenMalformedJsonMissingPlanOrNonObjectPayload_ShouldFailClosed()
    {
        var malformed = new ResolutionApproved(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{not-json");
        _parser.Parse(malformed).ErrorCode.Should().Be("INVALID_AI_PLAN_JSON");

        var missing = new ResolutionApproved(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null);
        _parser.Parse(missing).ErrorCode.Should().Be("MISSING_AI_PLAN");

        var nonObjectPayload = new ResolutionApproved(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AiPlanJson: null,
            SchemaVersion: PlanContractVersions.V2Structured,
            AiPlan: new StructuredAiPlan(
                PlanContractVersions.V2Structured,
                "T",
                "R",
                0.8m,
                null,
                null,
                null,
                null,
                false,
                [new DeferredActionContract("ACT", "https://tenant.test/a", "POST", JsonSerializer.SerializeToElement("not-an-object"))]));

        _parser.Parse(nonObjectPayload).ErrorCode.Should().Be("INVALID_ACTION_PAYLOAD");
    }
}
