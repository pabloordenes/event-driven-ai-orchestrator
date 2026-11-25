using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Omro.Application.Interfaces;
using Omro.Application.Tenants;
using Omro.Domain.Tenants;
using Omro.Infrastructure.Configuration;
using Omro.Infrastructure.Security;

namespace Omro.Domain.Tests.Security;

public class TenantAuthSignerTests
{
    [Fact]
    public void Sign_Rs256JwtWithCustomKeyRefAndKid_ShouldProduceValidTenantScopedJwt()
    {
        using var customRsa = RSA.Create(2048);
        var privatePem = customRsa.ExportRSAPrivateKeyPem();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TenantSecrets:acme-rsa-key"] = privatePem
            })
            .Build();

        using var defaultTokenService = new OmroTokenService(
            Options.Create(new OmroServiceAuthOptions()),
            NullLogger<OmroTokenService>.Instance,
            config);

        var secretResolver = new ConfigurationSecretResolver(config);
        using var signer = new TenantAuthSigner(secretResolver, defaultTokenService);

        var tenantId = Guid.NewGuid();
        var tenant = CreateTenant(
            tenantId,
            new TenantAuthProfile(
                Scheme: TenantAuthScheme.Rs256Jwt,
                Issuer: "omro-orchestrator",
                Audience: "acme-api",
                Role: "acme_executor",
                SigningKeyRef: "acme-rsa-key",
                SigningKeyKid: "kid-v2",
                HmacSecretRef: null));

        var headers = signer.Sign(new TenantSigningContext(
            tenant,
            "POST",
            new Uri("https://acme.example.com/api/actions"),
            Encoding.UTF8.GetBytes("{\"ok\":true}"),
            DateTimeOffset.UtcNow));

        headers.BearerToken.Should().NotBeNullOrWhiteSpace();
        headers.HmacSignature.Should().BeNull();
        headers.HmacTimestamp.Should().BeNull();
        headers.TenantId.Should().Be(tenantId);

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var jwt = handler.ReadJwtToken(headers.BearerToken);
        jwt.Header.Kid.Should().Be("kid-v2");

        var principal = handler.ValidateToken(
            headers.BearerToken,
            new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new RsaSecurityKey(customRsa),
                ValidateIssuer = true,
                ValidIssuer = "omro-orchestrator",
                ValidateAudience = true,
                ValidAudience = "acme-api",
                ValidateLifetime = true
            },
            out _);

        principal.FindFirst("role")?.Value.Should().Be("acme_executor");
        principal.FindFirst("tid")?.Value.Should().Be(tenantId.ToString());
    }

    [Fact]
    public void Sign_HmacSha256_ShouldComputeExactCanonicalSignatureFromEnvSecret()
    {
        const string envVar = "OMRO_TEST_HMAC_SIGNER_SECRET";
        const string secretValue = "super-secret-tenant-hmac-key-2026";
        var prev = Environment.GetEnvironmentVariable(envVar);
        Environment.SetEnvironmentVariable(envVar, secretValue);

        try
        {
            using var signer = new TenantAuthSigner(new ConfigurationSecretResolver());
            var tenantId = Guid.NewGuid();
            var tenant = CreateTenant(
                tenantId,
                new TenantAuthProfile(
                    Scheme: TenantAuthScheme.HmacSha256,
                    Issuer: null,
                    Audience: string.Empty,
                    Role: string.Empty,
                    SigningKeyRef: null,
                    SigningKeyKid: null,
                    HmacSecretRef: $"env:{envVar}"));

            var timestamp = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
            var body = Encoding.UTF8.GetBytes("{\"shipmentId\":\"123\"}");
            var uri = new Uri("https://logistics.example.com/api/reroute?dryRun=false");

            var headers = signer.Sign(new TenantSigningContext(tenant, "put", uri, body, timestamp));

            headers.BearerToken.Should().BeNull();
            headers.HmacTimestamp.Should().Be(timestamp.ToUnixTimeSeconds().ToString());

            var bodyHash = Convert.ToHexStringLower(SHA256.HashData(body));
            var canonical = $"{timestamp.ToUnixTimeSeconds()}.PUT./api/reroute?dryRun=false.{bodyHash}";
            var expectedMac = $"v1={Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretValue), Encoding.UTF8.GetBytes(canonical)))}";

            headers.HmacSignature.Should().Be(expectedMac);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, prev);
        }
    }

    [Fact]
    public void Sign_Rs256JwtAndHmac_ShouldEmitBothBearerAndHmacHeaders()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TenantSecrets:dual-hmac"] = "dual-secret-value"
            })
            .Build();

        using var defaultTokenService = new OmroTokenService(
            Options.Create(new OmroServiceAuthOptions()),
            NullLogger<OmroTokenService>.Instance,
            config);
        using var signer = new TenantAuthSigner(new ConfigurationSecretResolver(config), defaultTokenService);

        var tenant = CreateTenant(
            Guid.NewGuid(),
            new TenantAuthProfile(
                Scheme: TenantAuthScheme.Rs256JwtAndHmac,
                Issuer: "omro-orchestrator",
                Audience: "dual-tenant-api",
                Role: "omro_service",
                SigningKeyRef: "default",
                SigningKeyKid: null,
                HmacSecretRef: "dual-hmac"));

        var headers = signer.Sign(new TenantSigningContext(
            tenant,
            "PATCH",
            new Uri("https://dual.example.com/api/resource"),
            Encoding.UTF8.GetBytes("{}"),
            DateTimeOffset.UtcNow));

        headers.BearerToken.Should().NotBeNullOrWhiteSpace();
        headers.HmacTimestamp.Should().NotBeNullOrWhiteSpace();
        headers.HmacSignature.Should().StartWith("v1=");
    }

    [Fact]
    public void Sign_AndSecretResolver_WhenSecretMissingOrPathTraversal_ShouldFailClosed()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "file-secret-value");

        try
        {
            var resolver = new ConfigurationSecretResolver();
            resolver.ResolveSecret($"file:{tempFile}").Should().Be("file-secret-value");
            resolver.ResolveSecret("file:../etc/passwd").Should().BeNull();
            resolver.ResolveSecret("default").Should().BeNull();

            using var defaultTokenService = new OmroTokenService(
                Options.Create(new OmroServiceAuthOptions()),
                NullLogger<OmroTokenService>.Instance);
            using var signer = new TenantAuthSigner(resolver, defaultTokenService);

            var missingAudTenant = CreateTenant(
                Guid.NewGuid(),
                new TenantAuthProfile(TenantAuthScheme.Rs256Jwt, "omro", "", "omro_service", "default", null, null));
            ((Action)(() => signer.Sign(new TenantSigningContext(
                missingAudTenant,
                "POST",
                new Uri("https://tenant.example/hook"),
                [],
                DateTimeOffset.UtcNow)))).Should().Throw<TenantAuthConfigurationException>();

            var missingKeyRefTenant = CreateTenant(
                Guid.NewGuid(),
                new TenantAuthProfile(TenantAuthScheme.Rs256Jwt, "omro", "api", "omro_service", "missing-key-ref", null, null));
            ((Action)(() => signer.Sign(new TenantSigningContext(
                missingKeyRefTenant,
                "POST",
                new Uri("https://tenant.example/hook"),
                [],
                DateTimeOffset.UtcNow)))).Should().Throw<TenantAuthConfigurationException>();

            var missingHmacTenant = CreateTenant(
                Guid.NewGuid(),
                new TenantAuthProfile(TenantAuthScheme.HmacSha256, null, "", "", null, null, "missing-hmac-ref"));
            ((Action)(() => signer.Sign(new TenantSigningContext(
                missingHmacTenant,
                "POST",
                new Uri("https://tenant.example/hook"),
                [],
                DateTimeOffset.UtcNow)))).Should().Throw<TenantAuthConfigurationException>();
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    private static TenantConfiguration CreateTenant(Guid tenantId, TenantAuthProfile auth) =>
        new(
            TenantId: tenantId,
            TenantSlug: "test-tenant",
            DisplayName: "Test Tenant",
            IsActive: true,
            BasePromptContext: "Prompt",
            AutonomousExecutionEnabled: false,
            FeatureFlags: new Dictionary<string, JsonElement>(),
            HitlProposalTtlMinutes: 180,
            HitlProposalMinLeadMinutes: 60,
            DedupWindowMinutes: 60,
            AllowedOrigins: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "https://tenant.example" },
            Auth: auth,
            MaxPayloadBytes: 65536,
            EgressTimeoutSeconds: 15,
            LlmSettings: new TenantLlmSettings(),
            ConfigVersion: 1,
            Actions: new Dictionary<string, TenantActionDescriptor>(),
            Policies: new Dictionary<string, TenantPolicyDescriptor>());
}
