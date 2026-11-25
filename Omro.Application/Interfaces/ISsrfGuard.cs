using System.Net;

namespace Omro.Application.Interfaces;

public sealed record SsrfValidationResult(
    bool IsAllowed,
    IPAddress[] ResolvedAddresses,
    string? BlockReason);

public interface IDnsResolver
{
    Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default);
}

public interface ISsrfGuard
{
    Task<SsrfValidationResult> ValidateUriAsync(Uri uri, CancellationToken cancellationToken = default);
    Task<SsrfValidationResult> ValidateHostAsync(string host, CancellationToken cancellationToken = default);
}
