namespace Omro.Infrastructure.Configuration;

public class EgressSecurityOptions
{
    public const string SectionName = "EgressSecurity";

    public bool AllowInsecureHttp { get; set; } = false;
    public int DefaultMaxPayloadBytes { get; set; } = 65536;
    public int HardMaxPayloadBytes { get; set; } = 262144;
    public int MaxResponseBodyBytes { get; set; } = 4096;
    public int DefaultTimeoutSeconds { get; set; } = 15;
    public int MaxTimeoutSeconds { get; set; } = 120;
}
