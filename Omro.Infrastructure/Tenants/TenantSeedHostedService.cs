using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Omro.Application.Interfaces;
using Omro.Infrastructure.Configuration;

namespace Omro.Infrastructure.Tenants;

public class TenantSeedHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TenantSeedingOptions _options;
    private readonly ILogger<TenantSeedHostedService> _logger;

    public TenantSeedHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<TenantSeedingOptions> options,
        ILogger<TenantSeedHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.SeedOnStartup)
            return;

        var seedsDirectory = Path.IsPathRooted(_options.SeedsDirectory)
            ? _options.SeedsDirectory
            : Path.Combine(AppContext.BaseDirectory, _options.SeedsDirectory);

        using var scope = _scopeFactory.CreateScope();
        var loader = scope.ServiceProvider.GetRequiredService<ITenantSeedLoader>();
        var inserted = await loader.SeedFromDirectoryAsync(seedsDirectory, cancellationToken);

        _logger.LogInformation(
            "Seed de tenants completado al iniciar. Directorio='{SeedsDirectory}', TenantsInsertados={InsertedCount}.",
            seedsDirectory,
            inserted);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
