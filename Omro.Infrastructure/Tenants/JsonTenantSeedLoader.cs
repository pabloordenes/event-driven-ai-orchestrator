using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Omro.Application.Interfaces;
using Omro.Domain.Repositories;
using Omro.Domain.Tenants;

namespace Omro.Infrastructure.Tenants;

public class JsonTenantSeedLoader : ITenantSeedLoader
{
    private static readonly Regex PlaceholderRegex = new(
        @"\$\{([A-Za-z0-9_]+)(?::-([^}]*))?\}",
        RegexOptions.Compiled);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ITenantConfigRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantConfigProvider _tenantConfigProvider;
    private readonly ILogger<JsonTenantSeedLoader> _logger;

    public JsonTenantSeedLoader(
        ITenantConfigRepository repository,
        IUnitOfWork unitOfWork,
        ITenantConfigProvider tenantConfigProvider,
        ILogger<JsonTenantSeedLoader> logger)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _tenantConfigProvider = tenantConfigProvider;
        _logger = logger;
    }

    public async Task<int> SeedFromDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            _logger.LogInformation("Directorio de seeds de tenants no encontrado: '{DirectoryPath}'. Se omite seed.", directoryPath);
            return 0;
        }

        var files = Directory.EnumerateFiles(directoryPath, "*.tenant.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int insertedCount = 0;
        foreach (var file in files)
        {
            var rawJson = await File.ReadAllTextAsync(file, cancellationToken);
            if (await SeedFromJsonAsync(rawJson, cancellationToken))
            {
                insertedCount++;
                _logger.LogInformation("Seed de tenant aplicado desde '{SeedFile}'.", Path.GetFileName(file));
            }
            else
            {
                _logger.LogDebug("Seed de tenant omitido (ya existente) para '{SeedFile}'.", Path.GetFileName(file));
            }
        }

        return insertedCount;
    }

    public async Task<bool> SeedFromJsonAsync(string jsonContent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jsonContent))
            throw new ArgumentException("El contenido JSON del seed no puede estar vacío.", nameof(jsonContent));

        var resolvedJson = ResolveEnvironmentPlaceholders(jsonContent);
        var document = JsonSerializer.Deserialize<TenantSeedDocument>(resolvedJson, SerializerOptions)
                       ?? throw new InvalidOperationException("No se pudo deserializar el documento seed de tenant.");

        if (document.TenantId == Guid.Empty)
            throw new InvalidOperationException("El seed de tenant debe especificar un 'tenantId' válido.");

        if (await _repository.ExistsAsync(document.TenantId, cancellationToken))
            return false;

        var auth = document.Auth ?? new TenantSeedAuthDto();
        var authScheme = Enum.TryParse<TenantAuthScheme>(auth.Scheme, ignoreCase: true, out var parsedScheme)
            ? parsedScheme
            : throw new InvalidOperationException($"AuthScheme '{auth.Scheme}' no soportado en seed de tenant '{document.TenantSlug}'.");

        var featureFlagsJson = SerializeOptionalElement(document.FeatureFlags, "{}");
        var allowedHostsJson = JsonSerializer.Serialize(document.AllowedEndpointHosts ?? []);
        var llmConfigJson = SerializeOptionalElement(document.LlmConfig, "{}");

        var tenantConfig = TenantConfig.Create(
            tenantId: document.TenantId,
            tenantSlug: document.TenantSlug,
            displayName: document.DisplayName,
            basePromptContext: document.BasePromptContext,
            authAudience: auth.Audience,
            isActive: document.IsActive,
            autonomousExecutionEnabled: document.AutonomousExecutionEnabled,
            featureFlagsJson: featureFlagsJson,
            hitlProposalTtlMinutes: document.HitlProposalTtlMinutes,
            hitlProposalMinLeadMinutes: document.HitlProposalMinLeadMinutes,
            dedupWindowMinutes: document.DedupWindowMinutes,
            allowedEndpointHostsJson: allowedHostsJson,
            authScheme: authScheme,
            authIssuer: auth.Issuer,
            authRole: auth.Role,
            signingKeyRef: auth.SigningKeyRef,
            signingKeyKid: auth.SigningKeyKid,
            hmacSecretRef: auth.HmacSecretRef,
            maxPayloadBytes: document.MaxPayloadBytes,
            egressTimeoutSeconds: document.EgressTimeoutSeconds,
            llmConfigJson: llmConfigJson,
            configVersion: document.ConfigVersion);

        if (document.Actions is not null)
        {
            foreach (var actionDto in document.Actions)
            {
                var phases = Enum.TryParse<TenantActionPhase>(actionDto.AllowedPhases, ignoreCase: true, out var parsedPhases)
                    ? parsedPhases
                    : throw new InvalidOperationException($"AllowedPhases '{actionDto.AllowedPhases}' inválido en acción '{actionDto.ActionType}'.");

                var action = TenantAction.Create(
                    tenantId: document.TenantId,
                    actionType: actionDto.ActionType,
                    description: actionDto.Description,
                    endpointUrl: actionDto.EndpointUrl,
                    httpMethod: actionDto.HttpMethod,
                    payloadSchemaJson: SerializeRequiredElement(actionDto.PayloadSchema, "payloadSchema"),
                    defaultPayloadTemplateJson: SerializeRequiredElement(actionDto.DefaultPayloadTemplate, "defaultPayloadTemplate"),
                    llmEditableFieldsJson: JsonSerializer.Serialize(actionDto.LlmEditableFields ?? []),
                    requiresApproval: actionDto.RequiresApproval,
                    allowedPhases: phases,
                    retryPolicyJson: SerializeOptionalElement(actionDto.RetryPolicy, "{}"),
                    isActive: actionDto.IsActive,
                    schemaVersion: actionDto.SchemaVersion);

                tenantConfig.AddAction(action);
            }
        }

        if (document.Policies is not null)
        {
            foreach (var policyDto in document.Policies)
            {
                var policy = TenantPolicy.Create(
                    tenantId: document.TenantId,
                    anomalyType: policyDto.AnomalyType,
                    decisionTableJson: SerializeRequiredElement(policyDto.DecisionTable, "decisionTable"),
                    policyVersion: policyDto.PolicyVersion,
                    isActive: policyDto.IsActive,
                    dedupWindowMinutes: policyDto.DedupWindowMinutes);

                tenantConfig.AddPolicy(policy);
            }
        }

        _repository.Add(tenantConfig);

        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            if (await _repository.ExistsAsync(document.TenantId, cancellationToken))
                return false;

            throw;
        }

        _tenantConfigProvider.Invalidate(document.TenantId);
        return true;
    }

    private static string ResolveEnvironmentPlaceholders(string input)
    {
        return PlaceholderRegex.Replace(input, match =>
        {
            var varName = match.Groups[1].Value;
            var defaultVal = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;
            var envVal = Environment.GetEnvironmentVariable(varName);
            var resolved = !string.IsNullOrWhiteSpace(envVal) ? envVal.Trim() : defaultVal;
            return EscapeForJsonString(resolved);
        });
    }

    private static string EscapeForJsonString(string value)
    {
        var encoded = JsonSerializer.Serialize(value);
        return encoded.Length >= 2 ? encoded[1..^1] : string.Empty;
    }

    private static string SerializeOptionalElement(JsonElement? element, string fallbackJson)
    {
        if (!element.HasValue || element.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return fallbackJson;

        return element.Value.GetRawText();
    }

    private static string SerializeRequiredElement(JsonElement element, string fieldName)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new InvalidOperationException($"El campo '{fieldName}' es obligatorio en el seed de tenant.");

        return element.GetRawText();
    }

    private sealed class TenantSeedDocument
    {
        public Guid TenantId { get; init; }
        public string TenantSlug { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public bool IsActive { get; init; } = true;
        public string BasePromptContext { get; init; } = string.Empty;
        public bool AutonomousExecutionEnabled { get; init; }
        public JsonElement? FeatureFlags { get; init; }
        public int HitlProposalTtlMinutes { get; init; } = 180;
        public int HitlProposalMinLeadMinutes { get; init; } = 60;
        public int DedupWindowMinutes { get; init; } = 60;
        public List<string>? AllowedEndpointHosts { get; init; }
        public TenantSeedAuthDto? Auth { get; init; }
        public int MaxPayloadBytes { get; init; } = 65536;
        public int EgressTimeoutSeconds { get; init; } = 15;
        public JsonElement? LlmConfig { get; init; }
        public int ConfigVersion { get; init; } = 1;
        public List<TenantSeedActionDto>? Actions { get; init; }
        public List<TenantSeedPolicyDto>? Policies { get; init; }
    }

    private sealed class TenantSeedAuthDto
    {
        public string Scheme { get; init; } = "Rs256Jwt";
        public string? Issuer { get; init; }
        public string Audience { get; init; } = string.Empty;
        public string Role { get; init; } = "omro_service";
        public string? SigningKeyRef { get; init; }
        public string? SigningKeyKid { get; init; }
        public string? HmacSecretRef { get; init; }
    }

    private sealed class TenantSeedActionDto
    {
        public string ActionType { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string EndpointUrl { get; init; } = string.Empty;
        public string HttpMethod { get; init; } = "POST";
        public JsonElement PayloadSchema { get; init; }
        public JsonElement DefaultPayloadTemplate { get; init; }
        public List<string>? LlmEditableFields { get; init; }
        public bool RequiresApproval { get; init; }
        public string AllowedPhases { get; init; } = "Both";
        public JsonElement? RetryPolicy { get; init; }
        public bool IsActive { get; init; } = true;
        public int SchemaVersion { get; init; } = 1;
    }

    private sealed class TenantSeedPolicyDto
    {
        public string AnomalyType { get; init; } = string.Empty;
        public int PolicyVersion { get; init; } = 1;
        public bool IsActive { get; init; } = true;
        public int? DedupWindowMinutes { get; init; }
        public JsonElement DecisionTable { get; init; }
    }
}
