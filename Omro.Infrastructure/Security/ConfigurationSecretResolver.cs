using System.Text;
using Microsoft.Extensions.Configuration;
using Omro.Application.Interfaces;

namespace Omro.Infrastructure.Security;

public sealed class ConfigurationSecretResolver : ISecretResolver
{
    private readonly IConfiguration? _configuration;

    public ConfigurationSecretResolver(IConfiguration? configuration = null)
    {
        _configuration = configuration;
    }

    public string? ResolveSecret(string? secretRef)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
            return null;

        var trimmed = secretRef.Trim();
        if (string.Equals(trimmed, "default", StringComparison.OrdinalIgnoreCase))
            return null;

        if (trimmed.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var envName = trimmed[4..].Trim();
            if (string.IsNullOrWhiteSpace(envName))
                return null;

            var envValue = Environment.GetEnvironmentVariable(envName);
            return string.IsNullOrWhiteSpace(envValue) ? null : envValue;
        }

        if (trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var filePath = trimmed[5..].Trim();
            if (string.IsNullOrWhiteSpace(filePath) ||
                filePath.Contains("..", StringComparison.Ordinal))
            {
                return null;
            }

            if (!File.Exists(filePath))
                return null;

            var content = File.ReadAllText(filePath);
            return string.IsNullOrWhiteSpace(content) ? null : content;
        }

        var fromTenantSecrets = _configuration?[$"TenantSecrets:{trimmed}"];
        if (!string.IsNullOrWhiteSpace(fromTenantSecrets))
            return fromTenantSecrets;

        var fromServiceAuthKeys = _configuration?[$"ServiceAuth:Keys:{trimmed}"];
        if (!string.IsNullOrWhiteSpace(fromServiceAuthKeys))
            return fromServiceAuthKeys;

        var normalizedEnvSuffix = NormalizeEnvSuffix(trimmed);
        if (!string.IsNullOrEmpty(normalizedEnvSuffix))
        {
            var fromConventionEnv = Environment.GetEnvironmentVariable($"OMRO_SECRET_{normalizedEnvSuffix}");
            if (!string.IsNullOrWhiteSpace(fromConventionEnv))
                return fromConventionEnv;
        }

        return null;
    }

    private static string NormalizeEnvSuffix(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '_');
        }
        return sb.ToString().Trim('_');
    }
}
