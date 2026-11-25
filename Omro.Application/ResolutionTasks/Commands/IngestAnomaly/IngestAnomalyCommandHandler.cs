using MediatR;
using Microsoft.Extensions.Logging;
using Omro.Application.Interfaces;
using Omro.Application.ResolutionTasks.Services;
using Omro.Domain.Repositories;
using Omro.Domain.ResolutionTasks;

namespace Omro.Application.ResolutionTasks.Commands.IngestAnomaly;

public class IngestAnomalyCommandHandler : IRequestHandler<IngestAnomalyCommand, Guid>
{
    private readonly IResolutionTaskRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICognitiveOrchestrator _orchestrator;
    private readonly IActionExecutionCoordinator _coordinator;
    private readonly ITenantConfigProvider _tenantConfigProvider;
    private readonly ILogger<IngestAnomalyCommandHandler> _logger;

    public IngestAnomalyCommandHandler(
        IResolutionTaskRepository repository,
        IUnitOfWork unitOfWork,
        ICognitiveOrchestrator orchestrator,
        IActionExecutionCoordinator coordinator,
        ITenantConfigProvider tenantConfigProvider,
        ILogger<IngestAnomalyCommandHandler> logger)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _orchestrator = orchestrator;
        _coordinator = coordinator;
        _tenantConfigProvider = tenantConfigProvider;
        _logger = logger;
    }

    public async Task<Guid> Handle(IngestAnomalyCommand request, CancellationToken cancellationToken)
    {
        var existingTask = await _repository.GetByKeyAsync(
            request.TenantId,
            request.ResourceId,
            request.AnomalyType,
            cancellationToken);

        if (existingTask != null)
        {
            if (existingTask.Status == ResolutionStatus.ActionProposed)
            {
                _logger.LogInformation(
                    "[OMRO RECOVERY] Reanudando tarea existente {TaskId} en estado ActionProposed sin re-invocar al LLM.",
                    existingTask.Id);

                await _coordinator.ExecutePhaseAsync(existingTask, ActionExecutionPhase.Immediate, cancellationToken);
                return existingTask.Id;
            }

            _logger.LogInformation(
                "[OMRO IDEMPOTENCIA] Anomalía duplicada ({TenantId}, {ResourceId}, {AnomalyType}). Retornando tarea existente {TaskId} en estado {Status}.",
                request.TenantId,
                request.ResourceId,
                request.AnomalyType,
                existingTask.Id,
                existingTask.Status);
            return existingTask.Id;
        }

        // rehidratacion 
        var anomalyEvent = new AnomalyEvent(
            RiskProbability: request.RiskProbability,
            ResourceId: request.ResourceId,
            AnomalyType: request.AnomalyType,
            CorrelationId: request.CorrelationId);

        var resolutionTask = new ResolutionTask(request.TenantId, anomalyEvent);

        // mutación de dominio
        resolutionTask.StartAnalysis();

        var tenantConfig = await _tenantConfigProvider.GetTenantConfigurationAsync(request.TenantId, cancellationToken);
        if (tenantConfig is null || !tenantConfig.IsActive)
        {
            var reason = tenantConfig is null
                ? $"Tenant '{request.TenantId}' no tiene configuración registrada en OMRO."
                : $"Tenant '{request.TenantId}' ({tenantConfig.TenantSlug}) está inactivo.";

            _logger.LogWarning(
                "[OMRO FAIL-CLOSED] Escalando anomalía {AnomalyType} para recurso {ResourceId} sin orquestación: {Reason}",
                request.AnomalyType,
                request.ResourceId,
                reason);

            resolutionTask.MarkNeedsHumanReview(reason);
            _repository.Add(resolutionTask);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return resolutionTask.Id;
        }

        string tenantPromptContext = tenantConfig.BuildOrchestratorPromptContext(request.AnomalyType);

        // razonamiento cognitivo: el modelo asume el control con contexto del tenant desde BD y atributos agnósticos del evento
        var (plan, promptTokens, completionTokens) = request.Attributes is { Count: > 0 }
            ? await _orchestrator.ProposeResolutionAsync(
                anomalyEvent,
                tenantPromptContext,
                request.Attributes,
                cancellationToken)
            : await _orchestrator.ProposeResolutionAsync(
                anomalyEvent,
                tenantPromptContext,
                cancellationToken);

        _logger.LogInformation(
            "Decisión cognitiva generada para recurso {ResourceId}. Title={Title}, IsFallback={IsFallback}, ExpectsExternalApproval={ExpectsApproval}, Actions={ActionCount}",
            request.ResourceId,
            plan.Title,
            plan.IsFallback,
            plan.ExpectsExternalApproval,
            plan.ProposedActions?.Count ?? 0);

        if (plan.IsFallback)
        {
            resolutionTask.MarkNeedsHumanReview(plan.Reasoning, plan);
        }
        else
        {
            resolutionTask.ProposePlan(plan, promptTokens, completionTokens);
        }

        // Transacción 1: persistir intención (ResolutionTask + ActionExecutions Pending) ANTES de despachar webhooks
        _repository.Add(resolutionTask);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        if (plan.IsFallback)
        {
            return resolutionTask.Id;
        }

        await _coordinator.ExecutePhaseAsync(resolutionTask, ActionExecutionPhase.Immediate, cancellationToken);

        return resolutionTask.Id;
    }
}
