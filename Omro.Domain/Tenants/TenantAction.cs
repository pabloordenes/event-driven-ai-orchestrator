using System.Text.Json;

namespace Omro.Domain.Tenants;

public class TenantAction
{
    public const int MaxActionTypeLength = 100;
    public const int MaxDescriptionLength = 500;
    public const int MaxEndpointUrlLength = 1000;
    public const int MaxHttpMethodLength = 10;

    private static readonly HashSet<string> AllowedHttpMethods = new(StringComparer.Ordinal)
    {
        "POST",
        "PUT",
        "PATCH"
    };

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string ActionType { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string EndpointUrl { get; private set; } = string.Empty;
    public string HttpMethod { get; private set; } = "POST";
    public string PayloadSchemaJson { get; private set; } = "{}";
    public string DefaultPayloadTemplateJson { get; private set; } = "{}";
    public string LlmEditableFieldsJson { get; private set; } = "[]";
    public bool RequiresApproval { get; private set; }
    public TenantActionPhase AllowedPhases { get; private set; } = TenantActionPhase.Both;
    public string RetryPolicyJson { get; private set; } = "{}";
    public bool IsActive { get; private set; } = true;
    public int SchemaVersion { get; private set; } = 1;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private TenantAction() { } // ef core

    public static TenantAction Create(
        Guid tenantId,
        string actionType,
        string description,
        string endpointUrl,
        string httpMethod = "POST",
        string payloadSchemaJson = "{}",
        string defaultPayloadTemplateJson = "{}",
        string llmEditableFieldsJson = "[]",
        bool requiresApproval = false,
        TenantActionPhase allowedPhases = TenantActionPhase.Both,
        string retryPolicyJson = "{}",
        bool isActive = true,
        int schemaVersion = 1,
        Guid? id = null,
        DateTimeOffset? utcNow = null)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("TenantId no puede estar vacío.", nameof(tenantId));

        if (string.IsNullOrWhiteSpace(actionType) || actionType.Trim().Length > MaxActionTypeLength)
            throw new ArgumentException($"ActionType es obligatorio y no puede exceder {MaxActionTypeLength} caracteres.", nameof(actionType));

        if (string.IsNullOrWhiteSpace(description) || description.Trim().Length > MaxDescriptionLength)
            throw new ArgumentException($"Description es obligatorio y no puede exceder {MaxDescriptionLength} caracteres.", nameof(description));

        if (string.IsNullOrWhiteSpace(endpointUrl) || endpointUrl.Trim().Length > MaxEndpointUrlLength)
            throw new ArgumentException($"EndpointUrl es obligatorio y no puede exceder {MaxEndpointUrlLength} caracteres.", nameof(endpointUrl));

        var trimmedEndpoint = endpointUrl.Trim();
        if (!Uri.TryCreate(trimmedEndpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"EndpointUrl '{endpointUrl}' debe ser una URI absoluta http/https válida.", nameof(endpointUrl));
        }

        var normalizedMethod = string.IsNullOrWhiteSpace(httpMethod)
            ? "POST"
            : httpMethod.Trim().ToUpperInvariant();
        if (normalizedMethod.Length > MaxHttpMethodLength || !AllowedHttpMethods.Contains(normalizedMethod))
        {
            throw new ArgumentException(
                $"HttpMethod '{httpMethod}' no está permitido. Métodos permitidos: POST, PUT, PATCH.",
                nameof(httpMethod));
        }

        if (schemaVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), "SchemaVersion debe ser mayor o igual a 1.");

        var validSchema = TenantJsonValidator.EnsureValidJson(payloadSchemaJson, nameof(payloadSchemaJson), JsonValueKind.Object);
        var validTemplate = TenantJsonValidator.EnsureValidJson(defaultPayloadTemplateJson, nameof(defaultPayloadTemplateJson), JsonValueKind.Object);
        var validEditableFields = TenantJsonValidator.EnsureValidJson(llmEditableFieldsJson, nameof(llmEditableFieldsJson), JsonValueKind.Array);
        var validRetryPolicy = TenantJsonValidator.EnsureValidJson(retryPolicyJson, nameof(retryPolicyJson), JsonValueKind.Object);

        var now = utcNow ?? DateTimeOffset.UtcNow;

        return new TenantAction
        {
            Id = id.HasValue && id.Value != Guid.Empty ? id.Value : Guid.NewGuid(),
            TenantId = tenantId,
            ActionType = actionType.Trim(),
            Description = description.Trim(),
            EndpointUrl = trimmedEndpoint,
            HttpMethod = normalizedMethod,
            PayloadSchemaJson = validSchema,
            DefaultPayloadTemplateJson = validTemplate,
            LlmEditableFieldsJson = validEditableFields,
            RequiresApproval = requiresApproval,
            AllowedPhases = allowedPhases,
            RetryPolicyJson = validRetryPolicy,
            IsActive = isActive,
            SchemaVersion = schemaVersion,
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
