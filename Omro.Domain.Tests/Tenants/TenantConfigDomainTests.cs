using FluentAssertions;
using Omro.Application.Tenants;
using Omro.Domain.ResolutionTasks;
using Omro.Domain.Tenants;

namespace Omro.Domain.Tests.Tenants;

public class TenantConfigDomainTests
{
    [Fact]
    public void CreateTenantConfig_WithDefaults_ShouldHaveAutonomousExecutionDisabled()
    {
        var tenantId = Guid.NewGuid();
        var config = TenantConfig.Create(
            tenantId: tenantId,
            tenantSlug: "acme-ops",
            displayName: "Acme Operations",
            basePromptContext: "Operaciones generales de logística.",
            authAudience: "acme-api");

        config.TenantId.Should().Be(tenantId);
        config.IsActive.Should().BeTrue();
        config.AutonomousExecutionEnabled.Should().BeFalse();
        config.HitlProposalTtlMinutes.Should().Be(180);
        config.HitlProposalMinLeadMinutes.Should().Be(60);
        config.DedupWindowMinutes.Should().Be(60);
        config.AuthScheme.Should().Be(TenantAuthScheme.Rs256Jwt);
        config.AuthRole.Should().Be("omro_service");
        config.MaxPayloadBytes.Should().Be(65536);
        config.EgressTimeoutSeconds.Should().Be(15);
        config.ConfigVersion.Should().Be(1);
    }

    [Fact]
    public void CreateTenantConfig_WhenInvalidInvariants_ShouldThrow()
    {
        var validId = Guid.NewGuid();

        ((Action)(() => TenantConfig.Create(Guid.Empty, "slug", "Name", "Prompt", "aud")))
            .Should().Throw<ArgumentException>();
        ((Action)(() => TenantConfig.Create(validId, " ", "Name", "Prompt", "aud")))
            .Should().Throw<ArgumentException>();
        ((Action)(() => TenantConfig.Create(validId, "slug", "Name", " ", "aud")))
            .Should().Throw<ArgumentException>();
        ((Action)(() => TenantConfig.Create(validId, "slug", "Name", "Prompt", "aud", hitlProposalTtlMinutes: 0)))
            .Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => TenantConfig.Create(validId, "slug", "Name", "Prompt", "aud", featureFlagsJson: "not-json")))
            .Should().Throw<ArgumentException>();
        ((Action)(() => TenantConfig.Create(validId, "slug", "Name", "Prompt", "aud", allowedEndpointHostsJson: "{}")))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SetAutonomousExecution_AndSetActive_ShouldIncrementConfigVersion()
    {
        var config = TenantConfig.Create(Guid.NewGuid(), "acme", "Acme", "Prompt", "acme-api");
        var t1 = DateTimeOffset.UtcNow.AddMinutes(1);
        var t2 = DateTimeOffset.UtcNow.AddMinutes(2);

        config.SetAutonomousExecution(true, t1);
        config.AutonomousExecutionEnabled.Should().BeTrue();
        config.ConfigVersion.Should().Be(2);
        config.UpdatedAt.Should().Be(t1);

        config.SetActive(false, t2);
        config.IsActive.Should().BeFalse();
        config.ConfigVersion.Should().Be(3);
        config.UpdatedAt.Should().Be(t2);
    }

    [Fact]
    public void CreateTenantAction_AndAddAction_ShouldEnforceHttpMethodsUrisAndDuplicates()
    {
        var tenantId = Guid.NewGuid();
        var config = TenantConfig.Create(tenantId, "acme", "Acme", "Prompt", "acme-api");

        ((Action)(() => TenantAction.Create(
            tenantId,
            "BAD_URI",
            "Invalid URI",
            "/relative/path"))).Should().Throw<ArgumentException>();

        ((Action)(() => TenantAction.Create(
            tenantId,
            "BAD_METHOD",
            "Invalid method",
            "https://acme.test/hook",
            httpMethod: "DELETE"))).Should().Throw<ArgumentException>();

        var action = TenantAction.Create(
            tenantId,
            "REROUTE_ORDER",
            "Reroute logistics order",
            "https://acme.test/api/reroute",
            httpMethod: "post",
            payloadSchemaJson: "{\"type\":\"object\"}",
            defaultPayloadTemplateJson: "{\"resourceId\":\"{{resourceId}}\"}",
            llmEditableFieldsJson: "[\"reason\"]",
            requiresApproval: true,
            allowedPhases: TenantActionPhase.Immediate);

        action.HttpMethod.Should().Be("POST");
        action.AllowedPhases.Should().Be(TenantActionPhase.Immediate);

        config.AddAction(action);
        config.Actions.Should().ContainSingle();

        ((Action)(() => config.AddAction(action))).Should().Throw<InvalidOperationException>();
        ((Action)(() => config.AddAction(TenantAction.Create(
            Guid.NewGuid(), "OTHER", "Other tenant", "https://other.test/hook"))))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CreateTenantPolicy_AndAddPolicy_ShouldPreventDuplicateActiveOrSameVersion()
    {
        var tenantId = Guid.NewGuid();
        var config = TenantConfig.Create(tenantId, "acme", "Acme", "Prompt", "acme-api");

        var p1 = TenantPolicy.Create(tenantId, "DelayRisk", "{\"rules\":[]}", policyVersion: 1, isActive: true);
        var p1DupVersion = TenantPolicy.Create(tenantId, "DelayRisk", "{\"rules\":[]}", policyVersion: 1, isActive: false);
        var p2Active = TenantPolicy.Create(tenantId, "DelayRisk", "{\"rules\":[]}", policyVersion: 2, isActive: true);

        config.AddPolicy(p1);
        ((Action)(() => config.AddPolicy(p1DupVersion))).Should().Throw<InvalidOperationException>();
        ((Action)(() => config.AddPolicy(p2Active))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CreateShadowDecision_ShouldValidateRequiredIdentifiersAndRoundProbability()
    {
        var decision = ShadowDecision.Create(
            tenantId: Guid.NewGuid(),
            resolutionTaskId: Guid.NewGuid(),
            resourceId: Guid.NewGuid(),
            anomalyType: "DelayRisk",
            policyId: Guid.NewGuid(),
            policyVersion: 1,
            matchedRuleId: "rule-1",
            riskProbability: 0.91236m,
            intendedActionType: "AUTO_REROUTE",
            actualActionType: "PROPOSE_RESOLUTION",
            suppressionReason: "AUTONOMOUS_EXECUTION_DISABLED",
            evaluationSnapshotJson: "{\"score\":0.9124}",
            correlationId: Guid.NewGuid());

        decision.RiskProbability.Should().Be(0.9124m);

        ((Action)(() => ShadowDecision.Create(
            Guid.Empty,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "DelayRisk",
            Guid.NewGuid(),
            1,
            "rule-1",
            0.9m,
            "A",
            "B",
            "Reason",
            "{}",
            Guid.NewGuid()))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TenantConfiguration_ComputeHitlProposalExpiresAt_ShouldRespectTtlAndMinLeadCap()
    {
        var now = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        var snapshot = CreateSnapshot(hitlTtlMinutes: 180, hitlMinLeadMinutes: 60);

        // Sin tiempo de evento de recurso -> TTL normal (+180m)
        snapshot.ComputeHitlProposalExpiresAt(now).Should().Be(now.AddMinutes(180));

        // Evento de recurso lejano (+10h) -> cap (9h) > TTL (3h) -> prevalece TTL (+180m)
        snapshot.ComputeHitlProposalExpiresAt(now, now.AddHours(10)).Should().Be(now.AddMinutes(180));

        // Evento de recurso cercano (+120m) -> cap = +60m < TTL (+180m) -> prevalece cap (+60m)
        snapshot.ComputeHitlProposalExpiresAt(now, now.AddMinutes(120)).Should().Be(now.AddMinutes(60));

        // Evento de recurso dentro de ventana mínima (+30m) -> cap en el pasado -> devuelve now
        snapshot.ComputeHitlProposalExpiresAt(now, now.AddMinutes(30)).Should().Be(now);
    }

    [Fact]
    public void TenantConfiguration_ResolvePolicy_ShouldMatchExactOrFallbackWildcard()
    {
        var exactPolicy = new TenantPolicyDescriptor(Guid.NewGuid(), "DelayRisk", 2, 30, "{}");
        var wildcardPolicy = new TenantPolicyDescriptor(Guid.NewGuid(), "*", 1, null, "{}");

        var snapshot = CreateSnapshot(
            policies: new Dictionary<string, TenantPolicyDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["DelayRisk"] = exactPolicy,
                ["*"] = wildcardPolicy
            });

        snapshot.ResolvePolicy("delayrisk").Should().Be(exactPolicy);
        snapshot.ResolvePolicy("UnknownAnomaly").Should().Be(wildcardPolicy);

        var withoutWildcard = CreateSnapshot(
            policies: new Dictionary<string, TenantPolicyDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["DelayRisk"] = exactPolicy
            });

        withoutWildcard.ResolvePolicy("UnknownAnomaly").Should().BeNull();
    }

    private static TenantConfiguration CreateSnapshot(
        int hitlTtlMinutes = 180,
        int hitlMinLeadMinutes = 60,
        IReadOnlyDictionary<string, TenantPolicyDescriptor>? policies = null)
    {
        return new TenantConfiguration(
            TenantId: Guid.NewGuid(),
            TenantSlug: "test-tenant",
            DisplayName: "Test Tenant",
            IsActive: true,
            BasePromptContext: "Test context",
            AutonomousExecutionEnabled: false,
            FeatureFlags: new Dictionary<string, System.Text.Json.JsonElement>(),
            HitlProposalTtlMinutes: hitlTtlMinutes,
            HitlProposalMinLeadMinutes: hitlMinLeadMinutes,
            DedupWindowMinutes: 60,
            AllowedOrigins: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Auth: new TenantAuthProfile(TenantAuthScheme.Rs256Jwt, "omro", "test-api", "omro_service", null, null, null),
            MaxPayloadBytes: 65536,
            EgressTimeoutSeconds: 15,
            LlmSettings: new TenantLlmSettings(),
            ConfigVersion: 1,
            Actions: new Dictionary<string, TenantActionDescriptor>(StringComparer.OrdinalIgnoreCase),
            Policies: policies ?? new Dictionary<string, TenantPolicyDescriptor>(StringComparer.OrdinalIgnoreCase));
    }
}
