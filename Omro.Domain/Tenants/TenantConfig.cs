using System.Text.Json;

namespace Omro.Domain.Tenants;

public class TenantConfig
{
    public const int MaxTenantSlugLength = 80;
    public const int MaxDisplayNameLength = 150;
    public const int MaxAuthAudienceLength = 150;
    public const int MaxAuthIssuerLength = 150;
    public const int MaxAuthRoleLength = 80;
    public const int MaxKeyRefLength = 200;
    public const int MaxKeyKidLength = 64;

    public Guid TenantId { get; private set; }
    public string TenantSlug { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public bool IsActive { get; private set; } = true;
    public string BasePromptContext { get; private set; } = string.Empty;
    public bool AutonomousExecutionEnabled { get; private set; }
    public string FeatureFlagsJson { get; private set; } = "{}";
    public int HitlProposalTtlMinutes { get; private set; } = 180;
    public int HitlProposalMinLeadMinutes { get; private set; } = 60;
    public int DedupWindowMinutes { get; private set; } = 60;
    public string AllowedEndpointHostsJson { get; private set; } = "[]";
    public TenantAuthScheme AuthScheme { get; private set; } = TenantAuthScheme.Rs256Jwt;
    public string? AuthIssuer { get; private set; }
    public string AuthAudience { get; private set; } = string.Empty;
    public string AuthRole { get; private set; } = "omro_service";
    public string? SigningKeyRef { get; private set; }
    public string? SigningKeyKid { get; private set; }
    public string? HmacSecretRef { get; private set; }
    public int MaxPayloadBytes { get; private set; } = 65536;
    public int EgressTimeoutSeconds { get; private set; } = 15;
    public string LlmConfigJson { get; private set; } = "{}";
    public int ConfigVersion { get; private set; } = 1;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private readonly List<TenantAction> _actions = new();
    public IReadOnlyList<TenantAction> Actions => _actions.AsReadOnly();

    private readonly List<TenantPolicy> _policies = new();
    public IReadOnlyList<TenantPolicy> Policies => _policies.AsReadOnly();

    private TenantConfig() { } // ef core

    public static TenantConfig Create(
        Guid tenantId,
        string tenantSlug,
        string displayName,
        string basePromptContext,
        string authAudience,
        bool isActive = true,
        bool autonomousExecutionEnabled = false,
        string featureFlagsJson = "{}",
        int hitlProposalTtlMinutes = 180,
        int hitlProposalMinLeadMinutes = 60,
        int dedupWindowMinutes = 60,
        string allowedEndpointHostsJson = "[]",
        TenantAuthScheme authScheme = TenantAuthScheme.Rs256Jwt,
        string? authIssuer = null,
        string authRole = "omro_service",
        string? signingKeyRef = null,
        string? signingKeyKid = null,
        string? hmacSecretRef = null,
        int maxPayloadBytes = 65536,
        int egressTimeoutSeconds = 15,
        string llmConfigJson = "{}",
        int configVersion = 1,
        DateTimeOffset? utcNow = null)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("TenantId no puede estar vacío.", nameof(tenantId));

        var slug = NormalizeRequired(tenantSlug, MaxTenantSlugLength, nameof(tenantSlug));
        var name = NormalizeRequired(displayName, MaxDisplayNameLength, nameof(displayName));
        if (string.IsNullOrWhiteSpace(basePromptContext))
            throw new ArgumentException("BasePromptContext es obligatorio.", nameof(basePromptContext));

        var audience = NormalizeRequired(authAudience, MaxAuthAudienceLength, nameof(authAudience));
        var role = string.IsNullOrWhiteSpace(authRole)
            ? "omro_service"
            : NormalizeRequired(authRole, MaxAuthRoleLength, nameof(authRole));

        if (hitlProposalTtlMinutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(hitlProposalTtlMinutes), "HitlProposalTtlMinutes debe ser mayor a 0.");

        if (hitlProposalMinLeadMinutes < 0)
            throw new ArgumentOutOfRangeException(nameof(hitlProposalMinLeadMinutes), "HitlProposalMinLeadMinutes no puede ser negativo.");

        if (dedupWindowMinutes < 0)
            throw new ArgumentOutOfRangeException(nameof(dedupWindowMinutes), "DedupWindowMinutes no puede ser negativo.");

        if (maxPayloadBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes), "MaxPayloadBytes debe ser mayor a 0.");

        if (egressTimeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(egressTimeoutSeconds), "EgressTimeoutSeconds debe ser mayor a 0.");

        if (configVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(configVersion), "ConfigVersion debe ser mayor o igual a 1.");

        var normalizedFeatureFlags = TenantJsonValidator.EnsureValidJson(featureFlagsJson, nameof(featureFlagsJson), JsonValueKind.Object);
        var normalizedHosts = TenantJsonValidator.EnsureValidJson(allowedEndpointHostsJson, nameof(allowedEndpointHostsJson), JsonValueKind.Array);
        var normalizedLlmConfig = TenantJsonValidator.EnsureValidJson(llmConfigJson, nameof(llmConfigJson), JsonValueKind.Object);

        var now = utcNow ?? DateTimeOffset.UtcNow;

        return new TenantConfig
        {
            TenantId = tenantId,
            TenantSlug = slug,
            DisplayName = name,
            IsActive = isActive,
            BasePromptContext = basePromptContext.Trim(),
            AutonomousExecutionEnabled = autonomousExecutionEnabled,
            FeatureFlagsJson = normalizedFeatureFlags,
            HitlProposalTtlMinutes = hitlProposalTtlMinutes,
            HitlProposalMinLeadMinutes = hitlProposalMinLeadMinutes,
            DedupWindowMinutes = dedupWindowMinutes,
            AllowedEndpointHostsJson = normalizedHosts,
            AuthScheme = authScheme,
            AuthIssuer = NormalizeOptional(authIssuer, MaxAuthIssuerLength, nameof(authIssuer)),
            AuthAudience = audience,
            AuthRole = role,
            SigningKeyRef = NormalizeOptional(signingKeyRef, MaxKeyRefLength, nameof(signingKeyRef)),
            SigningKeyKid = NormalizeOptional(signingKeyKid, MaxKeyKidLength, nameof(signingKeyKid)),
            HmacSecretRef = NormalizeOptional(hmacSecretRef, MaxKeyRefLength, nameof(hmacSecretRef)),
            MaxPayloadBytes = maxPayloadBytes,
            EgressTimeoutSeconds = egressTimeoutSeconds,
            LlmConfigJson = normalizedLlmConfig,
            ConfigVersion = configVersion,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public void AddAction(TenantAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (action.TenantId != TenantId)
            throw new InvalidOperationException($"La acción '{action.ActionType}' pertenece al tenant '{action.TenantId}', no a '{TenantId}'.");

        if (_actions.Any(a => string.Equals(a.ActionType, action.ActionType, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Ya existe una acción '{action.ActionType}' registrada para el tenant '{TenantId}'.");

        _actions.Add(action);
    }

    public void AddPolicy(TenantPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (policy.TenantId != TenantId)
            throw new InvalidOperationException($"La política '{policy.AnomalyType}' pertenece al tenant '{policy.TenantId}', no a '{TenantId}'.");

        if (_policies.Any(p =>
                string.Equals(p.AnomalyType, policy.AnomalyType, StringComparison.OrdinalIgnoreCase) &&
                p.PolicyVersion == policy.PolicyVersion))
        {
            throw new InvalidOperationException(
                $"Ya existe la versión {policy.PolicyVersion} de política para '{policy.AnomalyType}' en el tenant '{TenantId}'.");
        }

        if (policy.IsActive && _policies.Any(p =>
                p.IsActive && string.Equals(p.AnomalyType, policy.AnomalyType, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Ya existe una política activa para '{policy.AnomalyType}' en el tenant '{TenantId}'.");
        }

        _policies.Add(policy);
    }

    public void SetAutonomousExecution(bool enabled, DateTimeOffset? utcNow = null)
    {
        AutonomousExecutionEnabled = enabled;
        ConfigVersion++;
        UpdatedAt = utcNow ?? DateTimeOffset.UtcNow;
    }

    public void SetActive(bool isActive, DateTimeOffset? utcNow = null)
    {
        IsActive = isActive;
        ConfigVersion++;
        UpdatedAt = utcNow ?? DateTimeOffset.UtcNow;
    }

    private static string NormalizeRequired(string value, int maxLength, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"El campo '{paramName}' es obligatorio.", paramName);

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"El campo '{paramName}' no puede exceder {maxLength} caracteres.", paramName);

        return trimmed;
    }

    private static string? NormalizeOptional(string? value, int maxLength, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"El campo '{paramName}' no puede exceder {maxLength} caracteres.", paramName);

        return trimmed;
    }
}
