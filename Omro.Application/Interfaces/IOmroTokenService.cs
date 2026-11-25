namespace Omro.Application.Interfaces;

public interface IOmroTokenService
{
    string GenerateToken();

    string GenerateToken(
        string issuer,
        string audience,
        string role,
        Guid? tenantId = null,
        string? keyId = null,
        DateTimeOffset? utcNow = null);
}
