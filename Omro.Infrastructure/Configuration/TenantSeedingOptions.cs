namespace Omro.Infrastructure.Configuration;

public class TenantSeedingOptions
{
    public const string SectionName = "TenantSeeding";

    public bool SeedOnStartup { get; set; } = false;
    public string SeedsDirectory { get; set; } = "Seeds";
}
