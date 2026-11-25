namespace Omro.Infrastructure.Configuration;

public class TenantCacheOptions
{
    public const string SectionName = "TenantCache";

    public int AbsoluteExpirationSeconds { get; set; } = 300;
    public int SlidingExpirationSeconds { get; set; } = 120;
    public int NegativeCacheSeconds { get; set; } = 30;
}
