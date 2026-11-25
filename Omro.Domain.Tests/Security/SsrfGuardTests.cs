using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Omro.Application.Interfaces;
using Omro.Infrastructure.Configuration;
using Omro.Infrastructure.Security;

namespace Omro.Domain.Tests.Security;

public class SsrfGuardTests
{
    [Theory]
    [InlineData("0.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("127.0.0.1")]
    [InlineData("127.10.20.30")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.0.0.1")]
    [InlineData("192.0.2.10")]
    [InlineData("192.168.1.50")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.254")]
    [InlineData("198.51.100.5")]
    [InlineData("203.0.113.9")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("fe80::1")]
    [InlineData("fec0::1")]
    [InlineData("ff02::1")]
    [InlineData("100::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("64:ff9b::10.0.0.5")]
    [InlineData("64:ff9b::169.254.169.254")]
    [InlineData("2002:7f00:0001::1")]
    [InlineData("2002:a9fe:a9fe::1")]
    public void IsBlockedIpAddress_ShouldBlockAllPrivateReservedMetadataAndMappedRanges(string ipString)
    {
        var ip = IPAddress.Parse(ipString);
        SsrfGuard.IsBlockedIpAddress(ip).Should().BeTrue($"IP '{ipString}' debe ser bloqueada");
    }

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("100.128.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("64:ff9b::93.184.216.34")]
    public void IsBlockedIpAddress_ShouldAllowPublicRoutableIps(string ipString)
    {
        var ip = IPAddress.Parse(ipString);
        SsrfGuard.IsBlockedIpAddress(ip).Should().BeFalse($"IP pública '{ipString}' debe permitirse");
    }

    [Theory]
    [InlineData("http://api.public.example/webhook")]
    [InlineData("https://user:pass@api.public.example/webhook")]
    [InlineData("https://api.public.example/webhook#frag")]
    [InlineData("https://localhost/webhook")]
    [InlineData("https://localhost./webhook")]
    [InlineData("https://api.localhost/webhook")]
    [InlineData("https://db.local/webhook")]
    [InlineData("https://metadata.google.internal/computeMetadata/v1/")]
    [InlineData("https://service.internal/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://127.0.0.1/admin")]
    [InlineData("https://[::ffff:127.0.0.1]/admin")]
    public async Task ValidateUriAsync_ShouldBlockInsecureSchemesCredentialsAndInternalHosts(string url)
    {
        var guard = new SsrfGuard(
            new MapDnsResolver(_ => [IPAddress.Parse("93.184.216.34")]),
            Options.Create(new EgressSecurityOptions()));

        var result = await guard.ValidateUriAsync(new Uri(url, UriKind.Absolute));

        result.IsAllowed.Should().BeFalse();
        result.BlockReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ValidateHostAsync_WhenDnsReturnsMixedPublicAndPrivateIps_ShouldRejectForAntiRebinding()
    {
        var guard = new SsrfGuard(
            new MapDnsResolver(_ => [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.5")]));

        var result = await guard.ValidateHostAsync("rebind.attacker.example");

        result.IsAllowed.Should().BeFalse();
        result.BlockReason.Should().Contain("10.0.0.5");
    }

    [Fact]
    public async Task ValidateHostAsync_WhenDnsFailsOrReturnsEmpty_ShouldFailClosed()
    {
        var failingGuard = new SsrfGuard(new ThrowingSocketDnsResolver());
        var failing = await failingGuard.ValidateHostAsync("missing.example");
        failing.IsAllowed.Should().BeFalse();

        var emptyGuard = new SsrfGuard(new MapDnsResolver(_ => []));
        var empty = await emptyGuard.ValidateHostAsync("empty.example");
        empty.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateUriAsync_WhenPublicHttpsResolvesToPublicIps_ShouldAllowAndReturnAddresses()
    {
        var expectedIp = IPAddress.Parse("93.184.216.34");
        var guard = new SsrfGuard(new MapDnsResolver(_ => [expectedIp]));

        var result = await guard.ValidateUriAsync(new Uri("https://tenant.example.com/api/hooks"));

        result.IsAllowed.Should().BeTrue();
        result.ResolvedAddresses.Should().ContainSingle().Which.Should().Be(expectedIp);
    }

    private sealed class MapDnsResolver : IDnsResolver
    {
        private readonly Func<string, IPAddress[]> _resolver;
        public MapDnsResolver(Func<string, IPAddress[]> resolver) => _resolver = resolver;
        public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default)
            => Task.FromResult(_resolver(host));
    }

    private sealed class ThrowingSocketDnsResolver : IDnsResolver
    {
        public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default)
            => throw new SocketException((int)SocketError.HostNotFound);
    }
}
