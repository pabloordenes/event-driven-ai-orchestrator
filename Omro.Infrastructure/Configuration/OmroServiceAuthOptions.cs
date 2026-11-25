namespace Omro.Infrastructure.Configuration;

public class OmroServiceAuthOptions
{
    public const string SectionName = "ServiceAuth";

    public string Issuer { get; set; } = "omro-orchestrator";
    public string Audience { get; set; } = string.Empty;
    public string Role { get; set; } = "omro_service";
    public string PrivateKeyPath { get; set; } = string.Empty;
    public string PrivateKeyContent { get; set; } = string.Empty;
}
