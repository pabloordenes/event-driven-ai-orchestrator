namespace Omro.Domain.Tenants;

public enum TenantAuthScheme
{
    Rs256Jwt,
    HmacSha256,
    Rs256JwtAndHmac
}
