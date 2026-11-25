using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Omro.Application.Interfaces;
using Omro.Application.Tenants;
using Omro.Domain.Repositories;
using Omro.Domain.Tenants;
using Omro.Infrastructure.Configuration;

namespace Omro.Infrastructure.Tenants;

public sealed class CachedTenantConfigProvider : ITenantConfigProvider, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _memoryCache;
    private readonly TenantCacheOptions _options;
    private readonly ILogger<CachedTenantConfigProvider> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _singleFlightLocks = new();

    public CachedTenantConfigProvider(
        IServiceScopeFactory scopeFactory,
        IMemoryCache memoryCache,
        IOptions<TenantCacheOptions> options,
        ILogger<CachedTenantConfigProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _memoryCache = memoryCache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<TenantConfiguration?> GetTenantConfigurationAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
            return null;

        var cacheKey = BuildCacheKey(tenantId);
        if (_memoryCache.TryGetValue(cacheKey, out CacheEntry? cached) && cached is not null)
            return cached.Value;

        var gate = _singleFlightLocks.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_memoryCache.TryGetValue(cacheKey, out cached) && cached is not null)
                return cached.Value;

            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<ITenantConfigRepository>();
            var entity = await repository.GetWithCatalogAndActivePoliciesAsync(tenantId, cancellationToken);

            var snapshot = entity is null ? null : MapToSnapshot(entity);

            var entryOptions = new MemoryCacheEntryOptions();
            if (snapshot is null)
            {
                var negativeSeconds = Math.Max(1, _options.NegativeCacheSeconds);
                entryOptions.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(negativeSeconds);
                _logger.LogDebug("Tenant {TenantId} no encontrado en BD. Cache negativa por {Seconds}s.", tenantId, negativeSeconds);
            }
            else
            {
                var absoluteSeconds = Math.Max(1, _options.AbsoluteExpirationSeconds);
                var slidingSeconds = Math.Clamp(_options.SlidingExpirationSeconds, 1, absoluteSeconds);
                entryOptions.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(absoluteSeconds);
                entryOptions.SlidingExpiration = TimeSpan.FromSeconds(slidingSeconds);
            }

            _memoryCache.Set(cacheKey, new CacheEntry(snapshot), entryOptions);
            return snapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            return;

        _memoryCache.Remove(BuildCacheKey(tenantId));
    }

    public void Dispose()
    {
        foreach (var gate in _singleFlightLocks.Values)
        {
            gate.Dispose();
        }
        _singleFlightLocks.Clear();
    }

    private static string BuildCacheKey(Guid tenantId) => $"omro:tenant-config:{tenantId:N}";

    private static TenantConfiguration MapToSnapshot(TenantConfig entity)
    {
        var featureFlags = ParseFeatureFlags(entity.FeatureFlagsJson);
        var allowedOrigins = ParseAllowedOrigins(entity.AllowedEndpointHostsJson);
        var llmSettings = ParseLlmSettings(entity.LlmConfigJson);

        var actions = entity.Actions
            .Where(a => a.IsActive)
            .ToDictionary(
                a => a.ActionType,
                a => new TenantActionDescriptor(
                    Id: a.Id,
                    ActionType: a.ActionType,
                    Description: a.Description,
                    EndpointUrl: a.EndpointUrl,
                    HttpMethod: a.HttpMethod,
                    PayloadSchemaJson: a.PayloadSchemaJson,
                    DefaultPayloadTemplateJson: a.DefaultPayloadTemplateJson,
                    LlmEditableFields: ParseStringList(a.LlmEditableFieldsJson),
                    RequiresApproval: a.RequiresApproval,
                    AllowedPhases: a.AllowedPhases,
                    RetryPolicy: ParseRetryPolicy(a.RetryPolicyJson),
                    SchemaVersion: a.SchemaVersion),
                StringComparer.OrdinalIgnoreCase);

        var policies = entity.Policies
            .Where(p => p.IsActive)
            .GroupBy(p => p.AnomalyType, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(p => p.PolicyVersion).First())
            .ToDictionary(
                p => p.AnomalyType,
                p => new TenantPolicyDescriptor(
                    Id: p.Id,
                    AnomalyType: p.AnomalyType,
                    PolicyVersion: p.PolicyVersion,
                    DedupWindowMinutes: p.DedupWindowMinutes,
                    DecisionTableJson: p.DecisionTableJson),
                StringComparer.OrdinalIgnoreCase);

        var auth = new TenantAuthProfile(
            Scheme: entity.AuthScheme,
            Issuer: entity.AuthIssuer,
            Audience: entity.AuthAudience,
            Role: entity.AuthRole,
            SigningKeyRef: entity.SigningKeyRef,
            SigningKeyKid: entity.SigningKeyKid,
            HmacSecretRef: entity.HmacSecretRef);

        return new TenantConfiguration(
            TenantId: entity.TenantId,
            TenantSlug: entity.TenantSlug,
            DisplayName: entity.DisplayName,
            IsActive: entity.IsActive,
            BasePromptContext: entity.BasePromptContext,
            AutonomousExecutionEnabled: entity.AutonomousExecutionEnabled,
            FeatureFlags: featureFlags,
            HitlProposalTtlMinutes: entity.HitlProposalTtlMinutes,
            HitlProposalMinLeadMinutes: entity.HitlProposalMinLeadMinutes,
            DedupWindowMinutes: entity.DedupWindowMinutes,
            AllowedOrigins: allowedOrigins,
            Auth: auth,
            MaxPayloadBytes: entity.MaxPayloadBytes,
            EgressTimeoutSeconds: entity.EgressTimeoutSeconds,
            LlmSettings: llmSettings,
            ConfigVersion: entity.ConfigVersion,
            Actions: actions,
            Policies: policies);
    }

    private static IReadOnlyDictionary<string, JsonElement> ParseFeatureFlags(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.Clone();
        }

        return dict;
    }

    private static IReadOnlySet<string> ParseAllowedOrigins(string json)
    {
        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ParseStringList(json))
        {
            if (Uri.TryCreate(entry.Trim(), UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                origins.Add($"{uri.Scheme.ToLowerInvariant()}://{uri.Authority.ToLowerInvariant()}");
            }
        }

        return origins;
    }

    private static IReadOnlyList<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<string>();

        var list = JsonSerializer.Deserialize<List<string>>(json, JsonOptions);
        return list is null
            ? Array.Empty<string>()
            : list.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray();
    }

    private static TenantLlmSettings ParseLlmSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new TenantLlmSettings();

        return JsonSerializer.Deserialize<TenantLlmSettings>(json, JsonOptions) ?? new TenantLlmSettings();
    }

    private static ActionRetryPolicy ParseRetryPolicy(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new ActionRetryPolicy();

        return JsonSerializer.Deserialize<ActionRetryPolicy>(json, JsonOptions) ?? new ActionRetryPolicy();
    }

    private sealed record CacheEntry(TenantConfiguration? Value);
}
