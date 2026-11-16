using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Omro.Application.Interfaces;
using Omro.Infrastructure.Configuration;
using JwtRegisteredClaimNames = Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames;

namespace Omro.Infrastructure.Security;

public class OmroTokenService : IOmroTokenService, IDisposable
{
    private readonly OmroServiceAuthOptions _options;
    private readonly RSA _rsa;
    private readonly SigningCredentials _signingCredentials;

    public OmroTokenService(
        IOptions<OmroServiceAuthOptions> options,
        ILogger<OmroTokenService> logger,
        IConfiguration? configuration = null)
    {
        _options = options.Value;
        _rsa = RSA.Create(2048);

        var pem = ResolvePrivateKeyPem(_options, configuration);
        if (!string.IsNullOrWhiteSpace(pem))
        {
            _rsa.ImportFromPem(pem.Replace("\\n", "\n").ToCharArray());
        }
        else
        {
            logger.LogWarning(
                "No se configuró ServiceAuth:PrivateKeyContent/PrivateKeyPath ni AiService:PrivateKeyContent/PrivateKeyPath. " +
                "Se usará una clave RSA efímera en memoria para desarrollo/pruebas.");
        }

        var securityKey = new RsaSecurityKey(_rsa);
        _signingCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);
    }

    private static string? ResolvePrivateKeyPem(OmroServiceAuthOptions options, IConfiguration? configuration)
    {
        if (!string.IsNullOrWhiteSpace(options.PrivateKeyContent))
            return options.PrivateKeyContent;

        if (!string.IsNullOrWhiteSpace(options.PrivateKeyPath) && File.Exists(options.PrivateKeyPath))
            return File.ReadAllText(options.PrivateKeyPath);

        var aiPrivateKeyContent = configuration?["AiService:PrivateKeyContent"];
        if (!string.IsNullOrWhiteSpace(aiPrivateKeyContent))
            return aiPrivateKeyContent;

        var aiPrivateKeyPath = configuration?["AiService:PrivateKeyPath"];
        if (!string.IsNullOrWhiteSpace(aiPrivateKeyPath) && File.Exists(aiPrivateKeyPath))
            return File.ReadAllText(aiPrivateKeyPath);

        if (File.Exists("/etc/secrets/dotnet-private.pem"))
            return File.ReadAllText("/etc/secrets/dotnet-private.pem");

        if (File.Exists("dotnet-private.pem"))
            return File.ReadAllText("dotnet-private.pem");

        return null;
    }

    public string GenerateToken()
    {
        var claims = new[]
        {
            new Claim("role", string.IsNullOrWhiteSpace(_options.Role) ? "omro_service" : _options.Role),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = string.IsNullOrWhiteSpace(_options.Issuer) ? "omro-orchestrator" : _options.Issuer,
            Audience = string.IsNullOrWhiteSpace(_options.Audience) ? "medcareos-api" : _options.Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = _signingCredentials
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        tokenHandler.OutboundClaimTypeMap.Clear();

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    public void Dispose() => _rsa.Dispose();
}
