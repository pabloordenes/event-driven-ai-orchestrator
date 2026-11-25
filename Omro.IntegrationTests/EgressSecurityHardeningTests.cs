using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Omro.Application.Interfaces;
using Omro.Application.Tenants;
using Omro.Domain.Repositories;
using Omro.Domain.Tenants;
using Omro.Infrastructure.Configuration;
using Omro.Infrastructure.HttpClients;
using Omro.Infrastructure.Persistence;
using Omro.Infrastructure.Persistence.Repositories;
using Omro.Infrastructure.Security;
using Omro.Infrastructure.Tenants;

namespace Omro.IntegrationTests;

public class EgressSecurityHardeningTests
{
    [Theory]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://127.0.0.1/api/admin")]
    [InlineData("https://rebind.evil.test/api/steal")]
    public async Task DispatchAsync_WhenTargetIsSsrfOrDnsRebinding_ShouldFailPermanentlyWithoutNetworkOrBreakerTrip(
        string maliciousEndpoint)
    {
        int wireHits = 0;
        var handler = new DelegateHttpMessageHandler((req, _) =>
        {
            Interlocked.Increment(ref wireHits);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        await using var ctx = CreateHardenedHost(
            handler,
            host => string.Equals(host, "rebind.evil.test", StringComparison.OrdinalIgnoreCase)
                ? [IPAddress.Parse("10.0.0.5")]
                : [IPAddress.Parse("93.184.216.34")]);

        var dispatcher = ctx.Provider.GetRequiredService<IWebhookDispatcher>();

        // 1. Disparo bloqueado por SSRF -> PermanentFailure sin tocar red ni abrir Circuit Breaker
        for (int i = 0; i < 6; i++)
        {
            var blocked = await dispatcher.DispatchAsync(
                maliciousEndpoint,
                new Dictionary<string, object> { ["ok"] = true },
                CancellationToken.None,
                $"ssrf-{i}");

            blocked.IsSuccess.Should().BeFalse();
            blocked.IsPermanentFailure.Should().BeTrue();
            blocked.Attempts.Should().Be(1);
        }

        wireHits.Should().Be(0);

        // 2. Host legítimo debe despachar normalmente (breaker no afectado)
        var valid = await dispatcher.DispatchAsync(
            "https://valid.tenant.test/api/webhook",
            new Dictionary<string, object> { ["ok"] = true },
            CancellationToken.None,
            "valid-1");

        valid.IsSuccess.Should().BeTrue();
        wireHits.Should().Be(1);
    }

    [Fact]
    public async Task CreateSsrfProtectedSocketsHandler_ShouldBlockDnsRebindingInConnectCallbackAndDisableRedirects()
    {
        var guard = new SsrfGuard(new DelegateDnsResolver(host =>
            string.Equals(host, "rebind.connect.test", StringComparison.OrdinalIgnoreCase)
                ? [IPAddress.Parse("10.0.0.42")]
                : [IPAddress.Parse("93.184.216.34")]));

        using var socketsHandler = WebhookResilienceExtensions.CreateSsrfProtectedSocketsHandler(guard);
        socketsHandler.AllowAutoRedirect.Should().BeFalse();

        using var invoker = new HttpMessageInvoker(socketsHandler);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://rebind.connect.test/api/hook");

        Func<Task> act = () => invoker.SendAsync(req, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<Exception>();
        (ex.Which is SsrfBlockedException || ex.Which.GetBaseException() is SsrfBlockedException)
            .Should().BeTrue("ConnectCallback debe abortar conexiones hacia IPs privadas antes de abrir socket TCP");
    }

    [Fact]
    public async Task DispatchAsync_WhenServerReturns302RedirectToMetadata_ShouldRejectWithoutFollowing()
    {
        int wireHits = 0;
        var handler = new DelegateHttpMessageHandler((req, _) =>
        {
            Interlocked.Increment(ref wireHits);
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("http://169.254.169.254/latest/meta-data/");
            return Task.FromResult(redirect);
        });

        await using var ctx = CreateHardenedHost(handler, _ => [IPAddress.Parse("93.184.216.34")]);
        var dispatcher = ctx.Provider.GetRequiredService<IWebhookDispatcher>();

        var result = await dispatcher.DispatchAsync(
            "https://redirect.tenant.test/api/hook",
            new Dictionary<string, object> { ["ok"] = true },
            CancellationToken.None,
            "redir-1");

        result.IsSuccess.Should().BeFalse();
        result.IsPermanentFailure.Should().BeTrue();
        result.HttpStatusCode.Should().Be(302);
        result.ErrorMessage.Should().Contain("Redirect HTTP 302");
        wireHits.Should().Be(1);
    }

    [Fact]
    public async Task DispatchAsync_WhenPayloadExceedsTenantMaxBytes_ShouldRejectBeforeNetwork()
    {
        int wireHits = 0;
        var handler = new DelegateHttpMessageHandler((_, _) =>
        {
            Interlocked.Increment(ref wireHits);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        await using var ctx = CreateHardenedHost(handler, _ => [IPAddress.Parse("93.184.216.34")]);
        var tenantId = await ctx.SeedTenantAsync(
            "size-limit",
            "https://limited.tenant.test",
            new TenantAuthProfile(TenantAuthScheme.Rs256Jwt, "omro", "size-api", "omro_service", "default", null, null),
            maxPayloadBytes: 128);

        var dispatcher = ctx.Provider.GetRequiredService<IWebhookDispatcher>();
        var result = await dispatcher.DispatchAsync(
            "https://limited.tenant.test/api/hook",
            new Dictionary<string, object> { ["blob"] = new string('A', 300) },
            CancellationToken.None,
            "too-large-1",
            dispatchOptions: new WebhookDispatchOptions(TenantId: tenantId));

        result.IsSuccess.Should().BeFalse();
        result.IsPermanentFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("tamaño máximo");
        wireHits.Should().Be(0);
    }

    [Fact]
    public async Task DispatchAsync_WhenErrorResponseStreams5Megabytes_ShouldReadAtMost4KbWithoutOom()
    {
        var largeStream = new CountingPatternStream(5 * 1024 * 1024);
        var handler = new DelegateHttpMessageHandler((_, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = new StreamContent(largeStream)
            });
        });

        await using var ctx = CreateHardenedHost(handler, _ => [IPAddress.Parse("93.184.216.34")]);
        var dispatcher = ctx.Provider.GetRequiredService<IWebhookDispatcher>();

        var result = await dispatcher.DispatchAsync(
            "https://large-err.tenant.test/api/hook",
            new Dictionary<string, object> { ["ok"] = true },
            CancellationToken.None,
            "large-err-1");

        result.IsSuccess.Should().BeFalse();
        result.IsPermanentFailure.Should().BeTrue();
        result.HttpStatusCode.Should().Be(422);
        largeStream.BytesConsumed.Should().BeLessThanOrEqualTo(4096);
        result.ErrorMessage!.Length.Should().BeLessThan(400);
    }

    [Fact]
    public async Task DispatchAsync_WhenTenantTimeoutExpires_ShouldAbortAndReturnTimeout408()
    {
        var handler = new DelegateHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await using var ctx = CreateHardenedHost(handler, _ => [IPAddress.Parse("93.184.216.34")]);
        var tenantId = await ctx.SeedTenantAsync(
            "slow-tenant",
            "https://slow.tenant.test",
            new TenantAuthProfile(TenantAuthScheme.Rs256Jwt, "omro", "slow-api", "omro_service", "default", null, null),
            egressTimeoutSeconds: 1);

        var dispatcher = ctx.Provider.GetRequiredService<IWebhookDispatcher>();
        var started = DateTimeOffset.UtcNow;

        var result = await dispatcher.DispatchAsync(
            "https://slow.tenant.test/api/hook",
            new Dictionary<string, object> { ["ok"] = true },
            CancellationToken.None,
            "slow-1",
            dispatchOptions: new WebhookDispatchOptions(TenantId: tenantId));

        var elapsed = DateTimeOffset.UtcNow - started;
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        result.IsSuccess.Should().BeFalse();
        result.IsPermanentFailure.Should().BeFalse();
        result.HttpStatusCode.Should().Be(408);
    }

    [Fact]
    public async Task DispatchAsync_ShouldUseDynamicAllowedMethods_AndBlockDisallowedMethodsBeforeNetwork()
    {
        var observedMethods = new List<HttpMethod>();
        var handler = new DelegateHttpMessageHandler((req, _) =>
        {
            observedMethods.Add(req.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        await using var ctx = CreateHardenedHost(handler, _ => [IPAddress.Parse("93.184.216.34")]);
        var dispatcher = ctx.Provider.GetRequiredService<IWebhookDispatcher>();

        var putRes = await dispatcher.DispatchAsync(
            "https://methods.tenant.test/api/resource",
            new Dictionary<string, object> { ["k"] = 1 },
            CancellationToken.None,
            dispatchOptions: new WebhookDispatchOptions(HttpMethod: "put"));
        var patchRes = await dispatcher.DispatchAsync(
            "https://methods.tenant.test/api/resource",
            new Dictionary<string, object> { ["k"] = 2 },
            CancellationToken.None,
            dispatchOptions: new WebhookDispatchOptions(HttpMethod: "PATCH"));
        var traceRes = await dispatcher.DispatchAsync(
            "https://methods.tenant.test/api/resource",
            new Dictionary<string, object> { ["k"] = 3 },
            CancellationToken.None,
            dispatchOptions: new WebhookDispatchOptions(HttpMethod: "TRACE"));
        var deleteRes = await dispatcher.DispatchAsync(
            "https://methods.tenant.test/api/resource",
            new Dictionary<string, object> { ["k"] = 4 },
            CancellationToken.None,
            dispatchOptions: new WebhookDispatchOptions(HttpMethod: "DELETE"));

        putRes.IsSuccess.Should().BeTrue();
        patchRes.IsSuccess.Should().BeTrue();
        observedMethods.Should().Equal(HttpMethod.Put, HttpMethod.Patch);

        traceRes.IsPermanentFailure.Should().BeTrue();
        deleteRes.IsPermanentFailure.Should().BeTrue();
    }

    [Fact]
    public async Task DispatchAsync_MultiTenantSigningAndAllowlistDefenseInDepth_ShouldEnforcePerTenantAuthAndOrigin()
    {
        using var customRsa = RSA.Create(2048);
        var secrets = new Dictionary<string, string?>
        {
            ["TenantSecrets:rsa-tenant-a"] = customRsa.ExportRSAPrivateKeyPem(),
            ["TenantSecrets:hmac-tenant-b"] = "secret-hmac-b-value",
            ["TenantSecrets:hmac-tenant-c"] = "secret-hmac-c-value"
        };

        var captured = new List<(string? Bearer, string? Timestamp, string? Signature, string? TenantHeader, string Body)>();
        var handler = new DelegateHttpMessageHandler(async (req, ct) =>
        {
            req.Headers.TryGetValues("X-Omro-Timestamp", out var tsVals);
            req.Headers.TryGetValues("X-Omro-Signature", out var sigVals);
            req.Headers.TryGetValues("X-Omro-Tenant-Id", out var tidVals);
            var body = await req.Content!.ReadAsStringAsync(ct);

            captured.Add((
                req.Headers.Authorization?.Parameter,
                tsVals?.FirstOrDefault(),
                sigVals?.FirstOrDefault(),
                tidVals?.FirstOrDefault(),
                body));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await using var ctx = CreateHardenedHost(handler, _ => [IPAddress.Parse("93.184.216.34")], secrets);

        var tenantA = await ctx.SeedTenantAsync(
            "tenant-a",
            "https://a.tenant.test",
            new TenantAuthProfile(TenantAuthScheme.Rs256Jwt, "omro-orchestrator", "api-a", "exec_a", "rsa-tenant-a", "kid-a", null));
        var tenantB = await ctx.SeedTenantAsync(
            "tenant-b",
            "https://b.tenant.test",
            new TenantAuthProfile(TenantAuthScheme.HmacSha256, null, "api-b", "omro_service", null, null, "hmac-tenant-b"));
        var tenantC = await ctx.SeedTenantAsync(
            "tenant-c",
            "https://c.tenant.test",
            new TenantAuthProfile(TenantAuthScheme.Rs256JwtAndHmac, "omro-orchestrator", "api-c", "exec_c", "default", null, "hmac-tenant-c"));

        var dispatcher = ctx.Provider.GetRequiredService<IWebhookDispatcher>();

        // Defense in depth: Tenant A cannot dispatch to Tenant B origin
        var crossOriginBlocked = await dispatcher.DispatchAsync(
            "https://b.tenant.test/api/hook",
            new Dictionary<string, object> { ["x"] = 1 },
            CancellationToken.None,
            dispatchOptions: new WebhookDispatchOptions(TenantId: tenantA));
        crossOriginBlocked.IsPermanentFailure.Should().BeTrue();
        crossOriginBlocked.ErrorMessage.Should().Contain("allowlist");
        captured.Should().BeEmpty();

        // Tenant A -> RS256 custom key
        (await dispatcher.DispatchAsync(
            "https://a.tenant.test/api/hook",
            new Dictionary<string, object> { ["tenant"] = "A" },
            CancellationToken.None,
            dispatchOptions: new WebhookDispatchOptions(TenantId: tenantA))).IsSuccess.Should().BeTrue();

        // Tenant B -> HMAC-SHA256 only
        (await dispatcher.DispatchAsync(
            "https://b.tenant.test/api/hook",
            new Dictionary<string, object> { ["tenant"] = "B" },
            CancellationToken.None,
            dispatchOptions: new WebhookDispatchOptions(TenantId: tenantB))).IsSuccess.Should().BeTrue();

        // Tenant C -> Both RS256 + HMAC
        (await dispatcher.DispatchAsync(
            "https://c.tenant.test/api/hook",
            new Dictionary<string, object> { ["tenant"] = "C" },
            CancellationToken.None,
            dispatchOptions: new WebhookDispatchOptions(TenantId: tenantC))).IsSuccess.Should().BeTrue();

        captured.Should().HaveCount(3);

        // Verify Tenant A
        var capA = captured[0];
        capA.Bearer.Should().NotBeNullOrWhiteSpace();
        capA.Signature.Should().BeNull();
        capA.TenantHeader.Should().Be(tenantA.ToString());
        var jwtHandler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        jwtHandler.ValidateToken(capA.Bearer!, new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new RsaSecurityKey(customRsa),
            ValidateIssuer = true,
            ValidIssuer = "omro-orchestrator",
            ValidateAudience = true,
            ValidAudience = "api-a",
            ValidateLifetime = true
        }, out _);

        // Verify Tenant B HMAC signature over wire body
        var capB = captured[1];
        capB.Bearer.Should().BeNull();
        capB.Timestamp.Should().NotBeNullOrWhiteSpace();
        capB.Signature.Should().NotBeNullOrWhiteSpace();
        var bodyHashB = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(capB.Body)));
        var canonicalB = $"{capB.Timestamp}.POST./api/hook.{bodyHashB}";
        var expectedB = $"v1={Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret-hmac-b-value"), Encoding.UTF8.GetBytes(canonicalB)))}";
        capB.Signature.Should().Be(expectedB);

        // Verify Tenant C has BOTH
        var capC = captured[2];
        capC.Bearer.Should().NotBeNullOrWhiteSpace();
        capC.Timestamp.Should().NotBeNullOrWhiteSpace();
        capC.Signature.Should().StartWith("v1=");
    }

    private static HardenedTestContext CreateHardenedHost(
        HttpMessageHandler primaryHandler,
        Func<string, IPAddress[]> dnsFunc,
        IReadOnlyDictionary<string, string?>? secrets = null)
    {
        var services = new ServiceCollection();
        var dbName = $"OmroEgressSec_{Guid.NewGuid()}";
        var inMemoryProvider = new ServiceCollection().AddEntityFrameworkInMemoryDatabase().BuildServiceProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(secrets ?? new Dictionary<string, string?>())
            .Build();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddDbContext<ApplicationDbContext>(o =>
            o.UseInMemoryDatabase(dbName).UseInternalServiceProvider(inMemoryProvider));
        services.AddMemoryCache();
        services.Configure<TenantCacheOptions>(_ => { });
        services.Configure<OmroServiceAuthOptions>(_ => { });
        services.Configure<EgressSecurityOptions>(_ => { });
        services.AddScoped<ITenantConfigRepository, TenantConfigRepository>();
        services.AddSingleton<ITenantConfigProvider, CachedTenantConfigProvider>();
        services.AddSingleton<IOmroTokenService, OmroTokenService>();
        services.AddSingleton<ISecretResolver, ConfigurationSecretResolver>();
        services.AddSingleton<ITenantAuthSigner, TenantAuthSigner>();
        services.AddSingleton<IDnsResolver>(new DelegateDnsResolver(dnsFunc));
        services.AddSingleton<ISsrfGuard, SsrfGuard>();
        services.AddLogging();

        services.AddResilientWebhookHttpClient(
            opt =>
            {
                opt.MaxRetryAttempts = 1;
                opt.RetryDelayMs = 10;
                opt.UseJitter = false;
                opt.AttemptTimeoutSeconds = 5;
                opt.SamplingDurationSeconds = 10;
                opt.MinimumThroughput = 3;
                opt.BreakDurationSeconds = 30;
            },
            builder => builder.ConfigurePrimaryHttpMessageHandler(() => primaryHandler));

        return new HardenedTestContext(services.BuildServiceProvider());
    }

    private sealed class HardenedTestContext : IAsyncDisposable
    {
        public ServiceProvider Provider { get; }

        public HardenedTestContext(ServiceProvider provider) => Provider = provider;

        public async Task<Guid> SeedTenantAsync(
            string slug,
            string allowedOrigin,
            TenantAuthProfile auth,
            int maxPayloadBytes = 65536,
            int egressTimeoutSeconds = 15)
        {
            var tenantId = Guid.NewGuid();
            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var tenant = TenantConfig.Create(
                tenantId: tenantId,
                tenantSlug: $"{slug}-{tenantId:N}"[..32],
                displayName: slug,
                basePromptContext: "Prompt",
                authAudience: auth.Audience,
                allowedEndpointHostsJson: JsonSerializer.Serialize(new[] { allowedOrigin }),
                authScheme: auth.Scheme,
                authIssuer: auth.Issuer,
                authRole: string.IsNullOrWhiteSpace(auth.Role) ? "omro_service" : auth.Role,
                signingKeyRef: auth.SigningKeyRef,
                signingKeyKid: auth.SigningKeyKid,
                hmacSecretRef: auth.HmacSecretRef,
                maxPayloadBytes: maxPayloadBytes,
                egressTimeoutSeconds: egressTimeoutSeconds);

            db.TenantConfigs.Add(tenant);
            await db.SaveChangesAsync();
            Provider.GetRequiredService<ITenantConfigProvider>().Invalidate(tenantId);
            return tenantId;
        }

        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }

    private sealed class DelegateDnsResolver : IDnsResolver
    {
        private readonly Func<string, IPAddress[]> _fn;
        public DelegateDnsResolver(Func<string, IPAddress[]> fn) => _fn = fn;
        public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default)
            => Task.FromResult(_fn(host));
    }

    private sealed class DelegateHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _fn;
        public DelegateHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _fn(request, cancellationToken);
    }

    private sealed class CountingPatternStream : Stream
    {
        private readonly long _length;
        private long _position;

        public long BytesConsumed { get; private set; }

        public CountingPatternStream(long length) => _length = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var remaining = _length - _position;
            if (remaining <= 0 || buffer.Length == 0)
                return 0;

            var toCopy = (int)Math.Min(buffer.Length, remaining);
            buffer[..toCopy].Fill((byte)'A');
            _position += toCopy;
            BytesConsumed += toCopy;
            return toCopy;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Read(buffer.Span));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
