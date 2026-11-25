using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Omro.Application.Interfaces;
using Omro.Infrastructure.Configuration;

namespace Omro.Infrastructure.Security;

public sealed class SsrfGuard : ISsrfGuard
{
    private static readonly string[] BlockedExactHosts =
    [
        "localhost",
        "metadata.google.internal",
        "metadata.goog",
        "instance-data"
    ];

    private static readonly string[] BlockedSuffixes =
    [
        ".localhost",
        ".local",
        ".internal"
    ];

    private readonly IDnsResolver _dnsResolver;
    private readonly EgressSecurityOptions _options;

    public SsrfGuard(
        IDnsResolver dnsResolver,
        IOptions<EgressSecurityOptions>? options = null)
    {
        _dnsResolver = dnsResolver;
        _options = options?.Value ?? new EgressSecurityOptions();
    }

    public Task<SsrfValidationResult> ValidateUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return Task.FromResult(Blocked("URI absoluta requerida para egreso SSRF-safe."));
        }

        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        if (!isHttps && !(_options.AllowInsecureHttp && isHttp))
        {
            return Task.FromResult(Blocked($"Esquema '{uri.Scheme}' bloqueado por seguridad de egreso."));
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return Task.FromResult(Blocked("URI con userinfo o fragment bloqueada."));
        }

        return ValidateHostAsync(uri.DnsSafeHost, cancellationToken);
    }

    public async Task<SsrfValidationResult> ValidateHostAsync(string host, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            return Blocked("Host vacío.");

        var normalized = NormalizeHost(host);
        if (string.IsNullOrEmpty(normalized) || normalized.Length > 253)
            return Blocked($"Host inválido: '{host}'.");

        if (IsBlockedHostname(normalized))
            return Blocked($"Hostname '{normalized}' bloqueado por política anti-SSRF.");

        if (IPAddress.TryParse(normalized, out var literalIp))
        {
            if (IsBlockedIpAddress(literalIp))
                return Blocked($"Dirección IP '{literalIp}' pertenece a un rango privado/reservado/metadata bloqueado.");

            return new SsrfValidationResult(true, [literalIp], null);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await _dnsResolver.GetHostAddressesAsync(normalized, cancellationToken);
        }
        catch (SocketException ex)
        {
            return Blocked($"No se pudo resolver DNS para '{normalized}': {ex.Message}");
        }

        if (addresses is null || addresses.Length == 0)
            return Blocked($"Resolución DNS vacía para '{normalized}'.");

        foreach (var resolvedIp in addresses)
        {
            if (IsBlockedIpAddress(resolvedIp))
            {
                return Blocked(
                    $"DNS rebinding/SSRF bloqueado: '{normalized}' resolvió hacia IP restringida '{resolvedIp}'.");
            }
        }

        return new SsrfValidationResult(true, addresses, null);
    }

    public static bool IsBlockedHostOrIp(Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
            return true;

        var normalized = NormalizeHost(uri.DnsSafeHost);
        if (string.IsNullOrEmpty(normalized))
            return true;

        if (IsBlockedHostname(normalized))
            return true;

        if (IPAddress.TryParse(normalized, out var literalIp))
            return IsBlockedIpAddress(literalIp);

        return false;
    }

    public static bool IsBlockedIpAddress(IPAddress ip)
    {
        if (ip is null)
            return true;

        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0) return true;                                  // 0.0.0.0/8 (This network)
            if (b[0] == 10) return true;                                 // 10.0.0.0/8 (RFC1918)
            if (b[0] == 100 && (b[1] & 0xC0) == 64) return true;         // 100.64.0.0/10 (CGNAT RFC6598)
            if (b[0] == 127) return true;                                // 127.0.0.0/8 (Loopback)
            if (b[0] == 169 && b[1] == 254) return true;                 // 169.254.0.0/16 (Link-local / Cloud IMDS)
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;    // 172.16.0.0/12 (RFC1918)
            if (b[0] == 192 && b[1] == 0 && b[2] == 0) return true;      // 192.0.0.0/24 (IETF protocol assignments)
            if (b[0] == 192 && b[1] == 0 && b[2] == 2) return true;      // 192.0.2.0/24 (TEST-NET-1)
            if (b[0] == 192 && b[1] == 168) return true;                 // 192.168.0.0/16 (RFC1918)
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return true;  // 198.18.0.0/15 (Benchmarking)
            if (b[0] == 198 && b[1] == 51 && b[2] == 100) return true;   // 198.51.100.0/24 (TEST-NET-2)
            if (b[0] == 203 && b[1] == 0 && b[2] == 113) return true;    // 203.0.113.0/24 (TEST-NET-3)
            if (b[0] >= 224) return true;                                // 224.0.0.0/4 (Multicast) & 240.0.0.0/4 (Reserved/Broadcast)
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IPv6None.Equals(ip) || IPAddress.IsLoopback(ip))
                return true;

            var b = ip.GetAddressBytes();

            var allZero = true;
            for (int i = 0; i < 16; i++)
            {
                if (b[i] != 0)
                {
                    allZero = false;
                    break;
                }
            }
            if (allZero) return true;                                    // ::/128
            if ((b[0] & 0xFE) == 0xFC) return true;                      // fc00::/7 (ULA)
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return true;      // fe80::/10 (Link-local)
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0xC0) return true;      // fec0::/10 (Deprecated site-local)
            if (b[0] == 0xFF) return true;                               // ff00::/8 (Multicast)

            // 100::/64 (Discard-Only)
            if (b[0] == 0x01 && b[1] == 0x00 &&
                b[2] == 0 && b[3] == 0 && b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0)
            {
                return true;
            }

            // 64:ff9b::/96 (NAT64 well-known prefix -> validar IPv4 embebida)
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B &&
                b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 &&
                b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0)
            {
                var embeddedV4 = new IPAddress(b[12..16]);
                return IsBlockedIpAddress(embeddedV4);
            }

            // 64:ff9b:1::/48 (Local-use NAT64)
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B &&
                b[4] == 0x00 && b[5] == 0x01)
            {
                return true;
            }

            // 2002::/16 (6to4 -> validar IPv4 embebida en bytes 2..6)
            if (b[0] == 0x20 && b[1] == 0x02)
            {
                var embeddedV4 = new IPAddress(b[2..6]);
                return IsBlockedIpAddress(embeddedV4);
            }

            // ::/96 IPv4-compatible IPv6
            var prefixZero = true;
            for (int i = 0; i < 12; i++)
            {
                if (b[i] != 0)
                {
                    prefixZero = false;
                    break;
                }
            }
            if (prefixZero)
            {
                var embeddedV4 = new IPAddress(b[12..16]);
                return IsBlockedIpAddress(embeddedV4);
            }

            return false;
        }

        return true;
    }

    private static string NormalizeHost(string rawHost)
    {
        var trimmed = rawHost.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Length > 2)
            trimmed = trimmed[1..^1];

        return trimmed.TrimEnd('.');
    }

    private static bool IsBlockedHostname(string host)
    {
        foreach (var blocked in BlockedExactHosts)
        {
            if (string.Equals(host, blocked, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var suffix in BlockedSuffixes)
        {
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static SsrfValidationResult Blocked(string reason) =>
        new(false, Array.Empty<IPAddress>(), reason);
}
