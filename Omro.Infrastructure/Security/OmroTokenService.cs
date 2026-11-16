using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
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

    public OmroTokenService(IOptions<OmroServiceAuthOptions> options, ILogger<OmroTokenService> logger)
    {
        _options = options.Value;
        _rsa = RSA.Create(2048);

        if (!string.IsNullOrWhiteSpace(_options.PrivateKeyContent))
        {
            _rsa.ImportFromPem(_options.PrivateKeyContent.ToCharArray());
        }
        else if (!string.IsNullOrWhiteSpace(_options.PrivateKeyPath) && File.Exists(_options.PrivateKeyPath))
        {
            var pem = File.ReadAllText(_options.PrivateKeyPath);
            _rsa.ImportFromPem(pem.ToCharArray());
        }
        else
        {
            logger.LogWarning(
                "No se configuró ServiceAuth:PrivateKeyContent ni ServiceAuth:PrivateKeyPath. " +
                "Se usará una clave RSA efímera en memoria para desarrollo/pruebas.");
        }

        var securityKey = new RsaSecurityKey(_rsa);
        _signingCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);
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
