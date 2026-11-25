using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Omro.Application.Interfaces;
using Omro.Domain.Cognitive;
using Omro.Domain.Repositories;

namespace Omro.Infrastructure.CognitiveServices;

public sealed class EfLlmUsageRecorder : ILlmUsageRecorder
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EfLlmUsageRecorder> _logger;

    public EfLlmUsageRecorder(
        IServiceScopeFactory scopeFactory,
        ILogger<EfLlmUsageRecorder> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task RecordAsync(LlmUsageLog entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<ILlmUsageLogRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            repository.Add(entry);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "No se pudo persistir telemetría LlmUsageLog para tenant {TenantId}, proveedor {Provider}.",
                entry.TenantId,
                entry.ProviderName);
        }
    }

    public async Task<int> GetTenantDailyTokensAsync(
        Guid tenantId,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
            return 0;

        try
        {
            var startOfUtcDay = new DateTimeOffset(utcNow.UtcDateTime.Date, TimeSpan.Zero);
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<ILlmUsageLogRepository>();
            return await repository.GetTenantTokensUsedSinceAsync(tenantId, startOfUtcDay, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Error consultando consumo diario de tokens LLM para tenant {TenantId}. Se omite verificación de cuota.",
                tenantId);
            return 0;
        }
    }
}
