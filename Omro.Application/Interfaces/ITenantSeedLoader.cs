namespace Omro.Application.Interfaces;

public interface ITenantSeedLoader
{
    Task<int> SeedFromDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default);
    Task<bool> SeedFromJsonAsync(string jsonContent, CancellationToken cancellationToken = default);
}
