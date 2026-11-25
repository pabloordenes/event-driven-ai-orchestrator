using System.Collections.Concurrent;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Omro.Application.Interfaces;
using Omro.Application.Tenants;
using Omro.Domain.Tenants;
using JwtRegisteredClaimNames = Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames;

namespace Omro.Infrastructure.Security;

public sealed class TenantAuthSigner : ITenantAuthSigner, IDisposable
{
    private readonly IOmroTokenService? _defaultTokenService;
    private readonly ISecretResolver _secretResolver;
    private readonly ConcurrentDictionary<string, CachedRsaSigner> _customRsaCache = new(StringComparer.Ordinal);
    private bool _disposed;

    public TenantAuthSigner(
        ISecretResolver secretResolver,
        IOmroTokenService? defaultTokenService = null)
    {
        _secretResolver = secretResolver;
        _defaultTokenService = defaultTokenService;
    }

    public TenantAuthHeaders Sign(TenantSigningContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tenant = context.Tenant;
        string? bearerToken = null;
        string? hmacSignature = null;
        string? hmacTimestamp = null;

        if (tenant.Auth.Scheme is TenantAuthScheme.Rs256Jwt or TenantAuthScheme.Rs256JwtAndHmac)
        {
            bearerToken = SignJwt(tenant, context.TimestampUtc);
        }

        if (tenant.Auth.Scheme is TenantAuthScheme.HmacSha256 or TenantAuthScheme.Rs256JwtAndHmac)
        {
            (hmacTimestamp, hmacSignature) = SignHmac(context);
        }

        return new TenantAuthHeaders(bearerToken, hmacSignature, hmacTimestamp, tenant.TenantId);
    }

    private string SignJwt(TenantConfiguration tenant, DateTimeOffset timestampUtc)
    {
        if (string.IsNullOrWhiteSpace(tenant.Auth.Audience))
        {
            throw new TenantAuthConfigurationException(
                $"Tenant '{tenant.TenantSlug}' ({tenant.TenantId}) usa autenticación JWT pero no tiene 'Auth.Audience' configurado.");
        }

        var issuer = string.IsNullOrWhiteSpace(tenant.Auth.Issuer) ? "omro-orchestrator" : tenant.Auth.Issuer.Trim();
        var audience = tenant.Auth.Audience.Trim();
        var role = string.IsNullOrWhiteSpace(tenant.Auth.Role) ? "omro_service" : tenant.Auth.Role.Trim();
        var keyRef = tenant.Auth.SigningKeyRef;
        var keyId = string.IsNullOrWhiteSpace(tenant.Auth.SigningKeyKid) ? null : tenant.Auth.SigningKeyKid.Trim();

        if (string.IsNullOrWhiteSpace(keyRef) ||
            string.Equals(keyRef.Trim(), "default", StringComparison.OrdinalIgnoreCase))
        {
            if (_defaultTokenService is null)
            {
                throw new TenantAuthConfigurationException(
                    $"Tenant '{tenant.TenantSlug}' ({tenant.TenantId}) requiere SigningKeyRef 'default' pero IOmroTokenService no está registrado.");
            }

            return _defaultTokenService.GenerateToken(
                issuer,
                audience,
                role,
                tenantId: tenant.TenantId,
                keyId: keyId,
                utcNow: timestampUtc);
        }

        var resolvedPem = _secretResolver.ResolveSecret(keyRef);
        if (string.IsNullOrWhiteSpace(resolvedPem))
        {
            throw new TenantAuthConfigurationException(
                $"No se encontró la clave RSA privada para SigningKeyRef '{keyRef}' del tenant '{tenant.TenantSlug}'.");
        }

        var normalizedPem = resolvedPem.Replace("\\n", "\n").Trim();
        var pemFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPem)));
        var cacheKey = $"{keyRef.Trim()}:{pemFingerprint}";

        var signer = _customRsaCache.GetOrAdd(cacheKey, _ => CreateCustomRsaSigner(normalizedPem));

        var claims = new List<Claim>
        {
            new("role", role),
            new("tid", tenant.TenantId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var credentials = signer.Credentials;
        if (keyId is not null)
        {
            var keyedSecurityKey = new RsaSecurityKey(signer.Rsa) { KeyId = keyId };
            credentials = new SigningCredentials(keyedSecurityKey, SecurityAlgorithms.RsaSha256);
        }

        var now = timestampUtc.UtcDateTime;
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = issuer,
            Audience = audience,
            NotBefore = now,
            IssuedAt = now,
            Expires = now.AddMinutes(5),
            SigningCredentials = credentials
        };

        var handler = new JwtSecurityTokenHandler();
        handler.OutboundClaimTypeMap.Clear();

        var token = handler.CreateToken(descriptor);
        return handler.WriteToken(token);
    }

    private (string Timestamp, string Signature) SignHmac(TenantSigningContext context)
    {
        var secretRef = context.Tenant.Auth.HmacSecretRef;
        var secret = _secretResolver.ResolveSecret(secretRef);
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new TenantAuthConfigurationException(
                $"Tenant '{context.Tenant.TenantSlug}' ({context.Tenant.TenantId}) requiere HmacSha256 pero no se pudo resolver HmacSecretRef '{secretRef}'.");
        }

        var unixTs = context.TimestampUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var method = string.IsNullOrWhiteSpace(context.HttpMethod)
            ? "POST"
            : context.HttpMethod.Trim().ToUpperInvariant();
        var bodyBytes = context.BodyBytes ?? Array.Empty<byte>();
        var bodyHash = Convert.ToHexStringLower(SHA256.HashData(bodyBytes));
        var canonical = $"{unixTs}.{method}.{context.RequestUri.PathAndQuery}.{bodyHash}";

        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var macBytes = HMACSHA256.HashData(secretBytes, Encoding.UTF8.GetBytes(canonical));
        var signature = $"v1={Convert.ToHexStringLower(macBytes)}";

        return (unixTs, signature);
    }

    private static CachedRsaSigner CreateCustomRsaSigner(string pem)
    {
        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem.ToCharArray());
            var key = new RsaSecurityKey(rsa);
            var creds = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
            return new CachedRsaSigner(rsa, creds);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            rsa.Dispose();
            throw new TenantAuthConfigurationException($"Clave PEM RSA inválida para firma del tenant: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var entry in _customRsaCache.Values)
        {
            entry.Rsa.Dispose();
        }
        _customRsaCache.Clear();
    }

    private sealed record CachedRsaSigner(RSA Rsa, SigningCredentials Credentials);
}
