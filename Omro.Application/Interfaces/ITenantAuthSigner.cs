using Omro.Application.Tenants;

namespace Omro.Application.Interfaces;

public sealed record TenantSigningContext(
    TenantConfiguration Tenant,
    string HttpMethod,
    Uri RequestUri,
    byte[] BodyBytes,
    DateTimeOffset TimestampUtc);

public sealed record TenantAuthHeaders(
    string? BearerToken,
    string? HmacSignature,
    string? HmacTimestamp,
    Guid TenantId);

public interface ITenantAuthSigner
{
    TenantAuthHeaders Sign(TenantSigningContext context);
}
