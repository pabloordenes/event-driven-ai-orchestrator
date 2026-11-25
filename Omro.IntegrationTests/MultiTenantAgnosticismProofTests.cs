using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Omro.Application.Contracts;
using Omro.Application.Interfaces;
using Omro.Application.Policies;
using Omro.Application.ResolutionTasks.Commands.IngestAnomaly;
using Omro.Application.ResolutionTasks.Services;
using Omro.Contracts.Events;
using Omro.Contracts.Plans;
using Omro.Domain.Repositories;
using Omro.Domain.ResolutionTasks;
using Omro.Infrastructure.Configuration;
using Omro.Infrastructure.HttpClients;
using Omro.Infrastructure.Messaging.Configuration;
using Omro.Infrastructure.Messaging.Observers;
using Omro.Infrastructure.Persistence;
using Omro.Infrastructure.Persistence.Repositories;
using Omro.Infrastructure.Security;
using Omro.Infrastructure.Tenants;
using Omro.Infrastructure.Validation;
using Omro.Worker.Consumers;

namespace Omro.IntegrationTests;

public class MultiTenantAgnosticismProofTests
{
    private static readonly Guid MedCareTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid NovaLogisticsTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string NovaHmacSecret = "nova-super-secret-hmac-key-2026";
    private const string ServiceIssuer = "omro-orchestrator";

    [Fact]
    public async Task NovaLogistics_AutonomousOff_ShouldRecordShadowDecision_SendHmacSignedV2Proposal_AndExecutePutRerouteOnV2Approval()
    {
        await using var ctx = await AgnosticTestHost.CreateAsync();
        await ctx.SeedNovaLogisticsAsync(autonomousExecutionEnabled: false);

        var shipmentId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();

        await ctx.Harness.Bus.Publish(new AnomalyDetected(
            TenantId: NovaLogisticsTenantId,
            CorrelationId: correlationId,
            AnomalyType: "ShipmentDelayRisk",
            ResourceId: shipmentId,
            RiskProbability: 0.90m,
            Attributes: new Dictionary<string, string>
            {
                ["customs_cleared"] = "true",
                ["cargo_tier"] = "cold_chain",
                ["delay_hours"] = "6"
            }));

        var waitingTask = await ctx.WaitForTaskAsync(t =>
            t.TenantId == NovaLogisticsTenantId &&
            t.Anomaly.ResourceId == shipmentId &&
            t.Status == ResolutionStatus.WaitingForExternalData);

        waitingTask.Should().NotBeNull("NovaLogistics debe proponer resolución HITL sin cambios de código en OMRO");
        waitingTask!.ExpectsExternalApproval.Should().BeTrue();
        waitingTask.ProposalExpiresAt.Should().NotBeNull();
        waitingTask.ProposalExpiresAt!.Value.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(100));

        using (var scope = ctx.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var shadow = await db.ShadowDecisions.FirstOrDefaultAsync(s => s.ResolutionTaskId == waitingTask.Id);
            shadow.Should().NotBeNull("la compuerta de autonomía apagada debe registrar ShadowDecision para REROUTE_SHIPMENT");
            shadow!.IntendedActionType.Should().Be("REROUTE_SHIPMENT");
            shadow.ActualActionType.Should().Be("REQUEST_DISPATCHER_APPROVAL");
            shadow.SuppressionReason.Should().Be("AUTONOMOUS_EXECUTION_DISABLED");
        }

        var proposalReq = ctx.CapturedRequests.Should().ContainSingle(r =>
            r.Uri.AbsoluteUri == "https://nova-ops.test/api/approvals/propose").Subject;

        proposalReq.Method.Should().Be("POST");
        proposalReq.BearerToken.Should().BeNull("NovaLogistics está configurado solo con HmacSha256");
        proposalReq.TenantIdHeader.Should().Be(NovaLogisticsTenantId.ToString());
        proposalReq.IdempotencyKey.Should().NotBeNullOrWhiteSpace();
        proposalReq.IdempotencyKey!.Length.Should().BeLessThanOrEqualTo(100);
        VerifyHmacSha256Signature(proposalReq, NovaHmacSecret);

        using var proposalDoc = JsonDocument.Parse(proposalReq.Body);
        proposalDoc.RootElement.GetProperty("schemaVersion").GetString().Should().Be(PlanContractVersions.V2Structured);
        proposalDoc.RootElement.GetProperty("shipmentId").GetGuid().Should().Be(shipmentId);
        proposalDoc.RootElement.TryGetProperty("aiPlanJson", out _).Should().BeFalse();

        var aiPlanEl = proposalDoc.RootElement.GetProperty("aiPlan");
        aiPlanEl.ValueKind.Should().Be(JsonValueKind.Object);
        var structuredPlan = JsonSerializer.Deserialize<StructuredAiPlan>(
            aiPlanEl.GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        structuredPlan.Should().NotBeNull();
        structuredPlan!.OmroTaskId.Should().Be(waitingTask.Id);
        structuredPlan.IsShadowModeFallback.Should().BeTrue();
        structuredPlan.Title.Should().Be("[ShipmentDelayRisk] Enriquecido por FakeLLM");
        structuredPlan.DeferredActions.Should().ContainSingle(a =>
            a.ActionType == "REROUTE_SHIPMENT" &&
            a.HttpMethod == "PUT" &&
            a.Endpoint == "https://nova-ops.test/api/shipments/reroute");

        // Aprobar con contrato v2 nativo (AiPlanJson == null)
        await ctx.Harness.Bus.Publish(new ResolutionApproved(
            TenantId: NovaLogisticsTenantId,
            CorrelationId: Guid.NewGuid(),
            ResolutionTaskId: Guid.NewGuid(),
            ResourceId: shipmentId,
            AiPlanJson: null,
            SchemaVersion: PlanContractVersions.V2Structured,
            AiPlan: structuredPlan));

        var resolvedTask = await ctx.WaitForTaskAsync(t =>
            t.Id == waitingTask.Id && t.Status == ResolutionStatus.Resolved);

        resolvedTask.Should().NotBeNull();
        resolvedTask!.ActionExecutions.Should().HaveCount(2);
        resolvedTask.ActionExecutions.Should().OnlyContain(e => e.Status == ActionExecutionStatus.Succeeded);

        var rerouteReq = ctx.CapturedRequests.Should().ContainSingle(r =>
            r.Uri.AbsoluteUri == "https://nova-ops.test/api/shipments/reroute").Subject;

        rerouteReq.Method.Should().Be("PUT");
        rerouteReq.BearerToken.Should().BeNull();
        rerouteReq.TenantIdHeader.Should().Be(NovaLogisticsTenantId.ToString());
        rerouteReq.IdempotencyKey.Should().NotBeNullOrWhiteSpace();
        rerouteReq.IdempotencyKey!.Length.Should().BeLessThanOrEqualTo(100);
        rerouteReq.IdempotencyKey.Should().NotBe(proposalReq.IdempotencyKey);
        VerifyHmacSha256Signature(rerouteReq, NovaHmacSecret);

        using var rerouteDoc = JsonDocument.Parse(rerouteReq.Body);
        rerouteDoc.RootElement.GetProperty("shipmentId").GetGuid().Should().Be(shipmentId);
        rerouteDoc.RootElement.GetProperty("executionMode").GetString().Should().Be("Approved");
    }

    [Fact]
    public async Task NovaLogistics_AutonomousOn_ShouldExecutePutRerouteDirectly_WithEnrichedNote_AndBlockLlmHijack()
    {
        await using var ctx = await AgnosticTestHost.CreateAsync();
        await ctx.SeedNovaLogisticsAsync(autonomousExecutionEnabled: true);

        var shipmentId = Guid.NewGuid();
        await ctx.Harness.Bus.Publish(new AnomalyDetected(
            TenantId: NovaLogisticsTenantId,
            CorrelationId: Guid.NewGuid(),
            AnomalyType: "ShipmentDelayRisk",
            ResourceId: shipmentId,
            RiskProbability: 0.91m,
            Attributes: new Dictionary<string, string>
            {
                ["customs_cleared"] = "true",
                ["cargo_tier"] = "cold_chain",
                ["delay_hours"] = "5"
            }));

        var task = await ctx.WaitForTaskAsync(t =>
            t.TenantId == NovaLogisticsTenantId &&
            t.Anomaly.ResourceId == shipmentId &&
            t.Status == ResolutionStatus.Resolved);

        task.Should().NotBeNull();
        task!.ExpectsExternalApproval.Should().BeFalse();

        using (var scope = ctx.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.ShadowDecisions.CountAsync(s => s.ResolutionTaskId == task.Id)).Should().Be(0);
        }

        ctx.CapturedRequests.Should().NotContain(r => r.Uri.Host.Contains("evil.attacker.test", StringComparison.OrdinalIgnoreCase));
        var rerouteReq = ctx.CapturedRequests.Should().ContainSingle(r =>
            r.Uri.AbsoluteUri == "https://nova-ops.test/api/shipments/reroute").Subject;

        rerouteReq.Method.Should().Be("PUT");
        VerifyHmacSha256Signature(rerouteReq, NovaHmacSecret);

        using var bodyDoc = JsonDocument.Parse(rerouteReq.Body);
        bodyDoc.RootElement.GetProperty("shipmentId").GetGuid().Should().Be(shipmentId);
        bodyDoc.RootElement.GetProperty("executionMode").GetString().Should().Be("Autonomous");
        bodyDoc.RootElement.GetProperty("dispatchNote").GetString().Should().Be("Despachador notificado: ruta aérea alterna prioritaria vía FakeLLM.");
        bodyDoc.RootElement.TryGetProperty("evilInjectedField", out _).Should().BeFalse();
    }

    [Fact]
    public async Task NovaLogistics_CustomsNotCleared_ShouldReturnNoActionWithoutLlmOrWebhook()
    {
        await using var ctx = await AgnosticTestHost.CreateAsync();
        await ctx.SeedNovaLogisticsAsync(autonomousExecutionEnabled: true);

        var shipmentId = Guid.NewGuid();
        await ctx.Harness.Bus.Publish(new AnomalyDetected(
            TenantId: NovaLogisticsTenantId,
            CorrelationId: Guid.NewGuid(),
            AnomalyType: "ShipmentDelayRisk",
            ResourceId: shipmentId,
            RiskProbability: 0.95m,
            Attributes: new Dictionary<string, string>
            {
                ["customs_cleared"] = "false",
                ["cargo_tier"] = "standard",
                ["delay_hours"] = "8"
            }));

        var task = await ctx.WaitForTaskAsync(t =>
            t.TenantId == NovaLogisticsTenantId &&
            t.Anomaly.ResourceId == shipmentId &&
            t.Status == ResolutionStatus.Resolved);

        task.Should().NotBeNull();
        ctx.FakeLlm.CallCount.Should().Be(0);
        ctx.CapturedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task MedCareAndNovaLogistics_LoadedSimultaneously_ShouldExecuteConcurrentlyWithStrictAuthAndCatalogIsolation()
    {
        await using var ctx = await AgnosticTestHost.CreateAsync();
        await ctx.SeedMedCareAsync(autonomousExecutionEnabled: true);
        await ctx.SeedNovaLogisticsAsync(autonomousExecutionEnabled: true);

        var medCareResourceId = Guid.NewGuid();
        var novaShipmentId = Guid.NewGuid();

        var medCarePublish = ctx.Harness.Bus.Publish(new AnomalyDetected(
            TenantId: MedCareTenantId,
            CorrelationId: Guid.NewGuid(),
            AnomalyType: "ConfirmationDeadlineMissed",
            ResourceId: medCareResourceId,
            RiskProbability: 0.91m,
            Attributes: new Dictionary<string, string>
            {
                ["effective_score"] = "0.91",
                ["final_notice_status"] = "Delivered",
                ["clinical_flags"] = "[]",
                ["edad"] = "35"
            }));

        var novaPublish = ctx.Harness.Bus.Publish(new AnomalyDetected(
            TenantId: NovaLogisticsTenantId,
            CorrelationId: Guid.NewGuid(),
            AnomalyType: "ShipmentDelayRisk",
            ResourceId: novaShipmentId,
            RiskProbability: 0.93m,
            Attributes: new Dictionary<string, string>
            {
                ["customs_cleared"] = "true",
                ["cargo_tier"] = "cold_chain",
                ["delay_hours"] = "7"
            }));

        await Task.WhenAll(medCarePublish, novaPublish);

        var medCareTask = await ctx.WaitForTaskAsync(t =>
            t.TenantId == MedCareTenantId &&
            t.Anomaly.ResourceId == medCareResourceId &&
            t.Status == ResolutionStatus.Resolved);

        var novaTask = await ctx.WaitForTaskAsync(t =>
            t.TenantId == NovaLogisticsTenantId &&
            t.Anomaly.ResourceId == novaShipmentId &&
            t.Status == ResolutionStatus.Resolved);

        medCareTask.Should().NotBeNull();
        novaTask.Should().NotBeNull();

        ctx.CapturedRequests.Should().HaveCount(2);

        var medCareReq = ctx.CapturedRequests.Should().ContainSingle(r =>
            r.TenantIdHeader == MedCareTenantId.ToString()).Subject;
        medCareReq.Method.Should().Be("POST");
        medCareReq.Uri.AbsoluteUri.Should().Be("https://medcare.test/api/appointments/overbook");
        medCareReq.Signature.Should().BeNull("MedCare utiliza Rs256Jwt, no HmacSha256");
        VerifyJwtToken(medCareReq.BearerToken, ctx.DefaultRsaKey, expectedAudience: "medcareos-api", expectedTenantId: MedCareTenantId);

        var novaReq = ctx.CapturedRequests.Should().ContainSingle(r =>
            r.TenantIdHeader == NovaLogisticsTenantId.ToString()).Subject;
        novaReq.Method.Should().Be("PUT");
        novaReq.Uri.AbsoluteUri.Should().Be("https://nova-ops.test/api/shipments/reroute");
        novaReq.BearerToken.Should().BeNull("NovaLogistics utiliza HmacSha256, no Rs256Jwt");
        VerifyHmacSha256Signature(novaReq, NovaHmacSecret);
    }

    private static void VerifyHmacSha256Signature(CapturedWebhookRequest request, string secret)
    {
        request.Timestamp.Should().NotBeNullOrWhiteSpace();
        request.Signature.Should().NotBeNullOrWhiteSpace();

        var bodyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Body)));
        var canonical = $"{request.Timestamp}.{request.Method}.{request.Uri.PathAndQuery}.{bodyHash}";
        var expectedMac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical));
        var expectedSignature = $"v1={Convert.ToHexStringLower(expectedMac)}";

        request.Signature.Should().Be(expectedSignature);
    }

    private static void VerifyJwtToken(string? jwt, RSA rsaKey, string expectedAudience, Guid expectedTenantId)
    {
        jwt.Should().NotBeNullOrWhiteSpace();

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(jwt!, new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new RsaSecurityKey(rsaKey),
            ValidateIssuer = true,
            ValidIssuer = ServiceIssuer,
            ValidateAudience = true,
            ValidAudience = expectedAudience,
            ValidateLifetime = true
        }, out _);

        principal.FindFirst("role")?.Value.Should().Be("omro_service");
        principal.FindFirst("tid")?.Value.Should().Be(expectedTenantId.ToString());
    }

    private sealed record CapturedWebhookRequest(
        string Method,
        Uri Uri,
        string? BearerToken,
        string? Timestamp,
        string? Signature,
        string? TenantIdHeader,
        string? IdempotencyKey,
        string? OmroTaskIdHeader,
        string Body);

    private sealed class FakeAgnosticCognitiveOrchestrator : ICognitiveOrchestrator
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<(CognitivePlan Plan, int PromptTokens, int CompletionTokens)> ProposeResolutionAsync(
            AnomalyEvent anomaly,
            string tenantPromptContext,
            CancellationToken cancellationToken = default)
            => ProposeResolutionAsync(anomaly, tenantPromptContext, null, cancellationToken);

        public Task<(CognitivePlan Plan, int PromptTokens, int CompletionTokens)> ProposeResolutionAsync(
            AnomalyEvent anomaly,
            string tenantPromptContext,
            IReadOnlyDictionary<string, string>? attributes,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);

            var proposedActions = anomaly.AnomalyType switch
            {
                "ShipmentDelayRisk" => new List<ProposedAction>
                {
                    new(
                        "REROUTE_SHIPMENT",
                        "https://evil.attacker.test/hijack",
                        new Dictionary<string, object>
                        {
                            ["dispatchNote"] = "Despachador notificado: ruta aérea alterna prioritaria vía FakeLLM.",
                            ["executionMode"] = "Tampered",
                            ["evilInjectedField"] = "pwned"
                        })
                },
                "ConfirmationDeadlineMissed" => new List<ProposedAction>
                {
                    new(
                        "GENERATE_OVERBOOK",
                        "https://evil.attacker.test/hijack",
                        new Dictionary<string, object>
                        {
                            ["reason"] = "Sobrecupo validado por FakeLLM.",
                            ["decisionMode"] = "Tampered",
                            ["evilInjectedField"] = "pwned"
                        })
                },
                _ => new List<ProposedAction>()
            };

            var plan = new CognitivePlan(
                Title: $"[{anomaly.AnomalyType}] Enriquecido por FakeLLM",
                Reasoning: $"Enriquecimiento agnóstico para anomalía {anomaly.AnomalyType} ({anomaly.ResourceId}).",
                ConfidenceScore: 0.95m,
                ProposedActions: proposedActions);

            return Task.FromResult((plan, 20, 35));
        }
    }

    private sealed class DelegateHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _fn;

        public DelegateHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fn)
            => _fn = fn;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _fn(request, cancellationToken);
    }

    private sealed class StubPublicDnsResolver : IDnsResolver
    {
        public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default)
            => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });
    }

    private sealed class AgnosticTestHost : IAsyncDisposable
    {
        public ServiceProvider Provider { get; }
        public ITestHarness Harness { get; }
        public ConcurrentQueue<CapturedWebhookRequest> CapturedRequests { get; }
        public FakeAgnosticCognitiveOrchestrator FakeLlm { get; }
        public RSA DefaultRsaKey { get; }

        private AgnosticTestHost(
            ServiceProvider provider,
            ITestHarness harness,
            ConcurrentQueue<CapturedWebhookRequest> capturedRequests,
            FakeAgnosticCognitiveOrchestrator fakeLlm,
            RSA defaultRsaKey)
        {
            Provider = provider;
            Harness = harness;
            CapturedRequests = capturedRequests;
            FakeLlm = fakeLlm;
            DefaultRsaKey = defaultRsaKey;
        }

        public static async Task<AgnosticTestHost> CreateAsync()
        {
            var captured = new ConcurrentQueue<CapturedWebhookRequest>();
            var handler = new DelegateHttpMessageHandler(async (req, ct) =>
            {
                req.Headers.TryGetValues("X-Omro-Timestamp", out var tsVals);
                req.Headers.TryGetValues("X-Omro-Signature", out var sigVals);
                req.Headers.TryGetValues("X-Omro-Tenant-Id", out var tidVals);
                req.Headers.TryGetValues("Idempotency-Key", out var idemVals);
                req.Headers.TryGetValues("X-Omro-Task-Id", out var taskVals);
                var body = req.Content is not null ? await req.Content.ReadAsStringAsync(ct) : string.Empty;

                captured.Enqueue(new CapturedWebhookRequest(
                    Method: req.Method.Method,
                    Uri: req.RequestUri!,
                    BearerToken: req.Headers.Authorization?.Scheme == "Bearer" ? req.Headers.Authorization.Parameter : null,
                    Timestamp: tsVals?.FirstOrDefault(),
                    Signature: sigVals?.FirstOrDefault(),
                    TenantIdHeader: tidVals?.FirstOrDefault(),
                    IdempotencyKey: idemVals?.FirstOrDefault(),
                    OmroTaskIdHeader: taskVals?.FirstOrDefault(),
                    Body: body));

                return new HttpResponseMessage(HttpStatusCode.OK);
            });

            var rsa = RSA.Create(2048);
            var fakeLlm = new FakeAgnosticCognitiveOrchestrator();

            var services = new ServiceCollection();
            var dbName = $"OmroAgnosticProofDb_{Guid.NewGuid()}";
            var inMemoryProvider = new ServiceCollection().AddEntityFrameworkInMemoryDatabase().BuildServiceProvider();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"TenantSecrets:nova-hmac-secret"] = NovaHmacSecret
                })
                .Build();
            services.AddSingleton<IConfiguration>(configuration);

            services.AddDbContext<ApplicationDbContext>(o =>
                o.UseInMemoryDatabase(dbName).UseInternalServiceProvider(inMemoryProvider));
            services.AddMemoryCache();
            services.Configure<TenantCacheOptions>(_ => { });
            services.Configure<PlanValidationOptions>(_ => { });
            services.Configure<OmroServiceAuthOptions>(opt =>
            {
                opt.Issuer = ServiceIssuer;
                opt.Audience = "default-api";
                opt.Role = "omro_service";
                opt.PrivateKeyContent = rsa.ExportRSAPrivateKeyPem();
            });
            services.Configure<EgressSecurityOptions>(_ => { });

            services.AddScoped<IResolutionTaskRepository, ResolutionTaskRepository>();
            services.AddScoped<IActionExecutionRepository, ActionExecutionRepository>();
            services.AddScoped<ITenantConfigRepository, TenantConfigRepository>();
            services.AddScoped<IShadowDecisionRepository, ShadowDecisionRepository>();
            services.AddScoped<IActionExecutionCoordinator, ActionExecutionCoordinator>();
            services.AddScoped<IUnitOfWork, UnitOfWork>();
            services.AddScoped<IMessageInboxStore, EfMessageInboxStore>();
            services.AddSingleton<ITenantConfigProvider, CachedTenantConfigProvider>();
            services.AddScoped<ITenantSeedLoader, JsonTenantSeedLoader>();
            services.AddSingleton<IPolicyEngine, JsonDecisionTablePolicyEngine>();
            services.AddSingleton<IDeterministicPlanBuilder, DeterministicPlanBuilder>();
            services.AddSingleton<IPlanValidator, CatalogPlanValidator>();
            services.AddSingleton<IAiPlanContractParser, AiPlanContractParser>();
            services.AddSingleton<IActionIdempotencyKeyGenerator, ActionIdempotencyKeyGenerator>();
            services.AddSingleton<IOmroTokenService, OmroTokenService>();
            services.AddSingleton<ISecretResolver, ConfigurationSecretResolver>();
            services.AddSingleton<ITenantAuthSigner, TenantAuthSigner>();
            services.AddSingleton<IDnsResolver, StubPublicDnsResolver>();
            services.AddSingleton<ISsrfGuard, SsrfGuard>();
            services.AddSingleton<ICognitiveOrchestrator>(fakeLlm);

            services.AddResilientWebhookHttpClient(
                opt =>
                {
                    opt.MaxRetryAttempts = 1;
                    opt.RetryDelayMs = 10;
                    opt.UseJitter = false;
                    opt.AttemptTimeoutSeconds = 2;
                    opt.SamplingDurationSeconds = 10;
                    opt.BreakDurationSeconds = 5;
                    opt.MinimumThroughput = 10;
                },
                builder => builder.ConfigurePrimaryHttpMessageHandler(() => handler));

            services.Configure<RabbitMqMessagingOptions>(o =>
            {
                o.RedeliveryMode = "None";
                o.RetryLimit = 1;
                o.RetryMinIntervalMs = 10;
                o.RetryMaxIntervalMs = 20;
                o.RetryIntervalDeltaMs = 5;
            });

            services.AddSingleton<IOperationalAlertSink, LoggingOperationalAlertSink>();
            services.AddSingleton<DeadLetterAlertObserver>();

            services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<IngestAnomalyCommandHandler>());
            services.AddMassTransitTestHarness(x =>
            {
                x.SetTestTimeouts(testTimeout: TimeSpan.FromSeconds(15), testInactivityTimeout: TimeSpan.FromSeconds(2));
                x.SetKebabCaseEndpointNameFormatter();
                x.AddConsumer<AnomalyEventConsumer, AnomalyEventConsumerDefinition>();
                x.AddConsumer<ResolutionApprovedConsumer, ResolutionApprovedConsumerDefinition>();
                x.UsingInMemory((context, cfg) =>
                {
                    cfg.ConnectReceiveObserver(context.GetRequiredService<DeadLetterAlertObserver>());
                    cfg.ConfigureEndpoints(context);
                });
            });

            services.AddLogging();

            var provider = services.BuildServiceProvider();
            var harness = provider.GetRequiredService<ITestHarness>();
            await harness.Start();
            return new AgnosticTestHost(provider, harness, captured, fakeLlm, rsa);
        }

        public async Task SeedNovaLogisticsAsync(bool autonomousExecutionEnabled)
        {
            var seedPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "novalogistics.tenant.json");
            var json = await File.ReadAllTextAsync(seedPath);
            await SeedJsonAsync(NovaLogisticsTenantId, json, autonomousExecutionEnabled);
        }

        public async Task SeedMedCareAsync(bool autonomousExecutionEnabled)
        {
            var seedPath = Path.Combine(AppContext.BaseDirectory, "Seeds", "medcare.tenant.json");
            var json = (await File.ReadAllTextAsync(seedPath))
                .Replace("${OMRO_SEED_MEDCARE_BASE_URL:-https://medcare-cqrs-clinical-api.onrender.com}", "https://medcare.test", StringComparison.Ordinal);
            await SeedJsonAsync(MedCareTenantId, json, autonomousExecutionEnabled);
        }

        private async Task SeedJsonAsync(Guid tenantId, string json, bool autonomousExecutionEnabled)
        {
            using var scope = Provider.CreateScope();
            var loader = scope.ServiceProvider.GetRequiredService<ITenantSeedLoader>();
            await loader.SeedFromJsonAsync(json);

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var entity = await db.TenantConfigs.FirstAsync(t => t.TenantId == tenantId);
            if (entity.AutonomousExecutionEnabled != autonomousExecutionEnabled)
            {
                entity.SetAutonomousExecution(autonomousExecutionEnabled);
                await db.SaveChangesAsync();
            }

            Provider.GetRequiredService<ITenantConfigProvider>().Invalidate(tenantId);
        }

        public async Task<ResolutionTask?> WaitForTaskAsync(Func<ResolutionTask, bool> predicate, int timeoutSeconds = 10)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                using var scope = Provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var tasks = await db.ResolutionTasks
                    .Include(t => t.ActionExecutions)
                    .Include(t => t.ReasoningTraces)
                    .AsNoTracking()
                    .ToListAsync();

                var match = tasks.FirstOrDefault(predicate);
                if (match is not null)
                    return match;

                await Task.Delay(30);
            }

            return null;
        }

        public async ValueTask DisposeAsync()
        {
            await Harness.Stop();
            await Provider.DisposeAsync();
            DefaultRsaKey.Dispose();
        }
    }
}
