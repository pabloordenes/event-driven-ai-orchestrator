using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Omro.Application.Interfaces;
using Omro.Application.Policies;
using Omro.Application.ResolutionTasks.Commands.IngestAnomaly;
using Omro.Application.ResolutionTasks.Services;
using Omro.Domain.Repositories;
using Omro.Domain.ResolutionTasks;
using Omro.Domain.Tenants;
using Omro.Infrastructure.Configuration;
using Omro.Infrastructure.Persistence;
using Omro.Infrastructure.Persistence.Repositories;
using Omro.Infrastructure.Tenants;

namespace Omro.IntegrationTests;

public class TenantConfigProviderTests
{
    private static readonly Guid MedCareTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task JsonTenantSeedLoader_ShouldLoadMedCareSeedWithEnvOverride_AndBeIdempotent()
    {
        var envVarName = "OMRO_SEED_MEDCARE_BASE_URL";
        var previousEnv = Environment.GetEnvironmentVariable(envVarName);
        Environment.SetEnvironmentVariable(envVarName, "https://staging.medcare.local");

        try
        {
            await using var provider = CreateInMemoryProvider();
            var seedPath = Path.Combine(AppContext.BaseDirectory, "Seeds", "medcare.tenant.json");
            File.Exists(seedPath).Should().BeTrue($"el archivo seed debe copiarse al directorio de salida: {seedPath}");

            var rawJson = await File.ReadAllTextAsync(seedPath);

            using (var scope = provider.CreateScope())
            {
                var loader = scope.ServiceProvider.GetRequiredService<ITenantSeedLoader>();
                var insertedFirst = await loader.SeedFromJsonAsync(rawJson);
                insertedFirst.Should().BeTrue();
            }

            var configProvider = provider.GetRequiredService<ITenantConfigProvider>();
            var snapshot = await configProvider.GetTenantConfigurationAsync(MedCareTenantId);

            snapshot.Should().NotBeNull();
            snapshot!.TenantSlug.Should().Be("medcare-cl");
            snapshot.AutonomousExecutionEnabled.Should().BeFalse();
            snapshot.HitlProposalTtlMinutes.Should().Be(180);
            snapshot.HitlProposalMinLeadMinutes.Should().Be(60);
            snapshot.AllowedOrigins.Should().Contain("https://staging.medcare.local");
            snapshot.Actions.Keys.Should().BeEquivalentTo("GENERATE_OVERBOOK", "PROPOSE_RESOLUTION", "MONITOR_RESOURCE");
            snapshot.Actions["GENERATE_OVERBOOK"].EndpointUrl.Should().Be("https://staging.medcare.local/api/appointments/overbook");
            snapshot.Actions["PROPOSE_RESOLUTION"].RequiresApproval.Should().BeTrue();
            snapshot.Actions["PROPOSE_RESOLUTION"].AllowedPhases.Should().Be(TenantActionPhase.Immediate);
            snapshot.Policies.Keys.Should().BeEquivalentTo("ConfirmationDeadlineMissed", "RiskEscalated", "AppointmentNoShowRisk");

            // Simular cambio en runtime y verificar que un segundo seed NO lo sobrescribe
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var entity = await db.TenantConfigs.FirstAsync(t => t.TenantId == MedCareTenantId);
                entity.SetAutonomousExecution(true);
                await db.SaveChangesAsync();

                var loader = scope.ServiceProvider.GetRequiredService<ITenantSeedLoader>();
                var insertedSecond = await loader.SeedFromJsonAsync(rawJson);
                insertedSecond.Should().BeFalse();
            }

            configProvider.Invalidate(MedCareTenantId);
            var reloaded = await configProvider.GetTenantConfigurationAsync(MedCareTenantId);
            reloaded!.AutonomousExecutionEnabled.Should().BeTrue();
            reloaded.ConfigVersion.Should().Be(2);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVarName, previousEnv);
        }
    }

    [Fact]
    public async Task CachedTenantConfigProvider_ShouldCacheAndInvalidateOnDemand()
    {
        await using var provider = CreateInMemoryProvider();
        var tenantId = Guid.NewGuid();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.TenantConfigs.Add(TenantConfig.Create(
                tenantId,
                "nova-ops",
                "Nova Logistics",
                "Contexto v1",
                "nova-api"));
            await db.SaveChangesAsync();
        }

        var configProvider = provider.GetRequiredService<ITenantConfigProvider>();

        var missing = await configProvider.GetTenantConfigurationAsync(Guid.NewGuid());
        missing.Should().BeNull();

        var firstLoad = await configProvider.GetTenantConfigurationAsync(tenantId);
        firstLoad.Should().NotBeNull();
        firstLoad!.AutonomousExecutionEnabled.Should().BeFalse();

        // Actualizar directamente en BD sin invalidar caché
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var entity = await db.TenantConfigs.FirstAsync(t => t.TenantId == tenantId);
            entity.SetAutonomousExecution(true);
            await db.SaveChangesAsync();
        }

        // Debe seguir sirviendo el snapshot cacheado
        var cachedHit = await configProvider.GetTenantConfigurationAsync(tenantId);
        cachedHit!.AutonomousExecutionEnabled.Should().BeFalse();

        // Invalidar caché y confirmar recarga desde BD
        configProvider.Invalidate(tenantId);
        var refreshed = await configProvider.GetTenantConfigurationAsync(tenantId);
        refreshed!.AutonomousExecutionEnabled.Should().BeTrue();
        refreshed.ConfigVersion.Should().Be(2);
    }

    [Fact]
    public async Task IngestAnomaly_WhenTenantMissingOrInactive_ShouldEscalateToHumanReviewWithoutCallingLlmOrWebhook()
    {
        await using var provider = CreateInMemoryProvider();
        var inactiveTenantId = Guid.NewGuid();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.TenantConfigs.Add(TenantConfig.Create(
                inactiveTenantId,
                "disabled-tenant",
                "Disabled Tenant",
                "Prompt",
                "disabled-api",
                isActive: false));
            await db.SaveChangesAsync();
        }

        var orchestratorMock = new Mock<ICognitiveOrchestrator>(MockBehavior.Strict);
        var webhookMock = new Mock<IWebhookDispatcher>(MockBehavior.Strict);
        var configProvider = provider.GetRequiredService<ITenantConfigProvider>();

        using var execScope = provider.CreateScope();
        var repo = execScope.ServiceProvider.GetRequiredService<IResolutionTaskRepository>();
        var uow = execScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var shadowRepo = execScope.ServiceProvider.GetRequiredService<IShadowDecisionRepository>();
        var policyEngine = execScope.ServiceProvider.GetRequiredService<IPolicyEngine>();
        var planBuilder = execScope.ServiceProvider.GetRequiredService<IDeterministicPlanBuilder>();
        var coordinator = new ActionExecutionCoordinator(webhookMock.Object, uow, NullLogger<ActionExecutionCoordinator>.Instance);
        var handler = new IngestAnomalyCommandHandler(
            repo,
            uow,
            orchestratorMock.Object,
            coordinator,
            configProvider,
            policyEngine,
            planBuilder,
            shadowRepo,
            NullLogger<IngestAnomalyCommandHandler>.Instance);

        var unknownTaskId = await handler.Handle(
            new IngestAnomalyCommand(Guid.NewGuid(), 0.95m, Guid.NewGuid(), "DelayRisk", Guid.NewGuid()),
            CancellationToken.None);

        var inactiveTaskId = await handler.Handle(
            new IngestAnomalyCommand(inactiveTenantId, 0.95m, Guid.NewGuid(), "DelayRisk", Guid.NewGuid()),
            CancellationToken.None);

        var dbRead = execScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var unknownTask = await dbRead.ResolutionTasks.FirstAsync(t => t.Id == unknownTaskId);
        var inactiveTask = await dbRead.ResolutionTasks.FirstAsync(t => t.Id == inactiveTaskId);

        unknownTask.Status.Should().Be(ResolutionStatus.NeedsHumanReview);
        inactiveTask.Status.Should().Be(ResolutionStatus.NeedsHumanReview);
        orchestratorMock.VerifyNoOtherCalls();
        webhookMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task IngestAnomaly_ShouldPassTenantPromptContextFromProviderToOrchestrator()
    {
        await using var provider = CreateInMemoryProvider();
        var tenantId = Guid.NewGuid();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var tenant = TenantConfig.Create(
                tenantId,
                "custom-tenant",
                "Custom Tenant",
                "CUSTOM_DB_PROMPT_MARKER",
                "custom-api");
            tenant.AddAction(TenantAction.Create(
                tenantId,
                "NOTIFY_OPS",
                "Notify operations",
                "https://custom.test/api/notify",
                defaultPayloadTemplateJson: "{\"resourceId\":\"{{resourceId}}\"}"));
            tenant.AddPolicy(TenantPolicy.Create(
                tenantId,
                "CustomAnomaly",
                """
                {
                  "hitPolicy": "FIRST_MATCH",
                  "rules": [
                    {
                      "ruleId": "notify",
                      "priority": 10,
                      "when": { "field": "riskProbability", "op": "gte", "value": 0.50 },
                      "then": {
                        "outcome": "ExecuteActions",
                        "invokeLlmEnrichment": true,
                        "actions": [{ "actionType": "NOTIFY_OPS" }]
                      }
                    }
                  ]
                }
                """));
            db.TenantConfigs.Add(tenant);
            await db.SaveChangesAsync();
        }

        string? capturedPrompt = null;
        var orchestratorMock = new Mock<ICognitiveOrchestrator>();
        orchestratorMock
            .Setup(o => o.ProposeResolutionAsync(
                It.IsAny<AnomalyEvent>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<AnomalyEvent, string, CancellationToken>((_, ctx, _) => capturedPrompt = ctx)
            .ReturnsAsync((new CognitivePlan("Plan", "Reason", 0.9m, [], IsFallback: false), 5, 5));

        var webhookMock = new Mock<IWebhookDispatcher>();
        webhookMock
            .Setup(w => w.DispatchAsync(
                "https://custom.test/api/notify",
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>(),
                It.IsAny<Guid?>()))
            .ReturnsAsync(WebhookDispatchResult.Succeeded(200, 1));
        var configProvider = provider.GetRequiredService<ITenantConfigProvider>();

        using var execScope = provider.CreateScope();
        var repo = execScope.ServiceProvider.GetRequiredService<IResolutionTaskRepository>();
        var uow = execScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var shadowRepo = execScope.ServiceProvider.GetRequiredService<IShadowDecisionRepository>();
        var policyEngine = execScope.ServiceProvider.GetRequiredService<IPolicyEngine>();
        var planBuilder = execScope.ServiceProvider.GetRequiredService<IDeterministicPlanBuilder>();
        var coordinator = new ActionExecutionCoordinator(webhookMock.Object, uow, NullLogger<ActionExecutionCoordinator>.Instance);
        var handler = new IngestAnomalyCommandHandler(
            repo,
            uow,
            orchestratorMock.Object,
            coordinator,
            configProvider,
            policyEngine,
            planBuilder,
            shadowRepo,
            NullLogger<IngestAnomalyCommandHandler>.Instance);

        await handler.Handle(
            new IngestAnomalyCommand(tenantId, 0.80m, Guid.NewGuid(), "CustomAnomaly", Guid.NewGuid()),
            CancellationToken.None);

        capturedPrompt.Should().NotBeNull();
        capturedPrompt.Should().Contain("CUSTOM_DB_PROMPT_MARKER");
        capturedPrompt.Should().Contain("NOTIFY_OPS");
        capturedPrompt.Should().Contain("https://custom.test/api/notify");
    }

    private static ServiceProvider CreateInMemoryProvider()
    {
        var services = new ServiceCollection();
        var dbName = $"OmroTenantCfg_{Guid.NewGuid()}";
        var inMemoryEfProvider = new ServiceCollection().AddEntityFrameworkInMemoryDatabase().BuildServiceProvider();

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase(dbName).UseInternalServiceProvider(inMemoryEfProvider));
        services.AddMemoryCache();
        services.Configure<TenantCacheOptions>(o =>
        {
            o.AbsoluteExpirationSeconds = 300;
            o.SlidingExpirationSeconds = 120;
            o.NegativeCacheSeconds = 30;
        });

        services.AddScoped<IResolutionTaskRepository, ResolutionTaskRepository>();
        services.AddScoped<IActionExecutionRepository, ActionExecutionRepository>();
        services.AddScoped<ITenantConfigRepository, TenantConfigRepository>();
        services.AddScoped<IShadowDecisionRepository, ShadowDecisionRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddSingleton<ITenantConfigProvider, CachedTenantConfigProvider>();
        services.AddScoped<ITenantSeedLoader, JsonTenantSeedLoader>();
        services.AddSingleton<IPolicyEngine, JsonDecisionTablePolicyEngine>();
        services.AddSingleton<IDeterministicPlanBuilder, DeterministicPlanBuilder>();
        services.AddLogging();

        return services.BuildServiceProvider();
    }
}
