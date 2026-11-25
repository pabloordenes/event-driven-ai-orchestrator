using System.Net;
using Omro.Application.Interfaces;

namespace Omro.Infrastructure.Security;

public sealed class SystemDnsResolver : IDnsResolver
{
    public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default)
        => Dns.GetHostAddressesAsync(host, cancellationToken);
}
