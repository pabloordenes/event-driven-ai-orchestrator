using System.Text.Json;

namespace Omro.Domain.Tenants;

internal static class TenantJsonValidator
{
    public static string EnsureValidJson(string? json, string paramName, JsonValueKind? expectedKind = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException($"El parámetro '{paramName}' debe contener un JSON válido.", paramName);

        var trimmed = json.Trim();
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (expectedKind.HasValue && doc.RootElement.ValueKind != expectedKind.Value)
            {
                throw new ArgumentException(
                    $"El parámetro '{paramName}' debe ser un JSON de tipo {expectedKind.Value}.",
                    paramName);
            }

            return trimmed;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"El parámetro '{paramName}' no contiene un JSON válido: {ex.Message}", paramName, ex);
        }
    }
}
